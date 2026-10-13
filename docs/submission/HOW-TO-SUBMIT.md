# Submitting Mesh Messenger 1.0.0 to the Zeus community catalog

Mesh Messenger is **already in the catalog**: pull request #19 was merged and
`registry.json` lists the feature with version 0.4.1. So this round adds one
version to an entry that already exists, which is a smaller change than the
first submission — and it must leave the published 0.4.1 object exactly as it
is, because the catalog's custody test rejects any edit to a version it has
already published.

Everything that can be prepared in advance is prepared. What is left needs a
GitHub account acting as you: creating a release, forking the catalog, and
opening the pull request.

The exact values are in `REGISTRY-ENTRY.json`, the exact edit is in
`registry-1.0.0.patch`, and the pull request body is `PULL-REQUEST.md`. The
screenshots come as a separate ZIP and are **not** in this repository, because
the catalog's rebuild check fails on a tracked binary.

---

## The short version: two scripts

**Before either one, install the GitHub CLI and sign in.** This is the only
setup, and it is needed because creating a release, forking a repository and
pushing a branch all act as you:

```powershell
winget install --id GitHub.cli
```

Close and reopen PowerShell so the new command is found, then:

```powershell
gh auth login
```

Choose **GitHub.com**, **HTTPS**, and **Login with a web browser**.

Then, pointing at the ZIP wherever you saved it:

```powershell
pwsh ./submit-1-release.ps1 -ZipPath "$HOME\Downloads\io.github.alarmguypro.meshmessenger-1.0.0.zip"
pwsh ./submit-2-registry.ps1
```

The first checks the archive's hash and refuses to publish the wrong file, then
creates the release. The second forks the catalog (or reuses your existing
clone), branches from the **current** upstream main so the merged 0.4.1 entry
is there, adds the new version, checks that 0.4.1 survived the edit, runs the
catalog's own validator and custody test, shows you the diff, and pushes.

Both ask before doing anything that cannot be undone, and neither opens the
pull request — that last step needs the description and the screenshots, which
are yours to paste and attach.

If a script stops with a message, read it: each one says what to do rather than
just what failed.

## Upload the exact file, do not rebuild it

The hash in `REGISTRY-ENTRY.json` is over one specific archive:

```text
bbb345149e35fed650467243581e8e0f908f26d2d3321289cecdbe39742b9986
```

Rebuilding from the same commit produces an assembly that is **semantically
identical but not byte-identical** — the PE header timestamp and the module
version id differ — so the ZIP gets a different hash every time it is built.
The catalog's rebuild check compares assembly metadata and is happy with that,
but the security scan verifies the SHA-256 of the file it downloads before it
reads a single byte, and that one has to match exactly.

So: upload the archive delivered in chat, or the copy on the `builds` branch at
`v1.0.0/io.github.alarmguypro.meshmessenger-1.0.0.zip`. Do not run the
packaging script again first. If the hash of what you are about to upload is
not the string above, you have the wrong file.

## What the tag must point at

```text
986bbd9bb6273e4fbcb921110b5cb5adfab4d95e
```

That is the commit the catalog clones and rebuilds. `submit-1-release.ps1`
targets it for you.

## By hand, if you would rather

1. **Release.** Releases → Draft a new release, tag `v1.0.0`, target the commit
   above, attach the ZIP, publish. The URL must come out as
   `https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v1.0.0/io.github.alarmguypro.meshmessenger-1.0.0.zip`
   with no query string.
2. **Fork and branch** from the current upstream main:
   ```powershell
   gh repo fork Zeus-SDR/zeus-community-features --clone
   Set-Location zeus-community-features
   git fetch upstream main
   git switch -c community/io.github.alarmguypro.meshmessenger-1.0.0 upstream/main
   ```
   If `git remote -v` names the source remote something other than `upstream`,
   use the name it actually shows. Don't guess.
3. **Edit registry.json, and nothing else.** Append the object from
   `REGISTRY-ENTRY.json` to the **existing** entry's `versions` array, after
   the 0.4.1 object, and set the top-level `generated` field to the current UTC
   RFC 3339 timestamp. `registry-1.0.0.patch` is the exact diff. Leave every
   other entry, and the 0.4.1 object, untouched.
4. **Validate** before committing:
   ```powershell
   pwsh tools/validate-registry.ps1
   pwsh tools/test-community-custody.ps1
   ```
5. **Commit, push, open the pull request**, paste `PULL-REQUEST.md` as the
   body, and **attach the screenshots**. The guide is explicit that they are
   required review evidence and that saying the UI was tested is not a
   substitute.
6. **Watch the checks:**
   ```powershell
   gh pr checks --repo Zeus-SDR/zeus-community-features --watch
   ```

## What the checks will do

- **Package security scan** downloads `source.package`, verifies the SHA-256
  before reading the file, runs ClamAV and the static scanner, and checks the
  pinned lockfiles against the OSV database. Locally this is CLEAR, 0 fail and
  0 review.
- **Source rebuild matches package** clones the repository at the pinned commit
  and rebuilds from `zeus-build.json`, then compares the assembly metadata.
  This passes locally from a clean clone of the pinned commit.
- **HTTPS/checksum** downloads `downloadUrl`, which points at Zeus-SDR and will
  404 until a maintainer mirrors the package into custody. CONTRIBUTING says
  contributors are not expected to make that one pass. It is already explained
  in the pull request body.

Both scanners handle exactly one new version per pull request, so do not add a
second version to the same branch.

## If something needs changing

Push corrections to the same branch and answer each review conversation. If the
**package bytes** have to change, that is a new version, a new tag, a new URL
and a new hash — never an overwrite of the published release.
