"""Pinned SSH/SFTP adapter. One JSON packet on stdin; no shell-command operation."""
from __future__ import annotations

import base64
import hashlib
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import socket
import sys
import threading
import time
import uuid

MAX_MESSAGE = 1_048_576


def unique_object(pairs):
    result = {}
    names = set()
    for name, value in pairs:
        if name.lower() in names:
            raise ValueError("Duplicate property")
        names.add(name.lower())
        result[name] = value
    return result


def decode(data):
    if len(data) > MAX_MESSAGE:
        raise ValueError("Message bound")
    return json.loads(data, object_pairs_hook=unique_object)


def digest(path):
    with open(path, "rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def relative(path):
    p = PurePosixPath(path)
    if not isinstance(path, str) or "\\" in path or ":" in path or p.is_absolute() or any(x in ("", ".", "..") for x in path.split("/")):
        raise ValueError("Package path")
    if not re.fullmatch(r"[A-Za-z0-9_./-]+", path):
        raise ValueError("Package path")
    for part in path.split("/"):
        if part.endswith(".") or part.split(".",1)[0].upper() in {"CON","PRN","AUX","NUL",*(f"COM{i}" for i in range(1,10)),*(f"LPT{i}" for i in range(1,10))}:
            raise ValueError("Windows package alias")
    return str(p)


def quote(value):
    return "'" + value.replace("'", "''") + "'"


def validate_packet(packet):
    if set(packet) != {"operation", "machine", "runId", "payload"} or packet["operation"] not in {"install", "install-agent", "remove-agent", "status", "send", "stop", "remove", "collect"}:
        raise ValueError("Operation")
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,80}", packet["runId"]):
        raise ValueError("Run ID")
    m = packet["machine"]
    root = m["installRoot"]
    if not re.fullmatch(r"[A-Za-z]:[\\/][A-Za-z0-9 _./\\-]+", root) or ".." in re.split(r"[\\/]", root) or str(PureWindowsPath(root)) == PureWindowsPath(root).anchor:
        raise ValueError("Root")
    ssh = m["ssh"]
    if not re.fullmatch(r"[A-Za-z0-9.:-]{1,253}", ssh["host"]) or not 1 <= ssh["port"] <= 65535 or ssh["secretKind"] not in {"password-env", "keyfile-env"}:
        raise ValueError("SSH settings")
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,127}", ssh["secretReference"]):
        raise ValueError("Secret reference")
    if len(base64.b64decode(ssh["hostKeyBase64"], validate=True)) < 32:
        raise ValueError("Host key")


class Connection:
    def __init__(self, settings, deadline):
        import paramiko
        self.deadline = deadline
        self.transport = None
        connection = socket.create_connection((settings["host"], settings["port"]), timeout=self.remaining(5))
        self.transport = paramiko.Transport(connection)
        self.transport.auth_timeout = self.remaining(5)
        self.transport.banner_timeout = self.remaining(5)
        self.timer = threading.Timer(self.remaining(120), self.transport.close)
        self.timer.daemon = True
        self.timer.start()
        try:
            self.transport.start_client(timeout=self.remaining(5))
            key = self.transport.get_remote_server_key()
            # Verify before accessing any credential provider.
            if key.get_name() != settings["hostKeyType"] or key.get_base64() != settings["hostKeyBase64"]:
                raise ValueError("Host key mismatch")
            secret = os.environ.get(settings["secretReference"])
            if not secret:
                raise ValueError("Missing credential provider value")
            if settings["secretKind"] == "password-env":
                self.transport.auth_password(settings["user"], secret, fallback=False)
            else:
                private = paramiko.PKey.from_path(secret)
                self.transport.auth_publickey(settings["user"], private)
            secret = None
        except BaseException:
            self.timer.cancel()
            self.transport.close()
            raise

    def remaining(self, bound=5):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("Transport deadline")
        return min(bound, remaining)

    def ps(self, script, stdin=None):
        prefix = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::InputEncoding=[Text.UTF8Encoding]::new(); [Console]::OutputEncoding=[Text.UTF8Encoding]::new();\n"
        encoded = base64.b64encode((prefix + script).encode("utf-16-le")).decode("ascii")
        if len(encoded) > 30000:
            raise ValueError("Remote command bound")
        channel = self.transport.open_session(timeout=self.remaining())
        channel.settimeout(self.remaining())
        try:
            channel.exec_command("powershell.exe -NoProfile -NonInteractive -EncodedCommand " + encoded)
            if stdin is not None:
                channel.sendall(stdin.encode("utf-8"))
            channel.shutdown_write()
            out, err = bytearray(), bytearray()
            while True:
                self.remaining()
                if channel.recv_ready(): out.extend(channel.recv(8192))
                if channel.recv_stderr_ready(): err.extend(channel.recv_stderr(8192))
                if len(out) + len(err) > MAX_MESSAGE:
                    raise ValueError("Remote output bound")
                if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready(): break
                time.sleep(.01)
            if channel.recv_exit_status() != 0:
                raise RuntimeError("Remote operation failed; completion unknown")
            return out.decode("utf-8-sig").strip()
        finally:
            channel.close()

    def sftp(self):
        import paramiko
        sftp = paramiko.SFTPClient.from_transport(self.transport)
        sftp.get_channel().settimeout(self.remaining())
        return sftp

    def close(self):
        self.timer.cancel()
        if self.transport:
            self.transport.close()


class Adapter:
    def __init__(self, packet, connection):
        validate_packet(packet)
        self.packet, self.connection = packet, connection
        self.run = packet["runId"]
        self.root = packet["machine"]["installRoot"].replace("\\", "/").rstrip("/") + "/" + self.run
        self.state = self.root + "/state"
        self.broker = self.root + "/package/Client.Broker/Client.Broker.exe"

    def checked_root(self, must_exist=True):
        # Verify resolved absolute target and every ancestor before removal or mutation.
        root = quote(self.root)
        expected_parent = quote(self.packet["machine"]["installRoot"].replace("\\", "/").rstrip("/"))
        return ("$root=[IO.Path]::GetFullPath(" + root + "); $parent=[IO.Path]::GetFullPath(" + expected_parent + "); "
                "if([IO.Path]::GetDirectoryName($root).TrimEnd('\\') -ne $parent.TrimEnd('\\')){throw 'Scope'}; "
                "$d=$root; while($d){if(Test-Path -LiteralPath $d){$i=Get-Item -LiteralPath $d; if($i.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse'}}; $d=[IO.Path]::GetDirectoryName($d)}; "
                + ("if(!(Test-Path -LiteralPath $root -PathType Container)){throw 'Missing'}; " if must_exist else ""))

    def broker_call(self, operation, stdin=None):
        if operation not in {"status", "send", "stop", "remove-task", "rollback"}:
            raise ValueError("Broker operation")
        script = self.checked_root() + "& " + quote(self.broker) + " " + operation + " " + quote(self.state) + "; if($LASTEXITCODE -ne 0){exit 1}"
        return decode(self.connection.ps(script, stdin))

    def install(self):
        self.ownership = uuid.uuid4().hex
        self.create_attempted = False
        try:
            return self._install()
        except Exception:
            if not self.create_attempted:
                raise
            # This is cleanup of a failed preparation, never a re-install or an input retry.
            # Even a lost response may only remove a tree bearing this request's random ownership marker.
            try:
                script = self.checked_root() + "$marker=Join-Path $root 'deployment-owner'; "
                script += "if(!(Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw) -ne " + quote(self.ownership) + "){throw 'Ownership'}; "
                rollback = "agent-rollback" if self.packet["operation"] == "install-agent" else "rollback"
                script += "if(Test-Path -LiteralPath " + quote(self.state) + "){ & " + quote(self.broker) + " " + rollback + " " + quote(self.state) + "; if($LASTEXITCODE -ne 0){exit 1} }; "
                script += "if(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Reparse'}; "
                script += "Remove-Item -LiteralPath $root -Recurse -Force; '{\"rolledBack\":true}'"
                self.connection.ps(script)
            except Exception:
                pass  # Caller receives failure/completionUnknown and must retain the reservation.
            raise

    def _install(self):
        payload = self.packet["payload"]
        if set(payload) not in ({"packagePath", "configPath"}, {"packagePath", "configPath", "connectionPath"}): raise ValueError("Install payload")
        package = Path(payload["packagePath"]).resolve(strict=True)
        manifest_path = package / "package-manifest.json"
        manifest = decode(manifest_path.read_text(encoding="utf-8-sig"))
        if manifest["version"] != "0.2.0" or manifest["protocolVersion"] != 2 or not 1 <= len(manifest["files"]) <= 256:
            raise ValueError("Package version")
        files, size = [], 0
        for file in manifest["files"]:
            rel = relative(file["path"])
            path = package / rel
            if not path.is_file() or path.is_symlink() or package not in path.resolve().parents or digest(path) != file["sha256"]:
                raise ValueError("Package hash")
            size += path.stat().st_size
            files.append((rel, path, file["sha256"]))
        if size > 200_000_000 or len({p.lower() for p, _, _ in files}) != len(files): raise ValueError("Package bound")
        required = {"Client/Agent.exe", "Client.Broker/Client.Broker.exe", "Client.Watchdog/Client.Watchdog.exe"}
        if not required.issubset({p for p, _, _ in files}): raise ValueError("Package executables")
        config_path = Path(payload["configPath"]).resolve(strict=True)
        config = decode(config_path.read_text(encoding="utf-8-sig"))
        agent_mode = self.packet["operation"] == "install-agent"
        if agent_mode:
            if set(payload) != {"packagePath", "configPath"} or config["machineId"] != self.packet["machine"]["id"] or config["stateRoot"].replace("\\", "/").lower() != self.state.lower():
                raise ValueError("Agent binding")
        elif config["runId"] != self.run or config["machineId"] != self.packet["machine"]["id"] or config["role"] not in self.packet["machine"]["roles"]:
            raise ValueError("Config binding")
        config_hash = digest(config_path)
        create = self.checked_root(False) + "if(Test-Path -LiteralPath $root){throw 'Conflict'}; if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'Parent missing'}; "
        create += "$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User; $acl=[Security.AccessControl.DirectorySecurity]::new(); $acl.SetOwner($sid); $acl.SetAccessRuleProtection($true,$false); "
        create += "$rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'); $acl.AddAccessRule($rule); "
        create += "$i=[IO.DirectoryInfo]::new($root); $i.Create($acl); [IO.File]::WriteAllText((Join-Path $root 'deployment-owner')," + quote(self.ownership) + "); 'created'"
        self.create_attempted = True
        self.connection.ps(create)
        # Every file is under the newly owned tree; a preparation failure rolls it back when reachable.
        with self.connection.sftp() as sftp:
            created = {self.root}
            def directory(path):
                if path in created: return
                directory(path.rsplit("/", 1)[0])
                sftp.mkdir(path)
                created.add(path)
            for rel, path, _ in files + [("package-manifest.json", manifest_path, digest(manifest_path))]:
                target = self.root + "/package/" + rel
                directory(target.rsplit("/", 1)[0])
                self.connection.remaining()
                sftp.put(str(path), target, callback=lambda transferred, total: self.connection.remaining(), confirm=True)
            sftp.put(str(config_path), self.root + "/config.json", callback=lambda transferred, total: self.connection.remaining(), confirm=True)
            if "connectionPath" in payload:
                connection_path = Path(payload["connectionPath"]).resolve(strict=True)
                connection = decode(connection_path.read_text(encoding="utf-8-sig"))
                if connection["runId"] != self.run or connection["manifestHash"] != config_hash or connection["machineId"] != config["machineId"]:
                    raise ValueError("Controller binding")
                sftp.put(str(connection_path), self.root + "/controller.json", callback=lambda transferred, total: self.connection.remaining(), confirm=True)
            receipt = {"runId": self.run, "machineId": self.packet["machine"]["id"], "configHash": config_hash, "packageHash": digest(manifest_path)}
            with sftp.open(self.root + "/deployment.json", "wx") as stream:
                stream.write(json.dumps(receipt).encode("utf-8"))
        verify = self.checked_root() + "$m=Get-Content -LiteralPath " + quote(self.root + "/package/package-manifest.json") + " -Raw | ConvertFrom-Json; "
        verify += "foreach($f in $m.files){$p=Join-Path " + quote(self.root + "/package") + " $f.path; if((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant() -ne $f.sha256){throw 'Hash'}}; "
        verify += "if((Get-FileHash -LiteralPath " + quote(self.root + "/config.json") + " -Algorithm SHA256).Hash.ToLowerInvariant() -ne " + quote(config_hash) + "){throw 'Config hash'}; "
        verify += "$who=[Security.Principal.WindowsIdentity]::GetCurrent().Name; if($who -ne " + quote(self.packet["machine"]["consoleUser"]) + "){throw 'Console account mismatch'}; "
        prepare = "agent-prepare" if agent_mode else "prepare"
        verify += "& " + quote(self.broker) + " " + prepare + " " + quote(self.state) + " " + quote(self.root + "/config.json") + "; if($LASTEXITCODE -ne 0){exit 1}"
        return decode(self.connection.ps(verify))

    def remove(self):
        payload = self.packet["payload"]
        if not isinstance(payload, dict) or set(payload) != {"configHash", "packageHash"} or any(not re.fullmatch(r"[a-f0-9]{64}", h) for h in payload.values()):
            raise ValueError("Removal binding")
        script = self.checked_root() + "$r=Get-Content -LiteralPath " + quote(self.root + "/deployment.json") + " -Raw | ConvertFrom-Json; "
        script += "if($r.runId -ne " + quote(self.run) + " -or $r.configHash -ne " + quote(payload["configHash"]) + " -or $r.packageHash -ne " + quote(payload["packageHash"]) + "){throw 'Receipt mismatch'}; "
        self.connection.ps(script + "'verified'")
        if not self.broker_call("stop")["clean"]: raise RuntimeError("Cleanup incomplete")
        self.broker_call("remove-task")
        script += "$c=Get-Content -LiteralPath " + quote(self.state + "/cleanup.json") + " -Raw | ConvertFrom-Json; if(!$c.clean -or !$c.normalDesktop){throw 'Recovery incomplete'}; "
        script += "$s=Get-Content -LiteralPath " + quote(self.state + "/status.json") + " -Raw | ConvertFrom-Json; foreach($id in @($s.client,$s.watchdog)){ $p=Get-Process -Id $id.pid -ErrorAction SilentlyContinue; if($p){throw 'Owned process still running'}}; "
        # Verify no junction was planted anywhere under the owned tree before recursive delete.
        script += "if(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Reparse'}; "
        script += "Remove-Item -LiteralPath $root -Recurse -Force; '{\"removed\":true}'"
        return decode(self.connection.ps(script))

    def collect(self):
        payload = self.packet["payload"]
        if not isinstance(payload, dict) or set(payload) != {"outputDirectory"}: raise ValueError("Collection payload")
        output = Path(payload["outputDirectory"]).resolve(strict=True)
        if not output.is_dir() or output.is_symlink() or any(output.iterdir()): raise ValueError("New empty private output required")
        collected = []
        with self.connection.sftp() as sftp:
            for name in sftp.listdir(self.state):
                if name == "secret" or not re.fullmatch(r"(?:audit\.jsonl|frame-[a-f0-9]{32}\.png|(?:status|cleanup|client-cleanup|run|ledger|stop|marker|fixture-input|fixture)\.json|command-[A-Za-z0-9_-]{1,80}\.json)", name): continue
                self.connection.remaining()
                remote = self.state + "/" + name
                import stat
                attributes = sftp.lstat(remote)
                if not stat.S_ISREG(attributes.st_mode) or attributes.st_size > 64_000_000: raise ValueError("Evidence bound")
                path = output / name
                sftp.get(remote, str(path), callback=lambda transferred, total: self.connection.remaining())
                collected.append({"path": str(path), "sha256": digest(path)})
        return {"files": collected}

    def remove_agent(self):
        payload = self.packet["payload"]
        if not isinstance(payload, dict) or set(payload) != {"configHash", "packageHash"} or any(not re.fullmatch(r"[a-f0-9]{64}", h) for h in payload.values()):
            raise ValueError("Removal binding")
        script = self.checked_root() + "$r=Get-Content -LiteralPath " + quote(self.root + "/deployment.json") + " -Raw | ConvertFrom-Json; "
        script += "if($r.runId -ne " + quote(self.run) + " -or $r.configHash -ne " + quote(payload["configHash"]) + " -or $r.packageHash -ne " + quote(payload["packageHash"]) + "){throw 'Receipt mismatch'}; "
        script += "$s=Get-Content -LiteralPath " + quote(self.state + "/agent-status.json") + " -Raw | ConvertFrom-Json; "
        script += "if($s.activeRun -or $s.cleanupBlocked){throw 'Cleanup required'}; if(Get-Process -Id $s.agent.pid -ErrorAction SilentlyContinue){throw 'Stop agent through controller first'}; "
        script += "& " + quote(self.broker) + " agent-rollback " + quote(self.state) + "; if($LASTEXITCODE -ne 0){exit 1}; "
        script += "if(Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }){throw 'Reparse'}; "
        script += "Remove-Item -LiteralPath $root -Recurse -Force; '{\"removed\":true}'"
        return decode(self.connection.ps(script))

    def run_operation(self):
        operation = self.packet["operation"]
        if operation in {"install", "install-agent"}: return self.install()
        if operation == "remove": return self.remove()
        if operation == "remove-agent": return self.remove_agent()
        if operation == "collect": return self.collect()
        if operation == "send":
            command = self.packet["payload"]
            if command["runId"] != self.run or command["machineId"] != self.packet["machine"]["id"]: raise ValueError("Command binding")
            return self.broker_call("send", json.dumps(command, separators=(",", ":")) + "\n")
        return self.broker_call(operation)


def main():
    connection = None
    try:
        line = sys.stdin.buffer.readline(MAX_MESSAGE + 1)
        packet = decode(line)
        validate_packet(packet)
        connection = Connection(packet["machine"]["ssh"], time.monotonic() + (115 if packet["operation"] in {"install", "install-agent"} else 8))
        result = Adapter(packet, connection).run_operation()
        print(json.dumps(result, ensure_ascii=True, separators=(",", ":")))
        return 0
    except Exception:
        # Never echo Paramiko/OS exception strings; they may contain credential or private paths.
        print('{"errorCode":"TransportLost","completionUnknown":true}', file=sys.stderr)
        return 1
    finally:
        if connection: connection.close()


if __name__ == "__main__":
    raise SystemExit(main())
