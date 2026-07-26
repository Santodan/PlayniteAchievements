<p align="center">
  <img
    src="BrandingPackage/01%20Logo%20(dark%20backgrounds)/02%20Crest%20Icon/PNG/pa-crest-gold-master-icon.png"
    alt="Playnite Achievements icon"
    width="128"
    height="128"
  >
</p>

<p align="center">
  <picture>
    <source
      media="(prefers-color-scheme: dark)"
      srcset="BrandingPackage/01%20Logo%20(dark%20backgrounds)/03%20Wordmark%20(stacked)/PNG/pa-stacked-wordmark-master-icon-white-text.png"
    >
    <source
      media="(prefers-color-scheme: light)"
      srcset="BrandingPackage/02%20Logo%20(light%20backgrounds)/03%20Wordmark%20(stacked)/PNG/pa-stacked-wordmark-master-icon-navy-text.png"
    >
    <img
      src="BrandingPackage/02%20Logo%20(light%20backgrounds)/03%20Wordmark%20(stacked)/PNG/pa-stacked-wordmark-master-icon-navy-text.png"
      alt="Playnite Achievements"
      width="350"
    >
  </picture>
</p>


<div align="center">

[![Release](https://img.shields.io/github/v/release/justin-delano/PlayniteAchievements?style=for-the-badge&logo=github&color=0ea5e9)](https://github.com/justin-delano/PlayniteAchievements/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-f59e0b?style=for-the-badge)](https://opensource.org/licenses/MIT)
[![Playnite SDK](https://img.shields.io/badge/Playnite%20SDK-6.14.0-6366f1?style=for-the-badge)](https://playnite.link/)
[![Downloads](https://img.shields.io/github/downloads/justin-delano/PlayniteAchievements/total?style=for-the-badge&label=Total%20Downloads&color=10b981)](https://github.com/justin-delano/PlayniteAchievements/releases)
[![Latest Release Downloads](https://img.shields.io/github/downloads/justin-delano/PlayniteAchievements/latest/total?style=for-the-badge&label=Latest%20Release%20Downloads&color=8b5cf6&cacheSeconds=3600)](https://github.com/justin-delano/PlayniteAchievements/releases/latest)

</div>

Playnite Achievements is a modern, performant, and fully customizable achievement extension for Playnite. PlayniteAchievements tracks achievements from you and your friends and provides a deep set of features that allow you to tailor your achievement collection and display to your exact taste.

Playnite Achievements features include:

* PC Storefronts, Retroachievements & emulator support  
  * Steam, GOG, Epic Games Store, Battle.net, EA app  
  * PlayStation Network, XBOX Live  
  * Retroachievements, RPCS3, ShadPS4, Xenia  
  * Final Fantasy XIV  
  * HoYoverse  
  * Exophase  
* Manual achievement support  
* Friends achievement data  
* Automatic syncing  
* Achievement unlock notifications (with screenshots/recordings)  
* Achievement groups  
* Custom Achievement Icons  
* Theme Migration  
* Hotkeys  
* Tags  
* Easy Maintenance

# **First Time Setup \- COMPLETE**

---

When you first start Playnite Achievements, you will be greeted by the landing page, with a list of achievement sources you can enable, and authenticate via the extension’s settings:

{33924F89-3A13-43B1-A568-68097D3ECE53}.png

{4EFFF0DD-FF08-44DB-82AA-83F38B3DE896}.png

{55508700-3C72-4EAE-93DD-FC63A9BD6293}.png

To authenticate with the various platforms PlayniteAchievements supports, navigate to the extension’s settings (or click the button on the landing page), and log into platforms using the Platforms tab.

{theme migration screenshot}

If your favorite Desktop theme was built to support SuccessStory, there is a useful Theme Migration function, which allows said theme to use PlayniteAchievements data instead. A limited migration will maintain full compatibility, while a full migration will enable new PlayniteAchievements specific features on all theme elements, such as new tooltip and click interactions for lists. A backup is made before all theme edits, and any theme migration can be reverted via a button in settings. 

When you trigger a refresh, PlayniteAchievements will fetch metadata from your selected sources.

{44FF0F2E-2707-4964-9D39-211DC5A4350B}.png  
Refresh progress is shown at the top of the overview window. There are various types of refreshes, which can fetch data from installed games or recent games, or you can perform a Full refresh to fetch achievement data for all of your games.

{7CAD2C9A-EF2E-4768-B852-76DD9C3BC1E7}.png

At the top right corner of the overview are your Score Cards. Your Collection Score increases as you collect achievements, with slight boosts for collecting rare achievements. Your Prestige Score increases as you unlock rare achievements, and common achievements are worth far less. Click the info button on either Score Card to learn more.

{DD774620-2171-431E-98C1-A5AE507FEE8B}.png

Congratulations\! PlayniteAchievements is now fully functional. This concludes the first time setup guide, please see the other sections for a more detailed showcase of more advanced and enthusiast features

# FRIENDS 

In addition to your own data, you can also retrieve achievement data for your friends. Steam, RetroAchievements, and Exophase are currently supported. First, navigate to the Friends tab in settings to get started:

{19B9D8DF-1A29-44BA-BBAE-E772ED016E24}.png  
Friends data can be viewed and refreshed by selecting the Friends tab in the Overview window.

{8B8838EA-0057-420E-B286-E743F6A06FAF}.png

Note, with many friends and many games, these refreshes can take a significant amount of time\! Shared/Recent refreshes will be the easiest to start with.

{B4F5B0E0-1336-49F3-9555-DC3F4E2DA4B4}.png

# **Customization \- COMPLETE \- SCREENSHOTS**

---

PA features a slew of customization options.

* Grid layout customizations  
  * Column widths, column orders, column cell alignments  
  * Separate per grid, for full flexibility  
* Color and font customizations  
  * All UI colors and fonts can be freely edited  
  * 20 Presets for easy experimentation  
  * Options to automatically follow Playnite theme colors.  
* Rarity and completion accents  
  * Add glowing borders to rare achievements or completed games.   
  * Color text by achievement rarity  
  * Show a special progress bar for completed games  
* Flexible Overview visualizations  
  * Pie charts for visualizing achievements per platform, rarity, or completion.  
  * Bar chart for showing achievement progress over time.

# **Per-Game Customization \- MOSTLY COMPLETE**

---

#### The Manage Achievements menu opens up another extremely powerful form of customization, with options available for each individual game in your library.

#### **Overview**

The overview tab allows you to see the general achievement status for the game. Additionally, you can export all per-game customized data to share with other users, or import their per-game customizations. 

#### **Capstones**

Some platforms automatically unlock an achievement to mark game completion, for example with Playstation and Platinum trophies. To generalize this, PlayniteAchievements developed a Capstone system, which can be used by games on all platforms. Unlocking an achievement that has been marked as a capstone will mark the game as completed. This is particularly useful when DLC/Multiplayer trophies should not count towards game completion.

#### **Categories**

Achievement can be manually (or, in some cases, automatically) organized into categories. Each category can have a set of Types (Singleplayer, Stackable, Missable) as well as a Label (“DLC \#1”). Categories are especially helpful for multi-game collections or games with many DLC packs.

 

#### **Filters**

Filtered achievements are hidden from your views. This is particularly useful for games with unobtainable or multiplayer-specific achievements. For example, achievements could be filtered out of Tomb Raider’s list for these reasons.

#### **Notes**

Notes can be added to each of a game’s achievements. This could be used to link to an achievement guide, mark if an achievement is bugged, or keep track of progress.

#### **Order**

Achievements can be freely reordered per game. This is useful for platforms which initially sort achievements alphabetically, and not in the order a player may achieve them.

For example, when Metal Hellsinger achievements are reordered, there is a very satisfying progression (seen in icons).

#### **Icons**

Locked and unlocked achievement icons can be individually customized, from web links or local files. By default, some platforms use very low-resolution images, and some are more high resolution. For comparison, here is an achievement from Flower, with Steam and Playstation Icons.

 Left: Steam 64x64 .jpg                      | Right: PlayStation 240x240 .png

#### **Overrides**

If a game is not behaving automatically, forced overrides can resolve these issues. Overrides force a game to use data from a specific platform. Games can also be excluded from refreshes in this way. 

# **Overlay \- COMPLETE**

PlayniteAchievements includes a robust achievement notification and tracking system. When achievements are unlocked while you are in a game, notifications appear, and screenshots or videos can be taken to record your progress.

Screenshots can be taken with/without the achievement notification on screen, or they can be taken with a full presentation frame. 

Unlock videos show the moment you earned an achievement, with a configurable amount of buffer time around the moment.

**Clean**

 **Notification**

 **Framed**

NOTE: Achievement notifications will not show for games that use DirectX 9 and lower in Exclusive Fullscreen mode.

Themes can create custom notification and frame styles, for full consistency. For example, in Aniki Remake:

**Toast**

 **Frame**

**Integration with Themes**

Any desktop theme created with SuccessStory support can migrate the theme to use PlayniteAchievements instead.

Many fullscreen themes can be migrated as well, but these complex themes are more likely to reveal compatibility issues. I recommend using **Aniki-ReMake** (link?) for the most comprehensive integration of PlayniteAchievements features.

**Integration with other Plugins \- COMPLETE**

PlayniteAchievements supports additional features when used with the following extensions:

#### **UniPlaySong**

When UPS is installed, achievement notifications are accompanied by custom musical jingles. Themes can also take advantage of this to have their own custom consistent jingles.

#### **StartPage**

PlayniteAchievements tables and visualizations can be added to StartPage, with their own separate customization, so you can create stunning dashboards like the example below:



## Docs

- [Open the wiki](https://github.com/justin-delano/PlayniteAchievements/wiki)

## Support / Contributing / Credits

- Issues: [GitHub Issue Tracker](https://github.com/justin-delano/PlayniteAchievements/issues)
- Discussions: [GitHub Discussions](https://github.com/justin-delano/PlayniteAchievements/discussions)
- Support: [Ko-fi](https://ko-fi.com/justindelano)
- Translations: contributions are welcome through pull requests and localization updates

License: [MIT](LICENSE).

