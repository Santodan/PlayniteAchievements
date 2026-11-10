---
name: santodan-fork-development
description: Implement, debug, review, and validate Santodan-Fork features in PlayniteAchievements while preserving upstream tabs, user data, manifests, and the separately authorized maintenance bundle.
---

# Develop the Santodan fork

Work in the actual PlayniteAchievements checkout, normally
`E:/Programs/Playnite/CustomExtension/PlayniteAchievements-main`.
Use English unless explicitly asked otherwise. Preserve existing work; never
commit or push without a request. Read applicable AGENTS.md and current diffs.

Fork changes belong under Santodan-Fork. Keep other tabs and their behavior
intact. Shared files should contain only the necessary fork integration hooks;
prefer existing Santodan partials/helpers for implementation. Do not refresh or
edit `tools/ForkMaintenance` unless the current message requests it. Do not edit
`source/extension.yaml` or `InstallerManifest.yaml`, or revert pre-existing edits
there. The extension-version exception applies only to an upstream update request.

## Find and constrain the change

Trace the visible control, its settings binding/edit copy, and the runtime caller.
Do not assume that the Local provider or a shared notification service belongs
exclusively to one settings tab. For a subsystem map, read
`.agents/skills/santodan-fork-context/references/source-map.md` if available;
otherwise search the actual source and the relevant sections of
`tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md`.

Preserve these boundaries when relevant:

- Settings remain transactional: editing and Restore defaults affect the edit
  copy, Save persists, Cancel discards. Preserve unrelated settings and defaults.
- Performance options retain their declared defaults and fork-only scope.
  Caches need correct invalidation even while their option is disabled. Custom
  polling context must not leak into concurrent or upstream work. Read
  `docs/ForkPerformanceOptions.md` if present for specific maintenance boundaries.
- Preserve local unlock ownership, explicit identity overrides, and separation
  of source discovery from metadata selection. Public definitions do not grant
  permission to import another user's unlocks or require authentication.
- Refresh summaries drive downstream work. A successful write must report its
  game ID through the normal and custom refresh paths after callbacks succeed.
  Preserve capstone generation markers and intentional deletion behavior.
- Notification preview success does not prove real unlock delivery. Preserve
  baseline/session deduplication, runtime summary invalidation, user templates,
  sound choices, and capture paths.
- Preserve custom-data migrations and per-game schema isolation. For themes,
  retain the runtime adapter and plugin-independent startup XAML.

Edit live source first. Generated bundle overlays and historical validation
snapshots are not additional source copies to keep synchronized during ordinary
development. If the bundle will need refreshing, report that without changing it
unless the current user message authorized maintenance.

## Build and verify

For a production build, use exactly this command from `source/`:

```powershell
msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild
```

This is a .NET Framework 4.6.2 WPF project. Inspect the csproj and current build
scripts before changing package/helper generation; do not substitute a different
build command or raise the framework version merely to make a local build work.

Choose relevant existing MSTest coverage in
`tests/PlayniteAchievements.Tests/PlayniteAchievements.Tests.csproj`. From the
repository root, a focused run can use:

```powershell
dotnet test tests/PlayniteAchievements.Tests/PlayniteAchievements.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ActualTestClass'
```

Replace the filter with discovered test names. Check that new behavior is included
in the test project's linked sources/stubs. Add regression coverage for meaningful
behavior changes; do not add tests that only duplicate a trivial implementation.
Broaden testing when shared paths, failures, or unresolved risks justify it.

For theme/PS5 changes, inspect `tools/ThemeMigration/Test-Ps5*.ps1` and the
checklist. WPF bridge/state tests may require `powershell.exe -STA`; inspect local
Playnite/PS5Core paths before running. Do not install a package, restart Playnite,
or modify an external theme merely to validate an ordinary repository change
unless that action is within the user's request.

Review the final diff and run `git diff --check`. Report the behavior changed,
build/test outcome, and remaining live Playnite checks. Do not present historical
test counts, package timestamps, or manual checks as results from this run.
