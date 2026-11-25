---
name: santodan-fork-local-provider
description: Map of the Santodan-Fork Local achievement provider - save discovery, schemas/metadata, offline cache, native emulator/port sources, per-game Local Overrides, Local Saves context menus, imports, and custom Local polling. Use before changing or debugging Local detection, identity, metadata, overrides or polling, to go straight to the right files.
---

# Local provider map

Read `santodan-fork-context` rules first. Paths are relative to the repository root.
`LocalSavesProvider.cs` is 11k lines: search by symbol or literal filename, and don't
read the whole file.

## Core files

- Provider: `source/Providers/Local/LocalSavesProvider.cs`, plus fork partials in
  `source/Providers/Local/Santodan/`:
  `LocalSavesProvider.SantodanDiscovery.cs`, `.SantodanOfflineCache.cs`
  (`LocalOfflineSchemaCache.cs`), `.SantodanPolling.cs`, `.SantodanStartupBaseline.cs`,
  `.NativeSources.cs`, `.AwnCompat.cs` (Achievement Watcher Next formats, extra roots,
  GreenLuma registry, portable saves from emulator configs, `Stats.ini` stat progress),
  `.AwnPlatforms.cs` (GOG/Ubisoft product-id saves mapped to a Steam schema, SocialClub
  name-keyed folders, FF7 2013, Ubisoft objective-id remap).
- AWN compatibility record: `source/Providers/Local/Santodan/AWN_SYNC.md` (last compared AWN
  commit, AWN-to-fork map, deferred items). Update it whenever AWN behavior is ported.
- Recognized save files: grep the literals `"achievements.json"`, `"achievements.ini"`,
  `"user_stats.ini"`, `"achievement_progress.json"`, `"tenoke.ini"`, `"SOVEREIGN.ini"`,
  `"LumaPlay.ini"`, `"achievements_db.json"` (Nemirtingas Epic). The full list is
  `LocalAchievementIniFileNames` (adds `achiev.ini`, `Achievements.Bin`, `achieve.dat`,
  `CreamAPI.Achievements.cfg`, `stats.ini`, `stats.bin`, `achievement`); every INI-like file is
  parsed by `ParseLocalSaveFileWithAwnFormats`, and `IsAwnSaveFileAccepted` guards generic names.
- Settings model: `source/Providers/Local/LocalSettings.cs` (also holds all fork notification
  and Memories settings), `Santodan/LocalSettings.NativeSources.cs`.
- Settings UI: `source/Providers/Local/LocalSettingsView.xaml` (+ `.xaml.cs`). Sections:
  General/icon, folders and missing-file interval, refresh on close, multiple-folder
  warning, SteamHunters categories, Steam path/user, metadata priority, automatic
  folders and path variables, custom/excluded folders, Offline Cache, Import,
  SuccessStory.
- Native consoles/ports: `source/Providers/Local/Santodan/NativeSources/`
  (`NativeSourceReader.cs`, `NativeTrophyReader.cs`, `NativeLocation*.cs`,
  `NativeX360PortTable.cs` + `NativeX360Ports.json` (Xbox 360 recomp port table, re:Blue bonus),
  `NativeXllnReader.cs` (Games for Windows - LIVE / XLiveLessNess, SPA definitions),
  `NativeSourcesSettingsView.cs`, `NativePortDefinitions.json`, icons and licenses).
  The user doc is `NATIVE_SOURCES_GUIDE.md`. Keep the license/attribution files.
- Import: `LocalFolderGamesImporter.cs`, `LocalImportTargetDialog.xaml`,
  `LocalSuccessStoryImportService.cs`.

## Where the UI lives

- Global: Santodan-Fork > Platforms > Local. Only Local, its native entries and the
  Steam secondary view are added to `ForkProviderNavigationItems` in
  `source/Views/SettingsControl.xaml.cs` (`BuildProviderNavigationItems`; grep
  `ForkProviderNavigationItems.Add`).
- Per game: Manage Achievements > **Local Overrides** tab
  (`source/Views/ManageAchievements/ManageAchievementsOverridesTab.xaml`). Sub-tabs:
  Local Saves & Schema (`source/Views/GameOptionsLocalOverridesSection.xaml`: cached
  unlock protection, save folder override, refresh on close), Native Sources
  (`GameOptionsNativeSourcesSection.xaml`), Steam (App ID, Steam user, account),
  Epic (Nemirtingas), LumaPlay, RetroAchievements. View model:
  `source/ViewModels/ManageAchievementsViewModel.ForkOverrides.cs`.
- Custom schema: Manage Achievements > Custom Schema (`ManageAchievementsCustomSchemaTab.xaml`).
- Context menu: `source/PlayniteAchievementsPlugin.Menus.cs`, section
  `PluginLocalGameMenuSection` = "Playnite Achievements|Local Saves" (Download expected
  achievements.json, Set forced Steam App ID, Change Steam User, Set local save folder,
  Change Provider). Actions are in `PlayniteAchievementsPlugin.LocalMenus.cs`. Refresh
  exclusion is under the "Maintenance" submenu.

## Polling (Performance tab options)

`source/Services/InGameMonitoring/SantodanLocalPolling.cs`, `SantodanPollingScope.cs`,
`SantodanPollingConfiguration.cs`, `SantodanStartupBaselineStore.cs`,
`LocalPollingDirectoryCache.cs`, `LocalPollingFingerprintGate.cs`;
`source/Services/Refresh/RefreshRuntime.SantodanPolling.cs`. The scope must not leak
into concurrent or upstream work, and an unknown fingerprint falls back to a full refresh.
Options and defaults: `source/Views/Settings/General/ForkPerformanceSettingsSection.xaml`,
`source/Services/Settings/ForkPerformanceDefaults.cs`, `docs/ForkPerformanceOptions.md`
(git-ignored; may be missing).

## Rules that are easy to break

- Separate save-source identity from Steam schema identity. An explicit App ID override
  beats links/notes for schema selection.
- LumaPlay: `TryGetConfiguredLumaPlayAppId` (forced Uplay App ID, then `LumaPlay.ini`
  `UplayGameID`) keys the `HKCU\SOFTWARE\LumaPlay` registry unlocks only. It must not set
  `isSchemaAppIdForced`; the Steam schema comes from `UplaySteamTable`, then the name
  search. Polling adds `BuildLumaPlayRegistryFingerprint`, since no file changes on unlock.
- Custom schema rows read `points`/`score`/`gamerscore` and percent from
  `globalPercent`/`percent`/`rarity` (numbers, `"12.5%"`, `"12,5"`); no percent means Common.
- Public SteamHunters definitions need no authentication and must never import another
  user's unlocks.
- Cached unlock protection: failed or empty payloads stay protected; disabled protection
  accepts valid lower counts.
- Successful refreshes must report written game IDs (normal and custom-poll paths).
- The Steam appcache schema `.bin` holds every language: its parsers
  (`ParseLocalizedTokenValue`, `ResolveSteamKvLocalizedField`) take `GetSteamLanguage()`
  first, then `english`. Never hardcode English there: an appcache-only schema (SteamHunters
  down) is cached in memory and offline under the user's language.

User doc to keep in sync: `LOCAL_PROVIDER_TROUBLESHOOTING.md` (its Performance section
mirrors the Performance tab). Checklist sections: `## Local provider and game overrides`,
`## Native emulator...`, `## Experimental Performance tab`.
