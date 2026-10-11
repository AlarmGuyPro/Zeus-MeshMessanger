# Mesh Messenger builds

Test packages only. Source lives on `main`; these ZIPs are kept off `main`
because the Zeus catalog's source-rebuild check rejects archives in the
source tree.

| Version | Built from | Status |
|---------|-----------|--------|
| [v0.4.1](v0.4.1/io.github.alarmguypro.meshmessenger-0.4.1.zip) | main | Meshtastic node no longer reports a full contact list |
| [v0.4.0](v0.4.0/io.github.alarmguypro.meshmessenger-0.4.0.zip) | main @ 205655b | Message a channel sender, tidy contacts, join/leave MeshCore channels |
| [v0.3.1](v0.3.1/io.github.alarmguypro.meshmessenger-0.3.1.zip) | main | Fix 404 on a quiet joined channel |
| [v0.3.0](v0.3.0/io.github.alarmguypro.meshmessenger-0.3.0.zip) | main | Searchable pickers, folders, favourites |
| [v0.2.1](v0.2.1/io.github.alarmguypro.meshmessenger-0.2.1.zip) | main | Full contact list: warning, remove, recycle |
| [v0.2.0](v0.2.0/io.github.alarmguypro.meshmessenger-0.2.0.zip) | main @ 3ccbfe7 | Debug tab, retry/clear/delete, advert, search |
| [v0.1.1](v0.1.1/io.github.alarmguypro.meshmessenger-0.1.1.zip) | main @ fix for MeshCore manual-add contacts | First real-hardware fixes |
| [v0.1.0](v0.1.0/io.github.alarmguypro.meshmessenger-0.1.0.zip) | main @ 904552f | Pre-release, not yet tested on real nodes |

Install: Zeus → Features → Community → Install local feature → choose the ZIP.
Test steps: `docs/TESTING.md` on `main`.

All three catalog gates pass on v0.4.0: package validation, the security
scan (CLEAR, 0 fail, 0 review) and the source rebuild.
