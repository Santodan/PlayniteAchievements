---
name: santodan-fork-recipes
description: Copy-paste, low-output commands and workflows for the Santodan PlayniteAchievements fork - filtered build and test runs, reading huge files cheaply, finding dead code, splitting changes into separate commits, commit message style, README changelog wording, and doc locations. Use whenever building, testing, searching large files, committing, or drafting release notes, to save tokens.
---

# Low-token recipes

Repository root: `E:/Programs/Playnite/CustomExtension/PlayniteAchievements-main`.
Follow `santodan-fork-context` rules. Commit or push only when the user asks in the
current conversation.

## Build (filtered)

The owner's build command, with output reduced to errors and the produced assemblies:

```powershell
Push-Location source; msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild /v:minimal /nologo 2>&1 | Select-String -Pattern ' error |PlayniteAchievements ->' | Select-Object -First 20; Pop-Location
```

A clean run prints only the Helper and PlayniteAchievements `->` lines.

## Tests (filtered)

```powershell
dotnet test tests/PlayniteAchievements.Tests/PlayniteAchievements.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ClassA|FullyQualifiedName~ClassB' 2>&1 | Select-String -Pattern ' error |Passed!|Failed!|^\s+Failed ' | Select-Object -First 20
```

- Full suite: drop `--filter` and add `--no-build` after a build (about 6,150 tests, ~80 s).
- The test project **links** production files explicitly. A new production file that a
  test needs must be added as `<Compile Include="..\..\source\...cs" Link="Source\...cs" />`
  in `tests/PlayniteAchievements.Tests/PlayniteAchievements.Tests.csproj`. Test files
  themselves are picked up automatically.
- Find tests for an area: `grep -rln 'TypeName' tests/PlayniteAchievements.Tests --include=*.cs`.

## Reading big files cheaply

Large files: `LocalSavesProvider.cs` 11k, `NotificationPublisher.cs` 9k,
`LegacyNotificationSettingsControl.xaml.cs` 9k, `ToastNotificationService.cs` 6.7k,
`LegacyNotificationSettingsControl.xaml` 5k lines.

- Find a member, then read a window: `grep -n 'MemberName' FILE`, then Read with offset/limit.
- Outline a class: `grep -nE '^\s*(public|private|internal|protected)[^=]*\(' FILE`.
- XAML labels and localization lookups: see `santodan-fork-settings-ui`.
- Use focused greps over `source --include=*.cs` and exclude `/obj/` and `/bin/`.
  `source/bin/Release` contains copies of the localization files.

## Is this code alive?

Before trusting or removing a class, check that something constructs it:
`grep -rn 'new TypeName\|TypeName(' source --include=*.cs | grep -v /obj/`.
Also check static callers, nested types and the test csproj links. The maintenance
bundle overlays (`tools/ForkMaintenance/bundle/overlay/`) and
`tools/ForkMaintenance/validation-*` are copies; ignore them when searching for live
callers.

## Commits

- Style: conventional prefixes, for example `docs:`, `fix(l10n):`, `fix(memories):`,
  `refactor:`, `chore(fork-maintenance):`. Subject in the imperative, body explains why.
  Never add `Co-Authored-By:` or any AI attribution line; commits show only the owner.
- The owner commits directly on `main`.
- One commit per logical change when asked. To split one file's hunks without
  interactive staging, write the partial version to a temporary file and stage it:
  `blob=$(git hash-object -w --path=FILE tmpfile); git update-index --cacheinfo 100644,$blob,FILE`.
  The working copy keeps the full version for the next commit.
- Commit code before refreshing the maintenance bundle, then commit the bundle last.
  The export captures the whole working tree, including repository skills.
- Use a heredoc or `-F -` for messages. Python heredocs inside the Bash tool can fail
  to parse, so write scripts to the scratchpad and run `python -I script.py`.

## README changelog (protected file: suggest text, do not edit)

The owner adds entries under `## Next release - TBD` above dated releases
(`## YYYY-MM-DD - vX.Y.Z.N`). Use short past-tense bullets naming settings in
backticks, for example ``- Fixed the `Capture delay (ms)` in `Memories` not being applied``.
Mention user-visible default changes.

## Docs to keep in sync

| Change area | Doc |
| --- | --- |
| Notifications, Memories, sounds, templates | `ACHIEVEMENT_NOTIFICATIONS_GUIDE.md` |
| Local provider, overrides, Performance tab | `LOCAL_PROVIDER_TROUBLESHOOTING.md` |
| Native consoles/ports | `NATIVE_SOURCES_GUIDE.md` |
| Progress bars | `tools/NotificationTemplates/PROGRESS_BAR.md` |
| Behavior to preserve | `tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md` (maintenance only) |

Don't link git-ignored `docs/` files from tracked docs.
