---
name: santodan-fork-upstream-update
description: Integrate an explicitly requested upstream PlayniteAchievements update into the Santodan fork, resolve and resume bundle conflicts, preserve fork ownership, and apply the owner's version policy.
---

# Integrate upstream into the Santodan fork

Use only for an upstream update request, not an ordinary fix or bundle refresh.
Work in the actual checkout; commands below start at its root. Respond in English
unless requested otherwise. Never commit or push without the user's request.
Do not discard, reset, or stash existing user work merely to satisfy the updater.
Fork functionality remains under Santodan-Fork; do not relocate it into other
tabs or change unrelated upstream tab behavior while resolving conflicts.

## Establish a recoverable starting point

Read applicable instructions, `git status --short`, pending merge state, remotes,
the existing bundle manifest, and `tools/ForkMaintenance/Update-ForkInPlace.ps1`.
Read maintenance procedures in `tools/ForkMaintenance/README.md` and relevant
behavior in `FORK_FEATURE_CHECKLIST.md`. Use the companion maintenance skill for
export/replay details when available; the scripts are the operational authority.

Confirm the selected upstream ref/commit from the request and configured remote.
Do not assume an old release mentioned in documentation is current. Fetching the
configured Git remote is appropriate to an upstream update request; do not use
unverified remote URLs or infer a newer version from the host date.

The in-place updater replaces non-protected tracked implementation files with
the selected upstream tree before replaying the bundle. It allows dirty protected
and maintenance paths but refuses other local changes. Preserve the user's work
and use an isolated checkout to investigate if this gate is hit. A fresh bundle
can capture uncommitted work, but it does not make bypassing the dirty-work gate
safe. When progress truly requires a user decision, explain the concrete conflict.

Verify that the saved bundle captures the intended fork before replacement.
Refreshing the bundle is part of an explicitly requested upstream integration
when needed to preserve the fork, but unrelated maintenance refactors are not.
Record protected-file hashes; keep fork identity and existing manifest edits.

## Update and resolve

Once the input state is suitable, use an explicit selected upstream ref:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ForkMaintenance/Run-ForkMaintenance.ps1 -Action Update -UpstreamRef $selectedUpstreamRef -NoPause
```

Set `$selectedUpstreamRef` to the verified ref. Use `-SkipFetch` only when that
local ref is already current. The updater may start an uncommitted merge with
upstream as a pending second parent; preserve that ancestry. Do not replace it
with a hard reset, squash, or a new independent commit.

Resolve real shared-file, overlay, and localization conflicts by preserving
upstream changes and the fork's behavior together. Use fork partials/helpers and
small routing hooks. Do not blindly choose ours/theirs or force overlay/semantic
replacement. Git rerere may assist but its results still need review.

When preflight names patch targets the checkout lacks, upstream deleted those files:
port each fork change to the upstream replacement (or drop it), remove those patch
sections and any overlay partial of the deleted class from the bundle, and rebuild
the bundle hashes before running Update. A clean merge is not proof of wiring: after
upstream rewrites a shared file (event guards, lazy builders, read models), re-check the
fork hooks it hosts and click through every Santodan-Fork sub-tab in Playnite.

Stage only the conflict resolutions necessary for resume; this is distinct from
permission to commit or stage unrelated work. Then resume without restarting or
resetting the update:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ForkMaintenance/Run-ForkMaintenance.ps1 -Action Update -ResumeAfterConflict -UpstreamRef $selectedUpstreamRef -NoPause
```

Confirm an appropriate pending merge exists before resume. If it belongs to
unrelated work, do not abort or repurpose it. Inspect reported errors rather than
repeatedly running a mutating update.

## Version policy and completion

The owner explicitly permits this upstream-update exception: set only
`source/extension.yaml` to the selected upstream version with `.1` appended
(upstream `4.2.1` becomes fork `4.2.1.1`). Read the upstream manifest at the selected
commit to determine that version; do not append to the fork's existing suffix.
Keep `InstallerManifest.yaml` unchanged, including existing user edits, unless
the user separately requests its update. Preserve `AddonDBManifest.yaml` fork
identity and the protected root README. The bundle excludes these files, so the
extension version adjustment is a separate deliberate edit after tooling checks.

Build from `source/` with:

```powershell
msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild
```

Run tests appropriate to the integration, including the wider suite when shared
upstream changes justify it. Review the relevant checklist sections for Local
identity/polling, Steam authentication and public schemas, custom-data migrations,
capstones, notification/capture behavior, saved settings, and theme adapters.
Do not claim live Playnite verification from compilation or replay alone.

Before exporting, update the `santodan-fork-*` skills to match the integrated code
in `.agents/skills/` and `~/.claude/skills/`, as described in
`santodan-fork-maintenance` step 4; the export carries them into the bundle overlay.
Export a fresh bundle with the newly selected upstream baseline explicitly, then
preflight and actually replay onto an isolated checkout of that exact commit.
Compare intended captured source, verify hashes/protected files, and record the
real validation outcome in maintenance notes. Keep changes uncommitted and
preserve any pending upstream merge parent for the user's eventual reviewed
commit. Report the upstream commit/version, fork version, test/build results,
conflicts resolved, and remaining live checks.
