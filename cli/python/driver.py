"""Thin JSON Lines CLI. Credentials are inherited by the transport, never parsed here."""
import argparse
import json
from pathlib import Path
import subprocess
import sys

p = argparse.ArgumentParser()
p.add_argument("--server", type=Path, required=True)
p.add_argument("arguments", nargs=argparse.REMAINDER)
a = p.parse_args()
try:
    payload = sys.stdin.buffer.read(1_048_577) if not sys.stdin.isatty() else b""
    if len(payload) > 1_048_576: raise ValueError("Message bound")
    result = subprocess.run([str(a.server.resolve(strict=True)), *a.arguments], input=payload, capture_output=True, timeout=130, check=False)
    if len(result.stdout) + len(result.stderr) > 1_048_576: raise ValueError("Output bound")
    sys.stdout.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    raise SystemExit(result.returncode)
except (OSError, ValueError, subprocess.TimeoutExpired):
    print(json.dumps({"errorCode": "TransportLost", "completionUnknown": True}), file=sys.stderr)
    raise SystemExit(1)
