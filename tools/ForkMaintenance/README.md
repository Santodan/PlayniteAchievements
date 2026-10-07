# Fork maintenance bundle

This directory turns the Santodan fork into a repeatable layer over an upstream
PlayniteAchievements checkout. It intentionally does not modify or package:

- `README.md`
- `AddonDBManifest.yaml` (the fork's add-on database identity)
- `source/extension.yaml`
- `InstallerManifest.yaml`

The bundle has three parts:

1. **Overlay files** — files that exist only in the fork. These are copied into
   a new upstream checkout and normally have no merge conflicts.
2. **A three-way Git patch** — only upstream-owned files changed by the fork.
   Full-index blob IDs let Git merge unchanged portions automatically.
3. **Localization recipes** — translation entries are merged by `x:Key`
   instead of by line, avoiding conflicts caused by reordered or reformatted
   resource dictionaries.

Git `rerere` is enabled while applying a bundle, so a conflict resolution Git
has seen before can be reused on later upstream updates.

The behavioral inventory in
[`FORK_FEATURE_CHECKLIST.md`](FORK_FEATURE_CHECKLIST.md) is the post-merge test
checklist for the fork features that have historically been lost during
upstream integrations.

The v4.0.1 recovery status and remaining live checks are recorded in
[`../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md`](../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md).
The 2026-10-07 bundle refresh captures the complete current working tree,
including uncommitted fixes; it is not a new upstream fetch, commit, or push.
Implementation coverage does not replace the pending live checks in that review.
This refresh replaces the obsolete cache schema-v19 reconciliation with the
working PS5 Experience Theme Migration/runtime adapter. The migrated theme
enables correction of PS5Core Local trophy progress in memory, including after
refreshes, without changing PS5Core.dll or the achievement database. Theme XAML
contains no dependency on plugin assemblies/resources during startup. The user
confirmed corrected fullscreen percentages on 2026-10-07, including the selected
game's trophy-detail header. That header uses a separate PS5Core selected-game
object. The adapter covers its percentage, earned count and progress bar, as well
as the trophy overlay. PS5Core can omit Provider or populate it with a platform
label such as `PC (Windows)`. Both list and detail progress use trophy counts
consistently rather than gating correction on that unreliable provider label.
The 2026-10-08 refresh also captures synchronization of individual achievement
states and unlock dates from the fork cache by game ID and exact achievement API
name. The adapter refreshes PS5Core filtering/sorting and notifies
`CanSelectUnlocked`/`CanSelectLocked`, so the Unlocked filter becomes selectable
after synchronization. The user confirmed the progress and filter fixes on
2026-10-08. Regression coverage includes actual PS5Core 0.7.4 objects and a live
WPF filter-button binding.
The refresh also captures persistent Theme Migration scrollable/highlight and
per-control choices, the automatic Limited/Full/Custom mode selector, and use of
those saved options for first-time and upgraded themes. Desktop defaults to Full;
Fullscreen retains Limited migration. The 15 targeted regression tests and
Release rebuild passed; live settings-dialog/startup checks remain to be verified.
Automatic theme migration notifications now restart Playnite when clicked, using
Playnite's built-in restart method on the UI thread. The English notification
explains this action. The Release rebuild passed; a live notification-click
restart check remains pending.
Git output and generated JSON/XML/patch reads explicitly use UTF-8, including
under Windows PowerShell 5.1, to preserve non-ASCII text during export/application.

The refreshed bundle was dry-run validated against a separate clean checkout of
upstream v4.0.1 (`79108753e3f8ddc775aa569ff8f5ed0362f3f22b`): the three-way
patch, fork-only overlays, localization recipes, hashes, and protected-file
checks passed. This validates replay against that baseline, not conflict-free
application to an as-yet-unseen future upstream release.

## Refresh the bundle from the working fork

Run this after the fork is working and tested:

```powershell
cd E:\Programs\Playnite\CustomExtension\PlayniteAchievements-main
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Export-ForkBundle.ps1 `
    -Baseline upstream/main `
    -Force
```

The export compares the complete working tree (staged and unstaged changes)
with the selected upstream baseline. Fork-only files, shared-file changes, and
localization keys are captured separately. Ignored build output is never
included.

Review the result:

```powershell
git diff -- tools/ForkMaintenance
```

The generated `bundle/bundle.json` records the exact upstream commit and SHA-256
hashes for every generated component.

## Update the current checkout in place

Run the `Update` action from the normal project directory. It fetches upstream,
replaces the tracked non-protected project files with `upstream/main`, and applies
the fork bundle directly in the current checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -UpstreamRef upstream/main `
    -NoPause
```

The changes remain uncommitted and appear immediately in the current checkout's
VS Code Source Control panel. The action preserves `README.md`,
`source/extension.yaml`, `InstallerManifest.yaml`, and `tools/ForkMaintenance`.
`AddonDBManifest.yaml` is also protected to retain the fork's update identity.
It refuses to start if other local changes exist, preventing unrelated work from
being overwritten. When upstream is not already an ancestor, the action also
opens a no-content merge before assembling the updated files. After review,
stage and commit normally in VS Code or Git: that commit will have the previous
fork tip and the selected upstream commit as its two parents, so GitHub will not
report the fork as behind those upstream commits. Use `-SkipFetch` only when the
selected upstream ref is already current locally.

If the patch reports conflicts, resolve and stage those files, then resume the
same in-place workflow without resetting the checkout again:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -ResumeAfterConflict `
    -NoPause
```

`-ExecutionPolicy Bypass` applies only to that PowerShell process. It does not
change the machine-wide or user-wide execution policy.

The dry run checks:

- patch applicability using Git's three-way merge;
- collisions with fork-only overlay files;
- independent upstream edits to localization keys;
- bundle hashes;
- protected manifests.

The same validation can be invoked through `Test-ForkBundle.ps1`.

If application leaves a real shared-file conflict, resolve it normally. Git
`rerere` records that resolution for later updates. Do not use
`-ForceOverlay` or `-ForceSemantic` until the reported collision has been
reviewed; those switches deliberately choose the fork's version.

## Recommended upstream workflow

1. Run `Run-ForkMaintenance.ps1 -Action Update` in the normal checkout.
2. Resolve only the reported shared-file conflicts, if any.
3. Build and test the extension.
4. Review every change in VS Code.
5. Stage and commit the reviewed update; the pending upstream merge parent is
   retained automatically.
6. Export a fresh bundle against the new upstream baseline.
7. Update the protected manifests manually, outside this tooling.

## Reducing the remaining shared patch

The bundle makes future updates repeatable immediately. Conflicts can be reduced
further over time by moving fork implementations into additive files:

- C# partial classes named `*.Fork.cs`;
- fork-specific services behind small upstream integration hooks;
- separate XAML controls/resource dictionaries;
- one registration table for providers, tabs, and context-menu commands.

After such a refactor, the shared patch should contain mostly small registration
hooks while the implementation remains in conflict-free overlay files.
