<p align="center">
  <img src="Images/big-icon.png" alt="Playnite Achievements icon" width="128" height="128">
</p>

<h1 align="center">Playnite Achievements - Santodan Fork</h1>

<div align="center">

[![Release](https://img.shields.io/github/v/release/Santodan/PlayniteAchievements?style=for-the-badge&logo=github&color=0ea5e9)](https://github.com/Santodan/PlayniteAchievements/releases/latest)
[![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.14.0-6366f1?style=for-the-badge)](https://playnite.link/)
[![Downloads](https://img.shields.io/github/downloads/Santodan/PlayniteAchievements/total?style=for-the-badge&label=Total%20Downloads&color=10b981)](https://github.com/Santodan/PlayniteAchievements/releases)
[![Latest Release Downloads](https://img.shields.io/github/downloads/Santodan/PlayniteAchievements/latest/total?style=for-the-badge&label=Latest%20Release%20Downloads&color=8b5cf6&cacheSeconds=3600)](https://github.com/Santodan/PlayniteAchievements/releases/latest)

</div>

> Official documentation for the base project should be taken from the original repository.
>
> - Original repository: https://github.com/justin-delano/PlayniteAchievements
> - Official wiki / documentation: https://github.com/justin-delano/PlayniteAchievements/wiki
> - Official releases: https://github.com/justin-delano/PlayniteAchievements/releases
> ## Quick Notes For Users Of This Fork
>
>- If you need setup instructions for Steam, GOG, Epic, RetroAchievements, PSN, Xbox, RPCS3, ShadPS4, Xenia, or Exophase, use the upstream wiki first.
>- If you are using this fork specifically for Local support, the main differences are the Local provider, Local overrides, Local compatibility fixes, and Local-focused refresh / notification work listed above.

## What This Fork Focuses On

- Local save support and local achievement recovery workflows
- Compatibility work for non-standard Steam and local setups
  - If you are using Steam local saves ( GreenLuma or SteamTools, for example) the game will be detected as been from steam, you will need to override the provider to Local
- Per-game Local overrides inside Game Options
- Faster access to saved refresh presets from the main sidebar refresh selector
- Local-only realtime monitoring and notification improvements

## Screenshot Gallery

The gallery below is intentionally reserved for screenshots of fork-specific features only.

The current screenshot files in the repo are mostly generic plugin views inherited from the broader project. Since you want this README to highlight only the changes made in your fork, the sections below are placeholders for the fork-specific screenshots you plan to add later.

### Local Provider Folder List And Browse Flow

You can set any custom folder where you have saves located in your system

<img src="Images/LocalAchivementsFolders.png" alt="Sidebar single-game view" width="900">

### Local Game Options Overrides

If you Right-click the game it will show a quicker way to do these changes:

<img src="Images/LocalAchivementsRightClickOptions.png" alt="Sidebar single-game view" width="900">

From the PlayniteAchivements menu inside the game

<img src="Images/LocalAchivementsOverrides.png" alt="Sidebar single-game view" width="900">

### Local Realtime Monitoring And Sound Settings

This was a request from a user to have a sound playing when an achivement is unlocked while playing a game.

<img src="Images/LocalAchivementsNotification.png" alt="Sidebar single-game view" width="900">.


## Fork Changelog

The entries below are fork-side changes, grouped by date. When a date includes an upstream sync, only the fork-specific additions are called out here.

### 2026-04-15

- Synced the fork forward to the upstream `v2.1.0` codebase.
- Added near-real-time Local achievement monitoring for the currently running game only, instead of polling the whole library.
- Added Local unlock notifications with sound playback.
- Added bundled Local notification sounds plus a separate custom sound-path override.
- Added a Local test notification action so the configured notification + sound can be tested from settings.
- Set the Local default bundled sound flow to use a bundled fallback instead of requiring a custom path.
- Added quick access to saved custom refresh presets from the main sidebar refresh selector.
- Changed the sidebar refresh selector so presets are grouped under a `Presets` menu entry instead of being mixed into the top-level refresh list.
- Fixed Local-only preset targeting and estimation so Local presets only count games that the Local provider can actually resolve.
- Improved extra custom local-save-folder handling so it works as a list with browse support instead of a single raw path.

### 2026-04-11

- Fixed Local save handling for RUNE and OnlineFix layouts.

### 2026-04-08

- Changed Local behavior so cached Local achievement data is preserved when Steam API access is unavailable instead of trying to refetch everything and losing useful local-state visibility.

### 2026-04-07

- Added GreenLuma / SteamTool compatibility improvements.

### 2026-04-06

- Added per-game Local Steam App ID overrides in Game Options.
- Added per-game Local folder overrides in Game Options.

### 2026-04-01

- Added compatibility with the StartPage extension.
- Updated post-build handling to improve `Toolbox.exe` detection and execution.
- Synced the fork state around the upstream `v2.0.2` release.

### 2026-03-31

- Updated the fork for the upstream `2.0.0` / `2.0.1` transition.
- Renamed and reshaped the old cracked-save workflow into the Local provider flow.
- Added support for showing locked achievements even when `achievements.json` does not exist, as long as Local schema / cache data can still resolve them.
- Updated local extension naming and IDs for the fork.
- Replaced `CrackedSavesProvider` with `LocalSavesProvider`.

### 2026-03-29

- Added initial support for Local saves.
- Cleaned up leftover provider debug-path behavior during the early Local provider work.



## Upstream Docs And Credits

- Upstream project: https://github.com/justin-delano/PlayniteAchievements
- Upstream documentation: https://github.com/justin-delano/PlayniteAchievements/wiki
- Upstream releases: https://github.com/justin-delano/PlayniteAchievements/releases
- Santodan fork: https://github.com/Santodan/PlayniteAchievements

