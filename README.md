<p align="center">
  <img src="Images/big-icon.png" alt="Playnite Achievements icon" width="128" height="128">
</p>

<h1 align="center">Playnite Achievements</h1>

<div align="center">

[![Release](https://img.shields.io/github/v/release/justin-delano/PlayniteAchievements?style=for-the-badge&logo=github&color=0ea5e9)](https://github.com/justin-delano/PlayniteAchievements/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-f59e0b?style=for-the-badge)](https://opensource.org/licenses/MIT)
[![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.14.0-6366f1?style=for-the-badge)](https://playnite.link/)
[![Downloads](https://img.shields.io/github/downloads/justin-delano/PlayniteAchievements/total?style=for-the-badge&label=Total%20Downloads&color=10b981)](https://github.com/justin-delano/PlayniteAchievements/releases)
[![Latest Release Downloads](https://img.shields.io/github/downloads/justin-delano/PlayniteAchievements/latest/total?style=for-the-badge&label=Latest%20Release%20Downloads&color=8b5cf6&cacheSeconds=3600)](https://github.com/justin-delano/PlayniteAchievements/releases/latest)

</div>

<p align="center">
Achievement tracking for Playnite across Steam, RetroAchievements, GOG, Epic, PSN, Xbox, RPCS3, ShadPS4, Xenia, and Exophase.
</p>

<p align="center">
  <img src="Images/sidebar-view1.png" alt="Sidebar overview" width="900">
</p>

## Recent Updates

| Version | Highlights |
| --- | --- |
| 1.6.2 | Cache fixes, pie chart/display fixes, option to hide the top menu button |
| 1.6.1 | Exophase manual links, new category types, sidebar popout, display-settings cleanup |
| 1.6.0 | Game Options window, manual tracking, categories, custom order, summary exclusions, multi-select filters |

Docs: [Wiki Home](https://github.com/justin-delano/PlayniteAchievements/wiki) · [Release notes](InstallerManifest.yaml)

## Why Switch From Legacy

| Area | Legacy | PlayniteAchievements |
| --- | --- | --- |
| Theme integration | Legacy bindings | Legacy + modern bindings, Theme Migration, backup, revert |
| Views | Plugin window, one-game view | Sidebar, selected-game drilldown, top-panel popout |
| Charts and layout | Main plugin charts | Contextual pie charts, timeline, trophy charts, resizable/hideable grid columns |
| Refresh | Recent / installed / full style passes | Background refresh, single-game refresh, Custom Refresh, presets, include/exclude targeting |
| Per-game control | Separate fixes | One Game Options window: manual tracking, capstones, categories, order, exclusions, RA / Exophase overrides |
| Completion rules | Provider completion / 100% | Capstones, summary exclusions |
| Manual carryover | Rebuild manual links | Import legacy manual links from the Manual tab |
| Cleanup | Refresh/export tools | Cache clear, image-cache clear, CSV export, data/log folder access |

Docs: [Migration docs](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Supported Platforms

| Type | Platforms |
| --- | --- |
| Store / service providers | Steam, GOG, Epic Games Store, PlayStation Network, Xbox Live, RetroAchievements |
| Emulator trophy providers | RPCS3, ShadPS4 |
| Extra tracking layers | Manual, Exophase |

Docs: [Provider setup in the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## What You Actually Get

| Area | What you can do |
| --- | --- |
| Sidebar | Drill into one game, filter by provider/completion/category/type, and use contextual pie charts plus timeline data |
| Refresh | Run quick refreshes or save a Custom Refresh preset with exact providers and game scope |
| Game Options | Fix one game's provider, add manual tracking, set capstones, categories, order, and exclusions |
| Library | Sync Playnite tags, set completion status, open a top-panel popout, clear cache, export CSV, and open logs |

Docs: [Settings and library tools](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Views, Filters, and Refresh Workflows

- Sidebar filters by provider, completion state, game type, and category
- Pie charts change with the selected game where that context makes sense
- Refresh modes include recent, installed, favorites, full, single-game, and background updates
- Custom Refresh adds platform pickers, scope rules, include/exclude lists, overrides, and saved presets
- The top menu button can open the sidebar popout window

<img src="Images/sidebar-view2.png" alt="Sidebar single-game view" width="900">

> Screenshot placeholder: [custom-refresh-dialog]

Docs: [Refresh workflows](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Game Options and Per-Game Control

| Tab | What it does |
| --- | --- |
| Overview | Provider, source, last update, completion, cache actions |
| Manual Tracking | Link a source game, mark unlocks, store unlock times |
| Capstones | Pick the achievement that marks completion |
| Categories | Add type flags and category labels |
| Achievement Order | Set a custom drag-and-drop order |
| Overrides | Exclusions, summary exclusions, RetroAchievements override, Exophase override |

> Screenshot placeholder: [game-options-overview]
>
> Screenshot placeholder: [manual-tracking-tab]
>
> Screenshot placeholder: [categories-tab]
>
> Screenshot placeholder: [achievement-order-tab]

Docs: [Game Options in the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Theme Integration and Migration

| Theme path | What you can do |
| --- | --- |
| Legacy themes | Keep using the legacy surface while switching plugins |
| Modern themes | Use modern Playnite Achievements controls and bindings |
| Migration modes | Limited, Full, or Custom |
| Safety | Backup before changes, revert after changes |

<p align="center">
  <img src="Images/aniki-remake1.png" alt="Theme integration example" width="900">
</p>

> Screenshot placeholder: [theme-migration-tab]

Docs: [Theme migration in the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Installation and First-Time Setup

1. Download the latest `.pext` from [Releases](https://github.com/justin-delano/PlayniteAchievements/releases/latest).
2. Install it from Playnite's addon browser or drag the file into Playnite.
3. Open `Settings -> Extensions -> Playnite Achievements`.
4. Enable the providers you want and finish their setup.
5. Open the sidebar or top panel entry and run your first refresh.

If you are importing manual links from a legacy setup, do that first from the Manual tab. If you are moving a theme over, open Theme Migration before you start editing files by hand.

<p align="center">
  <img src="Images/setup-view.png" alt="Setup view" width="900">
</p>

Docs: [First setup in the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Docs

Full docs live in the wiki.

- [Open the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Support / Contributing / Credits

- Issues: [GitHub Issue Tracker](https://github.com/justin-delano/PlayniteAchievements/issues)
- Discussions: [GitHub Discussions](https://github.com/justin-delano/PlayniteAchievements/discussions)
- Support: [Ko-fi](https://ko-fi.com/justindelano)
- Translations: contributions are welcome through pull requests and localization updates

Thanks to the Playnite Discord testers, theme authors, and everyone who kept reporting edge cases.

License: [MIT](LICENSE).

