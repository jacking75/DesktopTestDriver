"""Real loopback SSH/SFTP, fake remote OS: never starts a desktop or injects input."""
import base64
import copy
import importlib.util
import json
import logging
import os
from pathlib import Path
import re
import shutil
import socket
import tempfile
import threading
import time
import unittest

import paramiko
logging.getLogger("paramiko").setLevel(logging.CRITICAL)

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("ssh_transport", ROOT / "transport/ssh_transport.py")
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)


class RemoteFiles(paramiko.SFTPServerInterface):
    def __init__(self, server, *args, **kwargs):
        super().__init__(server, *args, **kwargs)
        self.lab = server.lab

    def local(self, path):
        path = path.replace("\\", "/").lstrip("/").replace(":", "")
        result = (self.lab.root / path).resolve()
        if self.lab.root.resolve() not in result.parents:
            raise ValueError("fake SFTP escape")
        return result

    def stat(self, path):
        try: return paramiko.SFTPAttributes.from_stat(self.local(path).stat())
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno)
    lstat = stat

    def list_folder(self, path):
        try:
            values = []
            for child in self.local(path).iterdir():
                entry = paramiko.SFTPAttributes.from_stat(child.stat()); entry.filename = child.name; values.append(entry)
            return values
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno)

    def mkdir(self, path, attr):
        try: self.local(path).mkdir(); return paramiko.SFTP_OK
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno)

    def open(self, path, flags, attr):
        try:
            descriptor = os.open(self.local(path), flags | getattr(os, "O_BINARY", 0), 0o600)
            mode = "r+b" if flags & os.O_RDWR else "wb" if flags & os.O_WRONLY else "rb"
            stream = os.fdopen(descriptor, mode)
            handle = paramiko.SFTPHandle(flags)
            if flags & (os.O_WRONLY | os.O_RDWR): handle.writefile = stream
            if not flags & os.O_WRONLY: handle.readfile = stream
            return handle
        except OSError as e: return paramiko.SFTPServer.convert_errno(e.errno)


class Server(paramiko.ServerInterface):
    def __init__(self, lab): self.lab = lab
    def check_auth_password(self, user, password):
        self.lab.auths += 1
        return paramiko.AUTH_SUCCESSFUL if user == "mock" and password == "fake-test-secret" else paramiko.AUTH_FAILED
    def get_allowed_auths(self, user): return "password"
    def check_channel_request(self, kind, channel): return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED
    def check_channel_exec_request(self, channel, command):
        threading.Thread(target=self.lab.execute, args=(channel, command), daemon=True).start()
        return True


class Lab:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory(prefix="DTD-mock-ssh-")
        self.root = Path(self.temp.name)
        self.key = paramiko.RSAKey.generate(2048)
        self.listener = socket.socket()
        for port in range(25150, 25199):
            try: self.listener.bind(("127.0.0.1", port)); break
            except OSError: continue
        else: raise RuntimeError("No development test port")
        self.listener.listen(5); self.listener.settimeout(.1)
        self.port = port; self.closed = False; self.transports = []; self.auths = 0; self.execs = []; self.commands = []
        self.lose_response = False; self.fail_hash = False
        self.thread = threading.Thread(target=self.accept, daemon=True); self.thread.start()

    def accept(self):
        while not self.closed:
            try: connection, _ = self.listener.accept()
            except socket.timeout: continue
            except OSError: break
            transport = paramiko.Transport(connection)
            self.transports.append(transport)
            transport.add_server_key(self.key)
            transport.set_subsystem_handler("sftp", paramiko.SFTPServer, RemoteFiles)
            try: transport.start_server(server=Server(self))
            except (EOFError, OSError, paramiko.SSHException): transport.close()

    def packet(self, operation="status", payload=None):
        return {"operation": operation, "runId": "run-1", "payload": payload, "machine": {
            "id": "vm", "installRoot": "C:/Driver", "consoleUser": "VM\\mock", "roles": ["host"],
            "ssh": {"host": "127.0.0.1", "port": self.port, "user": "mock", "hostKeyType": self.key.get_name(), "hostKeyBase64": self.key.get_base64(), "secretKind": "password-env", "secretReference": "DTD_TEST_SECRET"}}}

    def execute(self, channel, command):
        try:
            data = bytearray()
            while True:
                part = channel.recv(8192)
                if not part: break
                data.extend(part)
            encoded = command.decode("ascii").split("-EncodedCommand ",1)[1]
            script = base64.b64decode(encoded).decode("utf-16-le")
            self.execs.append(script)
            self.assert_scope(script)
            result = None
            if "'created'" in script:
                (self.root / "C/Driver/run-1").mkdir(parents=True)
                ownership = re.search(r"deployment-owner'\),'([a-f0-9]{32})'", script).group(1)
                (self.root / "C/Driver/run-1/deployment-owner").write_text(ownership)
                result = "created"
            elif " prepare " in script:
                if self.fail_hash: raise ValueError("injected hash failure")
                package = self.root / "C/Driver/run-1/package"
                manifest = json.loads((package / "package-manifest.json").read_text(encoding="utf-8-sig"))
                for item in manifest["files"]:
                    if adapter.digest(package / item["path"]) != item["sha256"]: raise ValueError("remote hash")
                result = json.dumps({"prepared": True, "ready": False, "manifestHash": adapter.digest(self.root / "C/Driver/run-1/config.json")})
            elif " send " in script:
                self.commands.append(json.loads(data))
                if self.lose_response: channel.close(); return
                result = json.dumps({"commandId": self.commands[-1]["commandId"], "accepted": True, "executed": True, "verified": False, "errorCode": "None"})
            elif " remove-task " in script: result = '{"removed":true}'
            elif '"rolledBack":true' in script:
                marker = self.root / "C/Driver/run-1/deployment-owner"
                expected = re.search(r"-ne '([a-f0-9]{32})'",script).group(1)
                if not marker.is_file() or marker.read_text() != expected: raise ValueError("rollback owner mismatch")
                shutil.rmtree(self.root / "C/Driver/run-1"); result = '{"rolledBack":true}'
            elif " stop " in script: result = '{"clean":true}'
            elif "Remove-Item -LiteralPath $root -Recurse -Force" in script:
                shutil.rmtree(self.root / "C/Driver/run-1"); result = '{"removed":true}'
            elif "'verified'" in script:
                receipt = json.loads((self.root / "C/Driver/run-1/deployment.json").read_text())
                for field in ("configHash","packageHash"):
                    expected = re.search(r"\$r\."+field+r" -ne '([a-f0-9]{64})'",script).group(1)
                    if receipt[field] != expected: raise ValueError("removal receipt mismatch")
                result = "verified"
            elif " status " in script: result = '{"mockStatus":true}'
            else: raise ValueError("Unknown mock operation")
            channel.sendall(result.encode("utf-8")); channel.send_exit_status(0)
        except Exception:
            channel.send_stderr(b"mock operation failed"); channel.send_exit_status(1)
        finally:
            channel.close()

    @staticmethod
    def assert_scope(script):
        if "$root=[IO.Path]::GetFullPath('C:/Driver/run-1')" not in script or "Reparse" not in script:
            raise ValueError("Missing remote path guard")

    def connect(self, packet): return adapter.Connection(packet["machine"]["ssh"], time.monotonic()+8)
    def close(self):
        self.closed = True; self.listener.close()
        for transport in self.transports: transport.close()
        self.thread.join(1); self.temp.cleanup()


class TransportTests(unittest.TestCase):
    def setUp(self):
        self.lab = Lab(); self.old = os.environ.get("DTD_TEST_SECRET"); os.environ["DTD_TEST_SECRET"] = "fake-test-secret"
        self.local = tempfile.TemporaryDirectory(prefix="DTD-package-")

    def tearDown(self):
        if self.old is None: os.environ.pop("DTD_TEST_SECRET",None)
        else: os.environ["DTD_TEST_SECRET"] = self.old
        self.lab.close(); self.local.cleanup()

    def package(self):
        root = Path(self.local.name); package = root / "package"; package.mkdir()
        files = []
        for project in ("Client", "Client.Broker", "Client.Watchdog"):
            directory = package / project; directory.mkdir(); path = directory / (project+".exe"); path.write_bytes(b"fake-executable-never-run")
            files.append({"path": f"{project}/{project}.exe", "sha256": adapter.digest(path), "size": path.stat().st_size})
        (package / "package-manifest.json").write_text(json.dumps({"version":"0.2.0","protocolVersion":2,"files":files}),encoding="utf-8")
        config = root / "config.json"; config.write_text(json.dumps({"runId":"run-1","machineId":"vm","role":"host"}),encoding="utf-8")
        return package, config

    def test_real_loopback_authentication_and_pinned_host_key(self):
        packet = self.lab.packet(); connection = self.lab.connect(packet)
        try: self.assertEqual(adapter.Adapter(packet,connection).run_operation(), {"mockStatus":True})
        finally: connection.close()
        self.assertEqual(self.lab.auths,1)

    def test_wrong_pin_is_rejected_before_authentication(self):
        packet = self.lab.packet(); packet["machine"]["ssh"]["hostKeyBase64"] = base64.b64encode(bytes(32)).decode()
        with self.assertRaises(ValueError): self.lab.connect(packet)
        self.assertEqual(self.lab.auths,0)

    def test_install_uses_real_sftp_checks_manifest_and_does_not_claim_ready(self):
        package, config = self.package()
        packet = self.lab.packet("install",{"packagePath":str(package),"configPath":str(config)})
        connection = self.lab.connect(packet)
        try:
            result = adapter.Adapter(packet,connection).run_operation()
            self.assertTrue(result["prepared"]); self.assertFalse(result["ready"])
            self.assertEqual((self.lab.root / "C/Driver/run-1/package/Client/Agent.exe").read_bytes(),b"fake-executable-never-run")
            self.assertTrue(any("Get-FileHash" in script for script in self.lab.execs))
        finally: connection.close()

    def test_local_hash_tamper_never_mutates_remote(self):
        package, config = self.package(); (package / "Client/Agent.exe").write_bytes(b"tampered")
        packet = self.lab.packet("install",{"packagePath":str(package),"configPath":str(config)})
        connection = self.lab.connect(packet)
        try:
            with self.assertRaises(ValueError): adapter.Adapter(packet,connection).run_operation()
            self.assertEqual(self.lab.execs,[])
        finally: connection.close()

    def test_remote_hash_failure_does_not_send_input(self):
        package, config = self.package(); self.lab.fail_hash = True
        packet = self.lab.packet("install",{"packagePath":str(package),"configPath":str(config)})
        connection = self.lab.connect(packet)
        try:
            with self.assertRaises(RuntimeError): adapter.Adapter(packet,connection).run_operation()
            self.assertEqual(self.lab.commands,[])
            self.assertFalse((self.lab.root / "C/Driver/run-1").exists(), "failed install was not rolled back")
        finally: connection.close()

    def test_lost_command_reply_never_retries(self):
        self.lab.lose_response = True
        packet = self.lab.packet("send",{"runId":"run-1","machineId":"vm","commandId":"once"})
        connection = self.lab.connect(packet)
        try:
            with self.assertRaises(RuntimeError): adapter.Adapter(packet,connection).run_operation()
            self.assertEqual(len(self.lab.commands),1)
        finally: connection.close()

    def test_path_scope_and_unknown_operations_are_rejected(self):
        packet = self.lab.packet("shell", {"command":"anything"})
        with self.assertRaises(ValueError): adapter.validate_packet(packet)
        for value in ("../secret", "C:/secret", "a\\b", "a/../b", "/absolute", "a//b", "Client./file", "NUL.txt"):
            with self.assertRaises(ValueError): adapter.relative(value)
        packet = self.lab.packet(); packet["machine"]["installRoot"] = "C:/"
        with self.assertRaises(ValueError): adapter.validate_packet(packet)
        with self.assertRaises(ValueError): adapter.decode('{"runId":1,"RUNID":2}')

    def test_install_collision_never_rolls_back_an_existing_tree(self):
        package, config = self.package()
        existing = self.lab.root / "C/Driver/run-1"; existing.mkdir(parents=True)
        (existing / "deployment-owner").write_text("another-owner")
        (existing / "preserve.txt").write_text("user-data")
        packet = self.lab.packet("install",{"packagePath":str(package),"configPath":str(config)})
        connection = self.lab.connect(packet)
        try:
            with self.assertRaises(RuntimeError): adapter.Adapter(packet,connection).run_operation()
            self.assertEqual((existing / "preserve.txt").read_text(),"user-data")
        finally: connection.close()

    def test_collection_excludes_secret_and_downloads_hash_bound_evidence(self):
        state = self.lab.root / "C/Driver/run-1/state"; state.mkdir(parents=True)
        (state / "secret").write_text("never-export")
        (state / "status.json").write_text('{"ready":true}')
        (state / "audit.jsonl").write_text('{"phase":"delegated"}\n')
        for name in ("fixture.json", "fixture-input.json", "marker.json"):
            (state / name).write_text('{}')
        (state / "unapproved.txt").write_text("never-export")
        output = Path(self.local.name) / "evidence"; output.mkdir()
        packet = self.lab.packet("collect",{"outputDirectory":str(output)})
        connection = self.lab.connect(packet)
        try:
            result = adapter.Adapter(packet,connection).run_operation()
            self.assertEqual({Path(x["path"]).name for x in result["files"]},{"status.json", "audit.jsonl", "fixture.json", "fixture-input.json", "marker.json"})
            for item in result["files"]:
                self.assertEqual(item["sha256"],adapter.digest(Path(item["path"])))
        finally: connection.close()

    def test_removal_checks_receipt_before_stop_and_preserves_neighbor(self):
        root = self.lab.root / "C/Driver/run-1"; root.mkdir(parents=True)
        neighbor = root.parent / "existing-user-data.txt"; neighbor.write_text("preserve")
        receipt = {"runId":"run-1","configHash":"a"*64,"packageHash":"b"*64}
        (root / "deployment.json").write_text(json.dumps(receipt))
        packet = self.lab.packet("remove",{"configHash":"c"*64,"packageHash":"b"*64})
        connection = self.lab.connect(packet)
        try:
            with self.assertRaises(RuntimeError): adapter.Adapter(packet,connection).run_operation()
            self.assertFalse(any(" stop " in script for script in self.lab.execs))
            self.assertTrue(root.exists())
            packet["payload"]["configHash"] = "a"*64
            self.assertTrue(adapter.Adapter(packet,connection).run_operation()["removed"])
            self.assertFalse(root.exists()); self.assertEqual(neighbor.read_text(),"preserve")
        finally: connection.close()

    def test_packaged_manifest_when_supplied(self):
        package = os.environ.get("DTD_TEST_PACKAGE")
        if not package: self.skipTest("set DTD_TEST_PACKAGE to verify a real local publish")
        root = Path(package); manifest = adapter.decode((root / "package-manifest.json").read_text(encoding="utf-8-sig"))
        self.assertEqual(manifest["version"],"0.2.0")
        for item in manifest["files"]:
            self.assertEqual(adapter.digest(root / adapter.relative(item["path"])),item["sha256"])
        self.assertTrue((root / "Client/Agent.exe").is_file())


if __name__ == "__main__":
    import sys
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
