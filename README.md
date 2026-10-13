# Mesh Messenger builds

Test packages only. Source lives on `main`; these ZIPs are kept off `main`
because the Zeus catalog's source-rebuild check rejects archives in the
source tree.

| Version | Built from | Status |
|---------|-----------|--------|
| [v1.0.3](v1.0.3/io.github.alarmguypro.meshmessenger-1.0.3.zip) | main @ 8e21064 | **The one to submit.** Fixes a crash that took Zeus's workspace down when you opened a second channel, and adds a panel error boundary so a fault can never do that again |
| v1.0.2 | main @ 808ee14 | **Do not use.** Contains the channel crash; only adds the error boundary that found it |
| [v1.0.1](v1.0.1/io.github.alarmguypro.meshmessenger-1.0.1.zip) | main @ 21a07e2 | **Do not use.** Crashes Zeus when you open a second channel. Everything in 1.0.0 plus seven findings from a day on a busy mesh: hashtag case warning, newest-message-first, bulk contact removal that doesn't bury the node, and a control for what the node saves by itself |
| [v1.0.0](v1.0.0/io.github.alarmguypro.meshmessenger-1.0.0.zip) | main @ 986bbd9 | All six maintainer review findings answered; Logs tab, which records every message in and out; version shown in the panel |
| [v0.4.1](v0.4.1/io.github.alarmguypro.meshmessenger-0.4.1.zip) | main @ f4e26ca | **Listed in the Zeus community catalog** (PR #19 merged). Meshtastic node no longer reports a full contact list |
| [v0.4.0](v0.4.0/io.github.alarmguypro.meshmessenger-0.4.0.zip) | main @ 205655b | Message a channel sender, tidy contacts, join/leave MeshCore channels |
| [v0.3.1](v0.3.1/io.github.alarmguypro.meshmessenger-0.3.1.zip) | main | Fix 404 on a quiet joined channel |
| [v0.3.0](v0.3.0/io.github.alarmguypro.meshmessenger-0.3.0.zip) | main | Searchable pickers, folders, favourites |
| [v0.2.1](v0.2.1/io.github.alarmguypro.meshmessenger-0.2.1.zip) | main | Full contact list: warning, remove, recycle |
| [v0.2.0](v0.2.0/io.github.alarmguypro.meshmessenger-0.2.0.zip) | main @ 3ccbfe7 | Debug tab, retry/clear/delete, advert, search |
| [v0.1.1](v0.1.1/io.github.alarmguypro.meshmessenger-0.1.1.zip) | main @ fix for MeshCore manual-add contacts | First real-hardware fixes |
| [v0.1.0](v0.1.0/io.github.alarmguypro.meshmessenger-0.1.0.zip) | main @ 904552f | Pre-release, not yet tested on real nodes |

Install: Zeus → Features → Community → Install local feature → choose the ZIP.
Test steps: `docs/TESTING.md` on `main`.

All three catalog gates pass on v1.0.3: package validation, the security scan
(CLEAR, 0 fail, 0 review) and the source rebuild from a clean clone of the
pinned commit. 48/48 tests, and the four security mutations from the catalog
review each turn the suite red (`scripts/mutation-check.py` on `main`).

v1.0.0 was gated but never released: a day of bench use produced seven more
findings, and two builds must not answer to the same version now that the
panel shows it. Submit v1.0.1.
