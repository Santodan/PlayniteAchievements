# Fork maintenance bundle

This directory turns the Santodan fork into a repeatable layer over an upstream
PlayniteAchievements checkout. It intentionally does not modify or package:

- `README.md`
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
It refuses to start if other local changes exist, preventing unrelated work from
being overwritten. Use `-SkipFetch` only when the selected upstream ref is
already current locally.

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
5. Export a fresh bundle against the new upstream baseline.
6. Update the protected manifests manually, outside this tooling.

## Reducing the remaining shared patch

The bundle makes future updates repeatable immediately. Conflicts can be reduced
further over time by moving fork implementations into additive files:

- C# partial classes named `*.Fork.cs`;
- fork-specific services behind small upstream integration hooks;
- separate XAML controls/resource dictionaries;
- one registration table for providers, tabs, and context-menu commands.

After such a refactor, the shared patch should contain mostly small registration
hooks while the implementation remains in conflict-free overlay files.
