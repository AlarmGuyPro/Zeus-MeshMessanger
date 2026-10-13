#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Re-runs the maintainer's mutation run. Each mutation must turn the suite red."""
import pathlib, subprocess, sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
HV = ROOT / 'src/MeshMessenger/Mesh/Discovery/HostValidator.cs'
DS = ROOT / 'src/MeshMessenger/Mesh/Discovery/DiscoveryService.cs'
CB = ROOT / 'src/MeshMessenger/Mesh/Connectors/MeshConnectorBase.cs'
PR = ROOT / 'src/MeshMessenger/Mesh/Connectors/MeshCore/MeshCoreProtocol.cs'

MUTATIONS = [
    ("allow loopback", HV,
     "internal const bool ShippedAllowsLoopback = false;",
     "internal const bool ShippedAllowsLoopback = true;"),
    ("drop the address check when following a node", CB,
     "        if (!HostValidator.IsLocalHost(found))\n        {\n            return false;\n        }\n",
     ""),
    ("drop the local filter on re-find candidates", DS,
     "candidates.Distinct().Where(HostValidator.IsLocal);",
     "candidates.Distinct();"),
    ("remove the frame-size cap", PR,
     "if (length == 0 || length > MaxFrame) continue;",
     "if (length == 0) continue;"),
]

def run():
    p = subprocess.run(['dotnet', 'run', '--project', 'tests/MeshMessenger.Tests'],
                       cwd=ROOT, capture_output=True, text=True)
    tail = [l for l in p.stdout.splitlines() if 'passed in' in l]
    return p.returncode, (tail[-1] if tail else p.stdout[-300:] + p.stderr[-300:])

# A mutation left behind by an interrupted run looks exactly like a broken suite and
# wastes the next hour, so put every file back before starting, whatever happened last time.
for _, path, old, new in MUTATIONS:
    text = path.read_text()
    if text.count(new) == 1 and text.count(old) == 0:
        path.write_text(text.replace(new, old, 1))
        print(f"restored after an interrupted run: {path.name}")

code, summary = run()
print(f"baseline: {summary}")
if code != 0:
    sys.exit("the suite is not green before mutating")

# Restore on Ctrl-C or a kill as well as on the normal path: a half-applied mutation left
# in the tree is worse than no check at all.
import signal
_originals = {}

def _restore(*_):
    for path, text in _originals.items():
        path.write_text(text)
    sys.exit("interrupted; every file restored")

signal.signal(signal.SIGINT, _restore)
signal.signal(signal.SIGTERM, _restore)

survived = []
for name, path, old, new in MUTATIONS:
    text = path.read_text()
    if text.count(old) != 1:
        survived.append(f"{name} (pattern not found — the code moved)")
        continue
    _originals[path] = text
    path.write_text(text.replace(old, new, 1))
    try:
        code, summary = run()
    finally:
        path.write_text(text)
        _originals.pop(path, None)
    verdict = 'CAUGHT' if code != 0 else 'SURVIVED'
    print(f"{verdict}: {name} — {summary}")
    if code == 0:
        survived.append(name)

print()
if survived:
    print("Mutations that survived:")
    for s in survived:
        print(f"  - {s}")
    sys.exit(1)
print("All four mutations turn the suite red.")
