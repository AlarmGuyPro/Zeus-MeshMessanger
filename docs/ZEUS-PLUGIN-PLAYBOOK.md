# Building a Zeus community feature: what we learned doing it

Hand this to Claude at the start of the next one. It is written from building
Mesh Messenger end to end — scaffold to a merged-ready catalog submission — and
it is mostly about the things that cost time, not the things that went well.

Everything here was true of the catalog in October 2026. The catalog is the
authority; where this disagrees with `CONTRIBUTING.md` in
`Zeus-SDR/zeus-community-features`, the guide wins. Check it rather than
trusting this.

---

## 1. The order that works

1. **Read the catalog's `CONTRIBUTING.md` first, all of it.** Its rules shape
   the repository layout, and retrofitting them later is expensive — see §3.
2. Scaffold from the template. Choose the reverse-DNS id once; it is permanent
   and appears everywhere.
3. Write the protocol or domain research down **before** writing code, in the
   repo. Any hardware protocol has rules that are not in its docs and are
   discoverable only from its source.
4. Build the backend with **fake devices**, not hardware. §6.
5. Build the panel. Screenshot it constantly. §5.
6. Get it onto real hardware as early as it is useful. The first contact with a
   real device will find things no test did, and those findings should go into
   an operator setup guide the same day, while you still remember why.
7. Package, run every gate locally, then submit. §7, §8.

The single biggest process win: **working software on real hardware in the
operator's hands, early and often**. Mesh Messenger went from 0.1.0 to 0.4.1 in
about two days of bench use, and almost every change came from the operator
looking at something and saying it was wrong. None of it came from planning.

## 2. What the catalog actually checks

Three automated gates, plus a human reading your source.

| Gate | What it does | How to pass |
|---|---|---|
| **Package validation** | Archive shape, manifest schema, path safety | `tools/validate-package.ps1` with every expected-metadata argument |
| **Package security scan** | Downloads `source.package`, verifies SHA-256 *before reading it*, ClamAV, static scan for undeclared network/file/process/native use, OSV against your lockfiles | Declare capabilities honestly; ship one assembly; pin dependencies |
| **Source rebuild** | Clones `source.repository` at `source.commit`, rebuilds from `zeus-build.json`, compares to the ZIP | §3 |

Run all of them locally before opening the PR. The list is in CONTRIBUTING §7
and takes about ten minutes. The rebuild check needs `bubblewrap` and
unprivileged user namespaces on Linux.

## 3. Repository decisions that the gates force

These are not style choices. Each one came from a failing check.

**One assembly.** Every extra DLL in the package is flagged for a human
reviewer. So shared code goes **inside** the plugin project, in a subfolder,
not in a sibling project linked in. Which leads directly to:

**No `..` in an MSBuild item path, and no `LinkBase`.** The rebuild check
rejects both. The obvious trick for compiling a sibling project's sources into
yours —

```xml
<Compile Include="../MyLib/**/*.cs" LinkBase="Lib" />   <!-- rejected -->
```

— fails the gate. Put the files under the project directory and let default
globbing find them. If you want a standalone library later, that is a folder
move.

**No tracked binaries.** The rebuild check fails on one, which means **test
build ZIPs cannot live on `main`**. Use a separate orphan-ish branch (we used
`builds`) with a README table of version, hash and what changed. Same for
screenshots: deliver them as files, never commit them.

**Keep the commit hash out of the assembly.** Put this in
`Directory.Build.props`:

```xml
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

Without it, `AssemblyInformationalVersion` embeds the git revision, the rebuild
from a snapshot produces a different value, and the comparison fails for a
reason that has nothing to do with your code.

**Pin everything.** `global.json` with the exact SDK, `packages.lock.json` via
`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`, and a
committed `package-lock.json` for the browser build. The OSV scan reads them.

**The ZIP is probably not byte-reproducible.** Ours wasn't: two builds of
identical source differed in 70 bytes of PE header timestamp and module version
id. The rebuild check compares assembly *metadata* and tolerates this, but the
registry pins a SHA-256 of the exact archive. So:

> **Publish the exact file you tested. Never rebuild before uploading.**

Keep that archive somewhere you can find it, and check its hash before the
release goes up. This is the single easiest way to waste an afternoon.

## 4. The manifest and capabilities

Ask for the least you can. Ours declares `NetworkAccess` and `PersistSettings`
and **no filesystem permission at all**, because settings live in the plugin
settings store and the code calls no `File` or `Directory` API. That is an easy
sentence to write in a PR and a hard one to argue with.

If you touch the network, **refuse public IP addresses in code**, not just in
policy, and say so in the PR. A validator that only accepts private ranges is
the kind of thing a reviewer can check in ten seconds.

Never: `[FromServices]`, `HttpContext.RequestServices`, environment variables,
reflection, `HostDataDirectory`, dynamic code loading. PureSignal is forbidden
outright, and a feature must never key a transmitter.

## 5. The UI contract, and how to satisfy it cheaply

Scope every selector under a feature-owned root class. Use **only** the public
token subset — no raw hex, rgb, hsl or named colours anywhere in feature CSS.
Use state colours semantically (`--tx` transmit only, `--amber` warning, `--ok`
healthy, `--accent` selection) and **never signal state by colour alone**; add
a word or a symbol.

Required, and all of it must be in screenshots on the PR: dark and light, normal
and narrow widths, 200% scaling, visible keyboard focus, and explicit loading,
empty, error, disconnected and unavailable states.

**Automate the screenshots.** Hand-taking twenty-one images and retaking them
every release does not survive contact with reality. What worked:

- A small host that runs the **real backend** with fake devices behind it and
  serves the built panel.
- A Playwright script that walks the tabs and writes numbered PNGs.
- Force the awkward states rather than waiting for them: intercept a request and
  delay it for the loading state, return a 500 for the error state, add a node
  pointing at a dead address for disconnected, delete every node at the end for
  the empty states.

That script produced the whole required set in about forty seconds, and the
screenshots repeatedly caught bugs that type-checking and tests did not —
including an edit that silently failed to apply and a stale bundle that made
the panel look unchanged.

**Two traps that look like success:**

- A build command that never ran because an earlier command in the same line
  failed. Confirm the `✓ built in …` line before believing a bundle is current.
- A scripted find-and-replace that matched nothing. Assert the match count
  rather than trusting the edit.

## 6. Testing without hardware

Write a fake device that speaks the real wire protocol over a loopback socket,
and point the real connector at it. Ours covers framing, contact lists,
channels, sends, acknowledgements, pushes and error codes. The whole suite runs
in about eight seconds with nothing plugged in, and it caught genuine protocol
bugs — a reserved slot being counted as a contact, a capacity reported as half
its real value.

Test what would hurt an operator if it regressed, in the operator's language:

> `MeshCoreResolvesAChannelSenderOnlyByNameAndSaysWhenItCannot`
> `CleanupRemovesRepeatersButNeverFavouritesOrRooms`

When hardware does arrive, the useful thing it adds is not correctness but
**reality**: full contact tables, clocks that are wrong, access points that
reject you, names with typos. Write each discovery into an operator guide
immediately.

## 7. Versioning and releasing

Bump the version in every place that carries it — ours was four files, which is
three too many; read it from one source if you can. Then:

1. Build the package.
2. Run every local gate.
3. Publish the **exact** archive as a GitHub Release asset on a versioned tag,
   at `https://github.com/<owner>/<repo>/releases/download/<tag>/<asset>.zip`
   with no query string.
4. Never replace bytes at a published URL. A change is a new version.

Tag the release at a commit that **rebuilds the package**, and verify that by
running the rebuild check against a clean clone of exactly that commit.

## 8. The catalog pull request

One file, `registry.json`, plus the `generated` timestamp. Two things that are
not obvious:

**Append your entry as the last element of `plugins`.** The catalog's custody
regression test reads the real `registry.json` and asserts that the *first*
`channel: community` entry is a grandfathered one carrying no `source` object.
Insert yours first and the test fails with "Fixture expects grandfathered
community versions without source", which tells you nothing about the real
problem. The catalog's own tooling refers to a new listing as `plugins[-1]`.

**`downloadUrl` points at Zeus-SDR and will 404.** That is correct. Your own
release URL goes in `source.package`. The HTTPS/checksum check downloads
`downloadUrl` and therefore fails until a maintainer mirrors the package into
custody; CONTRIBUTING says contributors are not expected to make it pass.
**Say so in a PR comment** so nobody has to work it out.

Attach the screenshots. The guide is explicit that they are required evidence
and that claiming the UI was tested is not a substitute.

If you script the registry edit on Windows, **normalise line endings on read**.
Git hands you a CRLF working copy, and a search for the end of the array using
LF finds nothing.

## 9. Documentation discipline

Write the operator help as the feature ships, not before. Ours described
channel management, a share-link import and a key-sharing QR code months before
any of it existed, and that had to be unwound at submission time. Help that
promises what the panel cannot do is worse than no help.

Write setup guides from the actual session that got hardware working, including
the wrong turns. The Meshtastic Wi-Fi guide is the most useful document in the
repository precisely because it records that the failure reason code says
whether the access point rejected the credentials or was never found, and that
a router answering ICMP for a stale lease will make an absent node look alive.

When the lesson turns out to be wrong later, fix it. Ours first blamed a
distant access point; a later dump showed the stored password differed by one
character from what we had been testing. The guide now leads with the password.

## 10. Habits that paid

- **Label every control with whether it transmits.** For a radio feature this
  is worth more than any amount of documentation, and reviewers notice.
- **Say what the software cannot do, in the software.** "Not in contacts"
  beats a button that silently fails.
- **Never guess an identity.** Where a protocol cannot tell you who sent
  something, say so and make the operator choose.
- **Confirm before anything irreversible**, with the count in the question:
  "Remove 37 contacts from KQ4WLR Zeus?"
- **Commit messages are the project's memory.** Write why, not what. When
  picking this up cold, `git log` was more useful than any status document.

## 11. What a maintainer review actually finds

Ours returned six findings on a feature that already passed every automated
gate. Three patterns in them are worth carrying forward, because they are not
specific to this feature:

**A guard with no failing test is a comment.** The best finding was a mutation
run: four changes to security-relevant code, all 32 tests still passing. The
cause was the test harness, which switched the guard off globally because the
simulated devices needed it off. Write the fakes so the suite runs with the
*shipped* defaults, and grant an exception per test. Then keep the mutation run
as a script in the repository and run it before every submission.

**The same rule applied in two places, enforced in one.** An index was
validated when a name was displayed and not when a transmitter was keyed. Look
for a check that exists on the read path and not on the write path; that is
where this kind of bug lives.

**Persistence that works until it doesn't.** Serialising everything into one
settings value is fine for a week of use and loses the lot on the day it
crosses the host's document limit — which deletes the old value before writing
the new one. Write per record, write only what changed, and bound the total on
purpose so nothing is ever lost by accident.

And one about documentation: help that describes unbuilt features is a review
finding, not a nicety. Ours described rooms, route tools and a health tab for
months. Deleting them was the fix, and the reference became easier to defend —
with the unshipped features gone, nothing in the panel transmitted on its own,
which is a much better sentence than a column of defaults.

## 12. Build the screenshot harness once, in the repository

§5 says to automate the screenshots. Go further: commit the harness. We built
it twice, from scratch, in two sessions, because it lived in a scratch
directory both times. It is a preview host that runs the real backend with the
fake devices behind it plus one Playwright script, it takes about an hour to
write and under a minute to run, and it immediately found a state that was
signalled by border colour alone.

Keep its dependencies outside the directory the catalog rebuilds, and keep its
output out of git — a tracked binary fails the rebuild check.

## 13. An effect must never return an expression

The worst bug of the project, and it is three characters wide:

```tsx
useEffect(() => el?.scrollTo({ top: 0 }), [id]);   // wrong
useEffect(() => { el?.scrollTo({ top: 0 }); }, [id]);  // right
```

React keeps whatever an effect returns and calls it later as the cleanup. `scrollTo` returns
undefined by specification, so the first line is harmless in a browser — and in the host's
webview, which ships a chainable scroll polyfill, it returns the element. React then called a
`<div>` and threw `d is not a function` from inside its own minified code, which took down
the whole Zeus workspace, not just the feature's panel.

Two lessons beyond the one line:

**Give every panel an error boundary, on day one.** A feature renders inside its host's React
tree, so an exception during render unmounts whatever tree it reaches. Without a boundary a
bug in a feature does not produce a broken panel, it takes the window — and the operator
loses their workspace to a feature they were only looking at. With one, they get a message,
a Try again, and a Copy the details button that hands over the stack. That button turned a
three-hour hunt into a four-minute fix.

**The browser you develop in is not the browser you ship in.** This reproduced on the bench
every time and in a plain Chromium never, because plain Chromium follows the specification.
The preview harness now renders under simulated host quirks
(`tests/preview/regression.mjs`), and that file is the right home for every future one.

One more, about the rebuild check itself: **run it against a freshly cloned tree every
time.** The check builds inside the directory you hand it, so a second run over the same
directory finds the first run's `obj/` and fails with "Committed build output directory is
not allowed" — a message about your repository, caused entirely by your own retry.

And a process note: trust the operator's description over your own reproduction. The report
was "clicking a channel crashes Zeus", which was exact. Two rounds were spent doubting it
because the logs showed a tidy restart and the panel was clean under test.
