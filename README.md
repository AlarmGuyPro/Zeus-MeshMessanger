# Mesh Messenger builds

Test packages only. Source lives on `main`; these ZIPs are kept off `main`
because the Zeus catalog's source-rebuild check rejects archives in the
source tree.

| Version | Built from | Status |
|---------|-----------|--------|
| [v0.2.0](v0.2.0/io.github.alarmguypro.meshmessenger-0.2.0.zip) | main @ 3ccbfe7 | Debug tab, retry/clear/delete, advert, search |
| [v0.1.1](v0.1.1/io.github.alarmguypro.meshmessenger-0.1.1.zip) | main @ fix for MeshCore manual-add contacts | First real-hardware fixes |
| [v0.1.0](v0.1.0/io.github.alarmguypro.meshmessenger-0.1.0.zip) | main @ 904552f | Pre-release, not yet tested on real nodes |

Install: Zeus → Features → Community → Install local feature → choose the ZIP.
Test steps: `docs/TESTING.md` on `main`.
