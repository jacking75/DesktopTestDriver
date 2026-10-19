"""Build and exercise only the core source export; no adapter, product source or V/F plans exist here."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="DTD-core-only-") as temporary:
    target = Path(temporary)
    assert target.resolve().parent == Path(tempfile.gettempdir()).resolve() and target.name.startswith("DTD-core-only-")
    for name in ("DesktopTestDriver.slnx", "global.json", "Directory.Build.props"):
        shutil.copy2(root / name, target / name)
    def source_only(directory, names):
        return [n for n in names if n in {"bin", "obj", "__pycache__"}]
    for name in ("src", "tests/Driver.Tests", "fixtures/MarkerWindow"):
        shutil.copytree(root / name, target / name, ignore=source_only)
    shutil.copy2(root / "fixtures/generic-desktop.json", target / "fixtures/generic-desktop.json")
    assert not (target / "adapters").exists()
    flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    def run(arguments):
        result = subprocess.run(arguments, cwd=target, capture_output=True, timeout=90, creationflags=flags)
        if result.returncode:
            raise AssertionError(result.stdout.decode("utf-8", errors="replace")[-5000:] + result.stderr.decode("utf-8", errors="replace")[-2000:])
        return result.stdout
    run(["dotnet", "build", "DesktopTestDriver.slnx", "-c", "Release", "--nologo"])
    directory = target / "tests/Driver.Tests/bin/Release/net10.0-windows10.0.19041.0"
    deps = json.loads((directory / "Driver.Tests.deps.json").read_text(encoding="utf-8"))
    output = run([str(directory / "Driver.Tests.exe")]).decode("utf-8")
    summary = json.loads(output.strip().splitlines()[-1])
    assert summary["passed"] == 35 and summary["failed"] == 0 and not summary["realDesktopInput"]
    print(json.dumps({"coreOnlyBuild": True, "passed": summary["passed"], "optionalAdapterPresent": False, "productPresent": False, "realDesktopInput": False}))
