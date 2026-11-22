---
name: santodan-fork-maintenance
description: Review, refresh, validate, and replay the Santodan PlayniteAchievements ForkMaintenance bundle when the current request asks for maintenance; distinguish bundle export from an upstream update.
---

# Maintain the fork bundle

Use in the actual Santodan PlayniteAchievements checkout. All commands below
start at the repository root. Respond in English unless requested otherwise.
Do not commit or push. Preserve existing staged/unstaged work and protected-file
edits. Only modify `tools/ForkMaintenance` when the current user message requests
maintenance work. A request to check the bundle authorizes review, not automatic
replacement. A request to refresh the bundle does not authorize an upstream update.

The protected paths currently configured are `README.md`, `AddonDBManifest.yaml`,
`source/extension.yaml`, and `InstallerManifest.yaml`. Read current
`tools/ForkMaintenance/fork-maintenance.json` rather than relying on this list
remaining exhaustive. Never change the release manifests as part of export.
Fork behavior must remain under Santodan-Fork; preserve other tabs.

## Understand the artifacts

Read `tools/ForkMaintenance/README.md` for operating details and relevant sections
of `FORK_FEATURE_CHECKLIST.md` for behavior to retain. Inspect the scripts when
their behavior matters more than historical prose.

- `bundle/bundle.json` records the baseline ref/commit and component SHA-256s.
- `bundle/fork-changes.patch` captures modifications to upstream-owned files with
  full-index blobs for three-way application.
- `bundle/overlay/` contains fork-only files.
- `bundle/localization-recipes.json` merges dictionaries by `x:Key`.
- `validation-progress-rows-20261022/` is a historical snapshot, not an input to
  active bundle application. Preserve existing edits there; do not extend it.

The maintenance directory and configured build paths are excluded from export.
Other non-ignored additions, including repository skills, can become overlays.
Review their inclusion instead of silently changing exclusions or omitting files.

## Review or refresh

1. Inspect status/diffs, config, and the existing manifest. Record protected-file
   hashes when validating preservation. Inspect a pending merge without aborting
   it. Determine whether the task is review, export, replay, or upstream update.
2. For review, compare source/overlay content and recorded hashes. An edited
   overlay with an unchanged manifest hash makes application fail. Do not stop
   at checking that the source and overlay text match.
3. For an ordinary refresh, retain the manifest's existing exact baseline unless
   the user requests another baseline. Resolve its ref and verify it still
   matches `baselineCommit`; if it moved, use the recorded commit. Do not fetch
   upstream or substitute `upstream/main` to refresh a local fix.
4. **Update the skills before exporting.** Every bundle refresh also refreshes the
   fork skills, inside and outside ForkMaintenance:
   - Review `.agents/skills/santodan-fork-*` (including
     `santodan-fork-context/references/source-map.md`) against the changes being
     captured. Fix paths, symbol names, settings, defaults, removed code and
     pitfalls that changed; add new fork areas to the relevant area map. Keep
     each skill short; it is loaded to save tokens.
   - Copy them to the global folder `~/.claude/skills/` (outside copy) and confirm
     `diff -rq .agents/skills/<name> ~/.claude/skills/<name>` reports nothing.
   - The export then copies `.agents/skills/` into
     `tools/ForkMaintenance/bundle/overlay/.agents/skills/` (inside copy). After
     export, confirm the overlay skill files equal `.agents/skills/`.
   - If nothing in the skills needed changing, say so in the report.
5. Export the current complete working tree only after understanding which
   staged, unstaged, and untracked changes it will capture. It is not limited to
   the latest fix. Do not discard unrelated user work to narrow the export.
   Isolate the export input if the request explicitly limits captured changes.

```powershell
$manifest = Get-Content tools/ForkMaintenance/bundle/bundle.json -Raw -Encoding UTF8 | ConvertFrom-Json
$baselineCommit = [string]$manifest.baselineCommit
git rev-parse --verify "$baselineCommit^{commit}"
# Use the verified unchanged baselineRef for readable metadata, or the exact commit.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ForkMaintenance/Export-ForkBundle.ps1 -Baseline $baselineCommit -Force
```

Check each command's exit code before proceeding. `-Force` replaces the generated
bundle directory: verify its resolved absolute path lies inside the intended
`tools/ForkMaintenance` directory before invoking replacement. If preserving the
previous bundle is needed for recovery, save a copy outside the export input.
The exporter uses a temporary index but still writes Git objects; restricted
environments may require tool-level escalation. It must not change the user's
real index or create a commit. Report an export failure and repair an incomplete
bundle before claiming success.

**Always pass `-Baseline` explicitly.** At skill creation the launcher
`Run-ForkMaintenance.ps1` defaulted to `v3.1.0`, the config defaulted to
`upstream/main`, and the active bundle used `v4.2.1`. None is a safe implicit
choice for every task. Do not hard-code these historical versions into a refresh.

## Validate in an isolated checkout

Use a unique temporary local clone or authorized worktree at the recorded
`baselineCommit`; do not reset the user's working checkout. For a local clone,
`git clone --shared --no-checkout <source-root> <unique-temp-target>` followed by
`git -C <unique-temp-target> checkout --detach <baselineCommit>` avoids registering
a worktree in a restricted source `.git`. Check exit codes. A shared clone depends
on the source object store and is suitable for temporary validation only.

Run preflight, then actual replay with the scripts from the working repository:

```powershell
# Set $validationRoot to the verified, clean, temporary baseline checkout.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ForkMaintenance/Test-ForkBundle.ps1 -RepositoryPath $validationRoot
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/ForkMaintenance/Apply-ForkBundle.ps1 -RepositoryPath $validationRoot
```

- The scripts verify patch/recipe/overlay hashes, collisions, localization, and
  protected files. A clean preflight alone does not prove actual replay.
- Compare replayed implementation files with intended working source. For a full
  export, compare the relevant captured paths across the tree, accounting for
  documented protected/excluded paths and semantic localization. Normalize only
  newline differences for text when needed; compare binary assets byte for byte.
- Never use `-ForceOverlay`, `-ForceSemantic`, or `-AllowDirty` to suppress an
  unexplained failure. Inspect conflicts and independently changed upstream data.
- Verify protected-file hashes and final diff. Add a concise maintenance note
  describing behavior, baseline, actual verification, and pending live checks.
  Avoid changing historical claims to look like new results.
- If a production build is needed, run from `source/`:
  `msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild`.
  A documentation/hash-only refresh does not itself require a rebuild.

Clean up only temporary paths created for this task, after verifying their
absolute locations. Report validation limitations explicitly. Successful replay
against one baseline does not prove compatibility with a future upstream release.

For an explicitly requested upstream integration, read
`.agents/skills/santodan-fork-upstream-update/SKILL.md` if available. Do not run
`-Action Update` as a substitute for refreshing a bundle.
