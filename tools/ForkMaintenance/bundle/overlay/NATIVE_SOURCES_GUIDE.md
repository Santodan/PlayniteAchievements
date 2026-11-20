# Native consoles, recompilations and community patches

These sources are part of the fork's **Local** provider. In **Settings → Santodan-Fork → Platforms**, the **Emulators** section has separate menus for **KytyPS5**, **PS3 Recomp**, **PS4 Recomp** and **Xbox 360 Recomp / ReXGlue**. Each port or patch has its own menu under **PC ports & community patches**. Shared folders are in **Native source folders** under **Local**. Keep the Local provider enabled. These controls use the normal settings edit copy: Save applies them and Cancel discards them.

## Configure shared locations once

You usually select folders, not every achievement file for every game. The reader recognizes the filenames listed below inside the appropriate game's folder. Per-game file selection is needed for unusual layouts, ambiguous profiles or multiple trophy sets.

AW Next's detection-related settings use **Folders** (save/emulator/config folders and game-library folders), source switches, and launcher/emulator configuration such as Kyty's `Kyty.ini`. The extension now exposes the corresponding location choices on its fork settings page:

- **Game-library folders:** shared libraries containing installed native games. The extension matches a game to its Playnite name/title ID and refuses ambiguous installations. Discovery is bounded to three levels and 400 directories per selected root; it does not search whole drives automatically.
- **Quiver discovery:** enable/disable reading Quiver libraries and optionally add a portable/custom Quiver data folder containing `apps.json` or `installs.json`.
- Select the source's menu to configure its **install folders**, **achievement save folders**, and, where applicable, **game-dump/definition folders**. Lists accept one folder per line and have Add folder buttons. Remove a line to remove a location.
- **Default profile/slot:** select the usual save slot or profile for that source. For example, Ghostship can use `save_0`; Alice can use a profile folder name. A per-game profile overrides this default.
- **Use standard AppData / Documents save locations:** leave enabled for ordinary installations. Turn it off to restrict automatic save selection to game-local paths and your configured folders. Nonempty configured save folders replace automatic save locations, so an unavailable relocated drive cannot silently select an older default save.
- **KytyPS5:** add the emulator folder, optionally override `Kyty.ini`, and optionally set a numeric user ID. With the user ID blank, the launcher's per-game user wins over its global user. Its game-folder list also helps locate games absent from Playnite's install path. An emulator folder may be selected as the emulator root, `_Trophies`, or one title's folder below `_Trophies`.

For example, configure `D:\Emulators\Kyty` once under Kyty emulator folders. If the game is found from Playnite, a configured library or Kyty's launcher settings, the extension reads its `sce_sys\trophy2` definitions and the matching `_Trophies\<title>` save for the active user. You do not select a JSON file for every game. If several trophy sets remain, choose that game's exact UCP and save in Local Overrides.

Shared folders accept the existing per-game path variables, including `{GameName}`, `{InstallDir}` and `{RomDir}`, plus Windows environment variables such as `%APPDATA%`. Shared console save/definition libraries must identify each game through a matching game-name or title-ID folder, or the selected schema's NPWR identity. A generic folder containing one unrelated game's save is not sufficient to associate it with another Playnite game.

Each source's page also has a **Menu icon** selector with a preview, **Browse image**, **Clear custom image**, and a platform-icon dropdown. Nintendo ports default to their original console: Link's Awakening DX HD uses Game Boy Color; Dusklight, Sunshine and Eclipse use GameCube; Ghostship and DKR-R use Nintendo 64; SMB Remastered uses NES. Custom images are copied into the extension's managed cache as PNG up to 64×64 pixels. A custom image takes priority over a borrowed platform icon. The menu and preview update while editing; Save applies the choice and Cancel keeps the saved settings and image. Clearing the selection does not delete the source image or the previously saved cache file. Location resets do not change icon selections.

**Reset this source's locations** resets only that source's location/profile settings in the edit copy. It does not change its enable switch, another source, or per-game overrides. Save commits the reset; Cancel discards it.

## Override or inspect one game

For a game, open **Manage Achievements → Local Overrides → Native Sources**:

1. Choose a source. The hint below it lists the files that source reads.
2. Leave the install folder blank to use the game's Playnite install directory, shared native libraries, Kyty's configured game folders, or an enabled Quiver library. Quiver 3 `apps.json` and Quiver 4 `installs.json` are supported; nothing is launched or installed.
3. Use **Inspect files**. Inspection reads the draft paths without saving overrides, downloading definitions or writing icons. Its report names the resolved install, unlock file and definition source, or explains why it cannot read them.
4. Select an exact unlock file when several profiles/slots are found, and an exact definition file when several trophy sets exist. The optional profile field matches a filename without its extension or its containing folder. Profiles and slots are never combined.
5. **Apply** saves the per-game override, selects Local for the game, and requests a refresh. **Clear override** restores automatic native detection; it does not delete achievements or game saves.

The install/save/schema fields accept the existing per-game path variables. Explicit file overrides are authoritative: a missing selected file never causes another profile to be adopted. Paths can point at a relocated save or game dump.

The **Kyty user ID for this game** field overrides the shared user preference and `Kyty.ini`. An exact save-file override takes priority over folder/user discovery. With no known active user, several users remain ambiguous and are never combined.

For a shared PS4 collection runtime log, select `u1`, `u2` or `u3` for Uncharted, or `p1`/`p2` for Prototype, and choose the corresponding trophy definition set. Events are matched to the last game context named by the launcher. Ambiguous collection events are rejected. Per-game trophy files do not need this field.

A successful change of native source, save/profile, definition file or collection selection replaces that selection's progress; cached unlock protection does not combine the previous selection's unlocks with the new one. Failed reads still leave the previous cache untouched.

| Source | Unlock state | Definitions and icons |
| --- | --- | --- |
| KytyPS5 | Emulator `_Trophies/<title>/trophies_<user>_<service>.json` | Game `sce_sys/trophy2/trophyNN.ucp`; reads configuration, English/default-language text and PNG icons. Configure the emulator folder once to resolve saves automatically. Title IDs and service labels are checked when available. |
| PS3 Recomp | `<install>/gamedata/trophies/NPWRnnnnn.json` | Original `PS3_GAME/TROPDIR/<NPWR>/TROPHY.TRP`, containing XML definitions and icons. A relocated runtime save needs an explicit file. |
| PS4 Recomp | `trophies.log`, `last_run.log`, `unlocked.txt`, `trophy/trophyNN.txt`; also user-folder snapshots, `trophies.json`, `trophies.dat` and `CUSA…-userN.bin` | `sce_sys/trophy/trophyNN.trp`, extracted `TROP.XML`, or runtime `trophies.tsv`. Encrypted TRP definitions use the adjacent `sce_sys/npbind.dat`. Select one trophy set per Playnite game. |
| Xbox 360 Recomp / ReXGlue | `achievements/<TITLEID>.toml`, `Achievements/<TITLEID>/<XUID>.tsv`, `SaveData/Achievements.json`; game-specific Documents folder also considered | Embedded JSON definitions when present, an explicit JSON schema, or cached public definitions from dbox.tools for a title-ID-named TOML/profile folder. The download switch is on the fork settings page. Definitions remain available offline after download. |
| MarkerPatch | `%LOCALAPPDATA%/EA Games/Dead Space 2/settings.txt`, `Controls.AcL.X/Y` | `<install>/achievements/txt/en.txt` and `achievements/img/<index>.png`. Requires patch achievement support enabled. |
| MadnessPatch | Documents `My Games/Alice Madness Returns/AliceGame/CheckPoint/<profile>/Achievements.txt`, `UnlockFlag` | `<install>/Binaries/Win32/Achievements/txt/en.txt` and `img/<index>.png`. Requires patch achievement support enabled. Select the active profile. |
| Link's Awakening DX HD | `%LOCALAPPDATA%/Zelda_LA/achievements`, or beside `portable.txt` | Installation `Data/Languages/achieve_eng.lng` and `Data/Achievements/achievement<N>.png`. |
| Dusklight | `%APPDATA%/TwilitRealm/Dusklight/achievements.json` | Bundled achievement definitions. `data_location.json` can select portable `data/` or a custom folder. |
| Ghostship | `<install>/saves/save_0.json` to `save_3.json` | Bundled definitions with progress goals. Select one slot. |
| DKR-R | `%APPDATA%/DKRPort/achievements/progress-v1.json`, or portable `dkr-runtime-data/achievements/progress-v1.json` | Bundled definitions with progress goals. |
| Super Mario Bros. Remastered | `%APPDATA%/SMB1R/achievements.sav`, or portable `config/achievements.sav` | Bundled definitions; the save is a text sequence of 0/1 flags. |
| SMS Launcher / Eclipse | `%APPDATA%/sms-launcher/achievements.txt` or `achievements-eclipse.txt`; portable launcher folder supported | Separate bundled definitions. Choose Eclipse explicitly so its unlocks are not interpreted as Sunshine achievements. |

## Scope and validation

This implementation reads the formats above. It does not execute launcher scripts, install patches, edit native saves, extract compressed/encrypted Xbox XEX executables, or read archived Kyty `.zar` game dumps. For those Xbox installations, use the title-ID definition lookup or supply JSON definitions. Arbitrary launcher-specific paths require per-game overrides. Native port artwork that is compiled into a game or fetched separately by AW Next is not bundled here.

JSON definition overrides accept an `achievements` array (or keyed object), with `id`/`name`, `displayName`, `description`, optional `icon`, `goal` and `gamerscore` fields. These schemas supply definitions only; unlocks always come from the source's selected save.

Missing, malformed, oversized or ambiguous files fail the read, preserving cached achievements. Unknown historical dates remain unknown. Native sources participate in existing refresh/notification processing; their monitor fingerprints deliberately remain unknown, so normal polling rechecks files, including saves first created after launch.

The readers have automated fixtures for the supported formats. Real-game and live Playnite validation is still needed, especially for launcher layouts and PS4 collection games. Use an appropriate per-game trophy file when a collection's log does not identify the running game.

Reference: [issue #93](https://github.com/Santodan/PlayniteAchievements/issues/93), [AW Next's source and folder settings](https://github.com/Shirowwww/Achievement-Watcher-Next/blob/main/docs/sources.md), and [AW Next parser sources at the original inspected revision](https://github.com/Shirowwww/Achievement-Watcher-Next/tree/9069ff6a6b3ef59837f42ac13d0f79513918fef0/app/parser). Bundled port definition data retains its upstream repository/license identifiers; notices ship with the extension.
