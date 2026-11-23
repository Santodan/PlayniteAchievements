# Achievement Watcher Next (AWN) sync record

AWN (https://github.com/Shirowwww/Achievement-Watcher-Next) is the reference for Local save
compatibility. Compare against it on every upstream update (see the
`santodan-fork-upstream-update` skill), then update this file.

## Last compared

- AWN commit: `f9903eb404f0eedf152144c576f197dcd96e2950` (2026-11-22, version 3.12.8)
- Compared on: 2026-11-23

## Coverage map (AWN source -> fork implementation)

| AWN | Fork |
| --- | --- |
| `app/parser/emuSaveRoots.js` `SAVE_ROOTS` | `LocalSavesProvider.GetLocalRootPaths` + `Santodan/LocalSavesProvider.AwnCompat.cs` `GetAwnCompatSaveRoots` (also used by `LocalFolderGamesImporter.GetImportRoots`) |
| `app/parser/steam.js` `SAVE_FOLDER_FILES` | `LocalSavesProvider.LocalAchievementIniFileNames`, `SupportedAchievementRelativeDirectories`; generic names guarded by `IsAwnSaveFileAccepted` |
| `app/parser/emuSave.js` (`decodeSaveEntries`, `normalizeSaveEntry`, RAZOR1911) | `AwnCompat.cs` `ParseLocalSaveFileWithAwnFormats`, `ApplyAwnIniShapes`, `ParseRazor1911Save` |
| `app/util/rldBlob.js` | `AwnCompat.cs` `TryDecodeRldBlob` |
| `app/parser/sse.js` + `achievementSchemaIndex.js` CRC lookup | `AwnCompat.cs` `ParseSmartSteamEmuStats`, `RemapAwnCrcEntries` (called from `CorrelateLocalEntriesWithSchema`) |
| `app/parser/greenluma.js` | `AwnCompat.cs` `TryGetGreenLumaRegistryEntries` (used when no save file exists) |
| `app/parser/userDir.js` (emulator configs), `goldberg.js` `readConfiguredSavePath` | `AwnCompat.cs` `EnumerateAwnInstallConfigSaveFolders` (hooked into `GetInstallLocalAchievementFolders`) |
| `app/parser/gog.js`, `gogUniverseLan.js`, `uplayR2.js`/`uplayR2Live.js` (save dirs, objective keys), `uplaySteamTable.js` + `assets/uplay-steam.json`, `socialclub*.js`, `ff7.js` + `assets/ff7-achievements.json`, `anadiusNative.js` (export dir) | `Santodan/LocalSavesProvider.AwnPlatforms.cs` (`ResolveAwnPlatformSteamAppIdAsync`, `RemapAwnObjectiveIdEntries`, `GetAwnNameKeyedSaveFolders`, FF7), `NativeSources/AwnUplaySteam.json` |
| `app/parser/steam.js` `mergeSiblingStats`, `statProgress.js` | `AwnCompat.cs` `ApplyAwnStatProgress`, `GetAwnStatLinks` |
| `app/parser/x360Ports.js`, `x360RecompFormats.js` (unleashed, ini-earned), `reblueBonus.js` | `NativeSources/NativeX360PortTable.cs` + `NativeX360Ports.json` (regenerate the JSON by parsing the `PORTS` literal as text, never by running it) |
| `app/parser/xlln.js`, `xllnSpa.js` | `NativeSources/NativeXllnReader.cs` (native source `xlln`) |
| `app/parser/recompPortTable.js`, `recompPortFormats.js`, `app/assets/recomp-ports.json` | `NativeSources/NativeSourceReader.cs`, `NativeSourceConfiguration.cs`, `NativeLocationFiles.cs`, `NativePortDefinitions.json` (entries copied unchanged; notice in `THIRD-PARTY.txt`) |

Ported sources keyed by another id: GOG (Nemirtingas Galaxy, UniverseLAN), Ubisoft (Goldberg Uplay
R1/R2), Rockstar (Goldberg SocialClub), FINAL FANTASY VII 2013, the anadius export, Games for Windows -
LIVE (XLiveLessNess, native source `xlln`), plus the Xbox 360 recomp port table and re:Blue bonus.

Ported formats: Goldberg/GSE/EMPRESS JSON (lenient: string/number flags, list layout, BOM),
CODEX/RUNE/CPY INI with progress, OnlineFix, TENOKE (inline and `[STATS]` progress), SOVEREIGN,
RLD! (`State`/`Time` hex values, time-only builds), ALI213 `HaveAchieved`, Hoodlum
`[AchievementsUnlockTimes]`, 3DM `[State]`/`[Time]`, SKIDROW `ACHIEVE_DATA`, CreamAPI cfg with 7-digit
times, SmartSteamEmu `stats.bin`, RAZOR1911 `achievement`, GreenLuma registry.

Ported recomp ports: dusklight, ghostship, sms, smseclipse, smb1r, dkrr, smas, rr64, gdiffuser,
nectar, nectar2, fh2azimuth, sanctuary, issn, issnfl, deadnation.

## Deferred (not ported yet)

- AWN's write side (setting up or repairing emulators, the anadius XML-to-ini bridge, Uplay R2
  schema generation, relocking): the fork only reads saves.
- x360 port details that need the game data: region choice from `default.xex` when one exe plays
  several titles, launcher `--user_data_root` arguments, and Linux-only paths.
- Linux/Wine prefix hosts (`util/winHosts.js`): the fork is Windows-only.
