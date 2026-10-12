# Submitting Mesh Messenger 0.4.1 to the Zeus community catalog

Everything that can be prepared in advance is prepared. What is left needs a
GitHub account acting as you: creating a release, forking the catalog, and
opening the pull request.

The exact values to use are in `REGISTRY-ENTRY.json`; the pull request body is
`PULL-REQUEST.md`; the screenshots came as a separate ZIP and are **not** in
this repository, because the catalog's rebuild check fails on a tracked binary.

---

## Upload the exact file, do not rebuild it

The hash in `REGISTRY-ENTRY.json` is over one specific archive:

```text
a854b549ce4717da09a16558cf533584771ae9c60649347de2c9b63ab28c53fc
```

Rebuilding from the same commit produces an assembly that is **semantically
identical but not byte-identical** — 70 bytes out of 384,512 differ, in the PE
header timestamp and the module version id — so the ZIP gets a different hash
every time it is built. The catalog's rebuild check compares assembly metadata
and is happy with that, but the security scan verifies the SHA-256 of the file
it downloads before it reads a single byte, and that one has to match exactly.

So: upload the archive that is already published on the `builds` branch at
`v0.4.1/io.github.alarmguypro.meshmessenger-0.4.1.zip`, which is the same file
delivered in chat. Do not run the packaging script again first. If the hash of
what you are about to upload is not the string above, you have the wrong file.

## Step 1: Publish the intake release

The catalog downloads the package from a versioned GitHub Release on your own
repository. The `builds` branch is not enough — it needs a release asset at a
URL of exactly this form, with no query string:

```text
https://github.com/AlarmGuyPro/Zeus-MeshMessanger/releases/download/v0.4.1/io.github.alarmguypro.meshmessenger-0.4.1.zip
```

With the GitHub CLI, from a checkout of the feature repository:

```powershell
gh release create v0.4.1 `
  artifacts/io.github.alarmguypro.meshmessenger/io.github.alarmguypro.meshmessenger-0.4.1.zip `
  --repo AlarmGuyPro/Zeus-MeshMessanger `
  --title "Mesh Messenger 0.4.1" `
  --notes "Intake release for the Zeus community catalog. Built from 5b90ba0a321d477a6282f24900d2ab686fe6f233."
```

Or in the browser: **Releases → Draft a new release**, tag `v0.4.1`, target the
commit below, attach the ZIP, publish.

Then confirm the bytes are the ones that were tested:

```powershell
(Get-FileHash -Algorithm SHA256 io.github.alarmguypro.meshmessenger-0.4.1.zip).Hash.ToLowerInvariant()
```

It must be:

```text
a854b549ce4717da09a16558cf533584771ae9c60649347de2c9b63ab28c53fc
```

If it differs, the wrong file was attached. **Never replace bytes at a URL that
has already been published** — cut a new version instead.

The tag must point at commit `5b90ba0a321d477a6282f24900d2ab686fe6f233`, which
is what the catalog will clone and rebuild.

## Step 2: Fork the catalog and branch

```powershell
gh repo fork Zeus-SDR/zeus-community-features --clone
Set-Location zeus-community-features
git remote -v
git fetch upstream main
git switch -c community/io.github.alarmguypro.meshmessenger-0.4.1 upstream/main
```

If `git remote -v` names the source remote something other than `upstream`, use
the name it actually shows. Don't guess.

## Step 3: Edit registry.json, and nothing else

Two changes in one commit:

1. Add the entry from `REGISTRY-ENTRY.json` to the `plugins` array.
2. Set the top-level `generated` field to the current UTC RFC 3339 timestamp.

Leave every `channel: "official"` entry exactly as it is. Change no other file.

The `downloadUrl` in the entry points at Zeus-SDR, not at your release, and
**will return 404 until a maintainer mirrors the package into custody**. That
is expected and correct — your own release URL goes in `source.package`.

Validate before committing:

```powershell
npx --yes -p ajv-cli@5.0.0 -p ajv-formats@3.0.1 ajv validate --spec=draft2020 --strict=false -c ajv-formats -s schema/registry.schema.json -d registry.json
pwsh tools/validate-registry.ps1
```

## Step 4: Open the pull request

```powershell
git add registry.json
git commit -m "feat(registry): add io.github.alarmguypro.meshmessenger 0.4.1"
git push -u origin community/io.github.alarmguypro.meshmessenger-0.4.1
gh pr create --repo Zeus-SDR/zeus-community-features --base main --fill
```

Paste `PULL-REQUEST.md` as the body, complete any items the repository's own
template adds, and **attach the 21 screenshots to the description or a review
comment**. The guide is explicit that screenshots are required review evidence
and that saying the UI was tested is not a substitute.

Then watch the checks:

```powershell
gh pr checks --repo Zeus-SDR/zeus-community-features --watch
```

## What the checks will do

- **Package security scan** downloads `source.package`, verifies the SHA-256
  before reading the file, runs ClamAV and the static scanner, and checks the
  pinned lockfiles against the OSV vulnerability database. Locally this comes
  back CLEAR with 0 fail and 0 review.
- **Source rebuild matches package** clones the repository at the pinned commit
  and rebuilds from `zeus-build.json`, then compares the result to the ZIP byte
  for byte. This passes locally from a clean clone.

Both handle exactly one new version. A pull request touching more than one
fails, so do not add a second version to the same branch.

## If something needs changing

Push corrections to the same branch and answer each review conversation. If the
**package bytes** have to change, that is a new version, a new tag, a new URL
and a new hash — never an overwrite of the published release.

## Callsign: settled

The author is **KQ4WLR**, which is what `plugin.json` already records, so the
package needs no change and the SHA-256 above still stands.

Worth fixing separately, on the hardware rather than in this repository: the
Meshtastic node's owner name was set to "KQ4WL Meshtastic cf18", missing the
final R. That name is what other stations see on the air, so it is worth
correcting on the node itself:

```powershell
py -m meshtastic --port COM12 --set-owner "KQ4WLR Meshtastic" --set-owner-short "wlr"
```
