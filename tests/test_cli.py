"""Published CLI integration: signing and a second-product simulation, no desktop or remote connection."""
import json
import os
from pathlib import Path
import subprocess
import sys
import uuid

root = Path(__file__).resolve().parents[1]
package = Path(os.environ["DTD_TEST_PACKAGE"])
server = package / "Server/Controller.exe"
work = root / "artifacts" / ("cli-" + uuid.uuid4().hex)
env = os.environ.copy()


def call(*args, ok=True):
    p = subprocess.run([str(server), *map(str, args)], capture_output=True, timeout=10, env=env,
                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    if (p.returncode == 0) != ok:
        raise AssertionError("CLI outcome: " + args[0] + " " + p.stderr.decode("utf-8", errors="replace"))
    return json.loads(p.stdout) if p.stdout else None


try:
    owner = call("owner-init", work, "smoke-owner")
    env["DTD_TRUST_STORE"] = str(work / "owners.json")
    plan = package / "scenarios/examples/counter.json"
    plan_info = call("plan-check", plan)
    desktop_plan = work / "desktop-plan.json"
    desktop_info = call("desktop-plan", "observer", desktop_plan)
    assert desktop_info["id"] == "desktop-fixture" and call("plan-check", desktop_plan)["planHash"] == desktop_info["planHash"]
    assert json.loads(desktop_plan.read_text(encoding="utf-8"))["adapter"] == "desktop-fixture"
    call("desktop-plan", "observer", desktop_plan, ok=False)
    manifest = json.loads((package / "package-manifest.json").read_text(encoding="utf-8-sig"))
    assert manifest["profile"] == "core"
    from datetime import datetime, timedelta, timezone
    config = json.loads((package / "inventory/run.template.json").read_text(encoding="utf-8"))
    config.update(runId="cli-smoke", machineId="mock-machine", role="observer",
                  expiresAt=(datetime.now(timezone.utc) + timedelta(minutes=5)).isoformat())
    config["delegation"].update(owner=owner["owner"], keyId="smoke-owner", requester="mock-requester",
                                  scenarioHashes={"counter-sample": plan_info["planHash"]})
    unsigned = work / "unsigned.json"
    unsigned.write_text(json.dumps(config), encoding="utf-8")
    signed = work / "signed.json"
    call("authorize", unsigned, work / "owner-private.pem", signed)
    report = call("simulate", plan, signed, work / "bundle")
    assert report["verdict"] == "Pass" and report["simulated"] and report["cleanupVerified"]
    assert "mock-requester" not in json.dumps(report) and "signature" not in json.dumps(report)
    call("bundle-check", work / "bundle")
    config = json.loads(signed.read_text(encoding="utf-8"))
    config["machineId"] = "tampered"
    signed.write_text(json.dumps(config), encoding="utf-8")
    call("simulate", plan, signed, work / "must-not-exist", ok=False)
    assert not (work / "must-not-exist").exists()
    print(json.dumps({"passed": 9, "simulation": True, "realDesktopInput": False, "remoteDeployment": False}))
finally:
    key = work / "owner-private.pem"
    if key.exists():
        key.unlink()
