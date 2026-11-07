# Local Provider Troubleshooting

This guide covers the `Local` achievement provider in this fork. It is intended for games whose unlock state is stored in local emulator, Steam-compatible, or custom achievement files rather than fetched from a normal platform account.

## Quick setup checklist

1. Open **Add-ons > Extension settings > Playnite Achievements > Providers > Local**.
2. Enable the Local provider.
3. If the saves are outside the automatically scanned locations, add their parent folder under **Additional folders to scan** and click **Apply**.
4. Right-click the game and open **Playnite Achievements > Local**, or open **Manage Achievements > Overrides > Local**.
5. Set the correct Local folder. If the game is being identified as Steam (for example with GreenLuma or SteamTools), change its preferred provider to **Local**.
6. Refresh that game from Playnite Achievements.

The Local folder must contain a supported achievement-state file, or one of its subfolders must contain one. Common names recognized by the provider include `achievements.json`, `achievements.ini`, `user_stats.ini`, and Steam appcache stats files. A metadata schema may be needed to turn internal API keys into names, descriptions, and icons.

Quickest way to check if the game is generating the achievement, search for the appid in in the whole pc.
If you find a folder with the name of the app id, check if it has the `achievements.json`, `achievements.ini` or `user_stats.ini` inside.

If you want to make sure that the game generates the achievements file, apply GSE ( my recommendation ):
- Inside the game's folder, search for the `steam_api64.dll`
- Get the release from https://github.com/alex47exe/gse_fork_tools
- You will have the folder `generate_emu_config`
- Open a terminal window in that folder and run the command `generate_emu_config.exe <appID>`
- Insert the steam credentials and then the verification code
    - If you don't want to insert the credentials every time, you can create a filde in the same folder with the name `my_login.txt` and the first line will be the username and the second line the password
- It will generate all the files inside the `_OUTPUT\<appid>`
- Copy all those files to the same folder where you found the `steam_api64.dll` in the game's folder
- The game will start to generate the achievements into the folder `%appdata%\GSE Saves\<appid>` after unlocking the first achievement

### Example:
I test things with the game `100 Find Kitties Kitty house` since it is easy to get achievements
If you search in steam, you will have the link https://store.steampowered.com/app/3220090/100_Find_Kitties_Kitty_house/
The `appid` are the numbers between the `app` and the game's name, so for this game it is `3220090`
I've found the `steam_api64.dll` in `100 Find Kitties Kitty house\find_kitty_house_Data\Plugins\x86_64`, I rename it only to keep a backup
I've run the command `generate_emu_config.exe 3220090` from the location where I've download it.
```
E:\Things\GSE Fork Tools\generate_emu_config>generate_emu_config.exe 3220090
Internet Connection - Online, Google OK via https://www.google.com
Enter the Steam 2FA code emailed to you: 8F4QD

*** STARTED config for app id 3220090 ***

[ ] Found app id on Steam store
[ ] Found app name on Steam store
[ ] __ orig name: '100 Find Kitties: Kitty house'
[ ] __ safe name: '100 Find Kitties Kitty house'
[ ] DEF_DIR = E:\Things\GSE Fork Tools\generate_emu_config\_DEFAULT
[ ] OUT_DIR = E:\Things\GSE Fork Tools\generate_emu_config\_OUTPUT\3220090
[ ] Copying preset emu configs...
[ ] __ default emu config from <DEF_DIR>\0 folder
[ ] __ preset emu config from <DEF_DIR>\1 folder
[ ] Found product info --- writing to <OUT_DIR>\steam_misc\app_info\app_product_info.json
[ ] Found app details --- writing to <OUT_DIR>\steam_misc\app_info\app_details.json
[?] No stats found - skip creating <OUT_DIR>\steam_settings\stats.json
[ ] Found 100 achievements --- writing to <OUT_DIR>\steam_settings\achievements.json
[ ] Found 2 achievements images --- downloading to <OUT_DIR>\steam_settings\img folder
[ ] Found 103 supported languages --- writing to <OUT_DIR>\steam_settings\supported_languages.txt
[?] No DLCs found - skip writing to <OUT_DIR>\steam_settings\configs.app.ini
[ ] Found 2 depots --- writing to <OUT_DIR>\steam_settings\depots.txt
[ ] Found 1 branch --- writing to <OUT_DIR>\steam_settings\branches.json
[ ] __ default branch name: public, latest build id: 22019218
[?] No controller configs found - skip generating action sets
[?] No inventory items found - skip creating <OUT_DIR>\steam_settings\items.json & default_items.json
[ ] Detected app exe: 'find_kitty_house.exe'

*** FINISHED config for app id 3220090 ***
```
Then copy all the files from `_OUTPUT\3220090` to `100 Find Kitties Kitty house\find_kitty_house_Data\Plugins\x86_64`
After getting the first achievement, I can see that the file now exists at `%appdata%\GSE Saves\3220090`


## What is scanned automatically

The settings page shows the authoritative list. It includes Steam `userdata` and `appcache\stats`, common Steam installation roots on available drives, and common locations for OnlineFix, RUNE, CODEX, Goldberg/GSE, EMPRESS, SKIDROW, SmartSteamEmu, and CreamAPI saves.

- **Steam path (optional):** use **Auto Detect**, or point it at the Steam installation folder.
- **Steam appcache stats user:** leave this on automatic to scan every detected local user, select one user to avoid cross-account matches, or choose **None** to skip Steam-related automatic folders.
- **Additional folders to scan:** add nonstandard roots. Environment variables such as `%APPDATA%` and `%PUBLIC%` are expanded.
- **Excluded folders from scan:** excludes that folder and all nested paths. Check this list whenever a valid save is unexpectedly ignored.

## Game is not detected

Work through these checks in order:

1. Confirm the Local provider is enabled.
2. Confirm the game is not excluded from Playnite Achievements refreshes.
3. Add the save root under **Additional folders to scan**, then click **Apply**. Browsing to a folder alone does not add it.
4. Use the game's **Local folder** override to bypass automatic matching.
5. If Playnite has no usable platform ID, set a **Steam App ID** override. This is the numeric ID only, not a store URL.
6. For LumaPlay/Ubisoft data, select the relevant `LumaPlay.ini`; the provider reads the Uplay game ID from it and can read unlock state from the registry.
7. Refresh only that game while testing.

If multiple folders match, the provider can display a warning. Select the correct folder in **Manage Achievements > Overrides > Local**; this also silences that game's ambiguity warning.

## A game is detected as the wrong provider

Steam-compatible tools can make a local game look like an ordinary Steam game. Right-click the game, open the Playnite Achievements Local menu, and change the provider to **Local**. The same setting is available under **Manage Achievements > Overrides > Local**.

Provider selection and folder selection solve different problems: the provider override chooses Local; the folder override tells Local which save to read. Some games need both.

## Achievements have API keys, generic text, or missing icons

Unlock-state files often contain only keys such as `ACH_WIN_FIRST_MATCH`. The Local provider correlates those keys with a schema to obtain display names, descriptions, locked/unlocked icons, and global rarity.

Try the following:

1. Set the correct Steam App ID override.
2. Check **Steam path** so local Steam schema/appcache data can be found.
3. Change **Anonymous metadata source priority**. The available priorities are Steam sources, SteamHunters, and Completionist.me.
4. Use a **custom schema** in the game's Local Overrides when automatic sources cannot identify the game.

SteamHunters can expose API names and is usually a better match for key-only Goldberg-style JSON. Completionist.me and Steam Community are title-based fallbacks, so they may not correlate with files that contain only API keys. Public metadata lookup also requires an internet connection, but reading local unlock state does not.

## Unlock state is wrong or belongs to another user

- Select the correct **Steam appcache stats user** globally, or set a per-game Steam-user override.
- Verify the selected Local folder does not belong to another account or an older installation.
- Clear an incorrect folder, App ID, user, schema, or provider override and refresh again.
- Do not point an additional scan root at a large backup tree containing duplicate saves; exclude the backup or set a precise per-game folder.

Cached achievement data may remain visible when a metadata source is temporarily unavailable. This protects useful local state, but it also means that changing an override should be followed by a game refresh before judging the result.

## Newly unlocked achievements do not appear

- Enable **Refresh achievements when a game closes**, or refresh the game manually.
- For live updates, enable real-time monitoring in the main **Achievement Notifications** tab. See [Achievement Notifications Guide](ACHIEVEMENT_NOTIFICATIONS_GUIDE.md).
- Confirm notifications have not been disabled for that individual game.
- Check that the achievement file actually changed. The monitor avoids reparsing an unchanged source file.
- If the game writes its save only on exit, a live notification cannot occur before that write; the final post-stop check or refresh-on-close is the appropriate path.

## Imported Local games

The Local settings can scan folders and import detected games, and can import Local entries from SuccessStory JSON. Before importing, choose the target library/source, metadata source, and how existing games should be handled. Importing creates or matches library entries; it does not fix a wrong folder or App ID for an already matched game.

## Collecting useful diagnostics

When reporting a problem, include:

- game name;
- the selected provider, Local folder, Steam App ID, Steam user (if applicable), and custom-schema overrides(if applicable);
- the achievement-state filename and a redacted example of its structure;
- whether a manual refresh succeeds;
- whether the issue affects detection, metadata, unlock state, or notifications;
- the relevant Playnite log - `Playnite\ExtensionsData\e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b\playniteachievements.log` .

Do not publish account tokens, cookies, full user profiles, or save files containing personal data.

## Reduce CPU usage while a game is idle

Open **Add-ons > Extension settings > Playnite Achievements > Santodan-Fork > Performance**.
These optional settings reduce repeated Local-provider work while a game is running.
They do not change your configured polling interval. Brief activity at game start
or when processing an unlocked achievement can still occur.

### Browser startup choices (CEF and WebView2)

The **Browser startup** group is independent of custom Local polling:

- **Defer Steam CEF cookie loading**: enable to skip the cookie-browser read at
  extension startup. The first authenticated Steam request loads cookies when
  needed. Steam login and browser fallbacks remain available. Default: off.
- **Preload fork notification browser (WebView2)**: disable to avoid keeping the
  hidden notification warm-up browser. Browser notifications and previews still
  initialize on demand, so their first use can take longer. Default: on.

For less eager startup work, enable deferred Steam cookie loading and disable
notification browser preload. Save and restart Playnite when changing these
options; disabling preload does not dispose a browser already initialized in
that session. CEF (Playnite's embedded browser) and WebView2 (the fork notification
browser) are separate engines. Account authentication and metadata features can
still create browser views when needed; these switches do not disable them.

### Background work choices

These settings also work without custom Local polling:

| Setting | Meaning and tradeoff | Default |
| --- | --- | --- |
| **Build overview data only when needed** | Skips automatic overview precomputation at startup and after changes. The overview and other consumers still build fresh data when needed, so their first load can be slower. This goes beyond the existing option that defers overview warm-up only during gameplay. | Off |
| **Preload Steam-user menu choices** | Disable to skip the startup discovery scan. Local menu choices are discovered asynchronously when first requested; reopen the menu after discovery to see them. Save and restart to compare startup behavior. | On |
| **Watch capture folders for external changes** | Disable to remove recursive screenshot/video filesystem watchers. Extension-created captures still invalidate their game data. Reopen the capture viewer to rescan external additions/removals; other cached indicators may remain unchanged until a refresh. Save and restart to apply. | On |
| **Defer scheduled refreshes during gameplay** | Holds scheduled current-user and friend refresh checks while any Playnite game is running. Checks resume within about 15 seconds after gameplay ends and reassess whether a refresh is still due. It does not stop a refresh already running or change live polling, manual refreshes, or game-close refreshes. | Off |

For less background work, enable **Build overview data only when needed** and
**Defer scheduled refreshes during gameplay**, disable **Preload Steam-user menu
choices**, and disable **Watch capture folders for external changes** if automatic
updates for externally managed captures are not needed. Save and restart Playnite.
Keep the last option enabled if you regularly add or delete captures externally
and want the open UI to notice those changes automatically.

Existing scheduled-update frequencies, friends features, capture/recording options,
and notification appearance remain in their own settings pages. Disabling unused
features there can avoid their work entirely; these Performance controls manage
preparation, caching, and scheduling without duplicating those feature switches.

### Experimental artwork, previews and memory options

The Experimental notice and **Restore defaults** button stay visible above the
scrolling options. Restore defaults resets only the Performance tab settings;
click **Save** to apply or **Cancel** to discard. Restart Playnite afterward to
fully apply startup, cache, and artwork changes. Other provider and notification
settings, including General Debug mode, are preserved.

The Performance tab is marked **Experimental**. These controls are provided as-is,
without guarantees. Enable them at your own risk and restore defaults when
troubleshooting. They can change appearance and first-use latency; they do not
promise a fixed total RAM or CPU limit.

| Setting | Meaning and tradeoff | Default |
| --- | --- | --- |
| **Use static WPF artwork** | Shows still artwork instead of GIF/WebP animation handled by the extension's WPF image controls. It does not change images animated inside custom HTML/WebView2 templates. Restart to release already running artwork. | Off |
| **Animated WebP cache budget (MB)** | Limits retained decoded WebP cache entries to 8?128 MB. Smaller values may reduce resolution/frame retention and require decoding again. Visible controls can retain frames separately, so this is not a total animation-memory limit. | 128 MB |
| **Manual notification previews** | Disables automatic fork inline preview rebuilding, automatic popup previews and preview loops. Use the existing Preview/Test buttons for an explicit notification preview. | Off |
| **Release notification previews when leaving the tab** | Drops the fork's inline preview content on unload. The settings overlay closes too, as before. Returning can rebuild the preview if automatic previews are enabled. Live notifications and the separate browser startup preload are unaffected. | Off |
| **Reduce fork notification effects** | Removes WPF notification shadow/outline effects and window entrance/exit transitions, and simplifies CSS effects and animation timing in generated SAN notifications. Saved styles remain unchanged. Custom scripts can still perform their own work. | Off |
| **Decoded sound cache (seconds)** | Retains up to 0?60 seconds of decoded audio clips in the helper process. Zero disables decoded-clip caching and preload decoding; active playback still needs memory. Smaller limits can increase decoding at playback. Save and restart to apply. | 60 seconds |

For a conservative memory comparison, try a **32 MB** animation cache and **10
seconds** of decoded sound. For fewer animations, also enable static WPF artwork,
manual previews, preview release, and reduced effects. Save and restart, compare
idle use, then test opening the notification settings, explicit WPF/SAN previews,
live unlocks, sounds, and an animated artwork example. If first-use delays or
appearance changes are undesirable, restore the corresponding default. Cache
reductions do not guarantee lower CPU: decoding the same assets again costs work.

### Recommended starting configuration

Enable **Use custom Local polling mechanism**, then enable all the options in its
**Custom Local polling** group. Keep your preferred polling interval; five seconds
is a useful starting point when notification timing matters. Save settings, stop the
game, and launch it again before comparing CPU usage.

Leave **Debug mode** in **Santodan-Fork > General** and **Sample process CPU by
thread** off for normal use. They collect diagnostic information rather than
reduce CPU usage. Thread sampling adds measurement overhead.

The two options for notification summaries and theme analysis are separate from
polling. Enable them if you want caching for those fork features; they are not
required to reduce idle Local polling work.

### What each Performance setting does

| Setting | Meaning |
| --- | --- |
| **Use custom Local polling mechanism** | Checks supported source files for changes and skips a full refresh when they are unchanged. The other options in this group require it. Disable it to return to upstream polling. |
| **Limit authentication checks to Local** | Prevents a custom Local poll from checking unrelated platform accounts. This avoids repeatedly opening a Steam authentication browser, which can otherwise happen even without a linked Steam account. Manual refreshes and other providers keep their normal authentication checks. |
| **Check available sources while waiting for an achievement file** | Checks other supported source files even while the monitor is waiting for its expected achievement file. If those sources are known and unchanged, it can skip a full refresh. If no usable source fingerprint is available, it refreshes normally. |
| **Reuse folder discovery within each poll** | Reuses identical folder-search results during one poll, including refresh and monitor setup. Results expire after that poll, so a missing file is searched for again next poll. Achievement contents are still read fresh. |
| **Use lightweight game-start discovery** | Checks a previously validated achievement-file path before scanning folders at game start. Missing files or invalid records fall back to normal discovery. It needs a successful refresh with this option enabled to prepare the path. |
| **Reuse a validated startup baseline** | Avoids the first full polling refresh when a successful refresh from the last 24 hours recorded matching source files, settings and cached data before this game session. Changed or unavailable data triggers normal refresh. |
| **Cache installation-folder discovery** | Reuses discovered installation folders across polls within a game session. Directory or relevant settings changes invalidate the cache. Unlike per-poll reuse, this cache can survive more than one poll. |
| **Reuse SteamHunters category metadata** | Reuses cached achievement categories during custom polling while reading fresh unlocks and progress. Manual refreshes still fetch metadata according to the provider settings. |
| **Defer background overview rebuilds during gameplay** | Cancels pending background overview precomputation when gameplay begins. Opening the overview can still load fresh data. |
| **Cache notification score summaries** | Reuses library score summaries for the fork notification's score wildcards and progress bars. Game changes invalidate the summaries. |
| **Cache theme-content analysis** | Reuses content-analysis results in the fork's Theme Migration tab. Files are still read to detect edits. It does not change upstream migration pages. |
| **Sample process CPU by thread** | With Debug mode enabled, records process CPU and the busiest operating-system threads every second in `PollingDebug.log`. Use only when investigating CPU activity. |

Custom polling, startup reuse, lightweight startup, and the three authentication/
source-check/per-poll discovery switches default to off. Installation-folder caching,
category reuse, and overview deferral default to on but only apply when custom
polling is enabled. Notification-summary caching, theme-analysis caching, and CPU
thread sampling default to off.

### Check that it helps

1. Complete one normal game refresh or launch/quit cycle with your options enabled
   to prepare startup records. The first run can still perform the normal scans.
2. Record an idle period without unlocking achievements, then unlock an achievement
   and verify that its notification arrives at the expected time.
3. If idle activity remains high, compare one option at a time, restarting the game
   between runs. Record the times of game launch and achievement unlocks.
4. For investigation, enable Debug mode and optionally thread sampling, save, and
   collect a fresh `PollingDebug.log` alongside PerfView and your CPU recording.
   Turn diagnostics off afterward.

A poll can start asynchronous work that continues beyond the second it began, so
CPU activity between polling timestamps does not by itself rule polling out.
Task Manager and psrecord also use different CPU percentage scales and sample
windows; compare recordings made with the same tool and conditions.

For implementation details and startup-record validation, see
[Optional fork performance changes](docs/ForkPerformanceOptions.md).
