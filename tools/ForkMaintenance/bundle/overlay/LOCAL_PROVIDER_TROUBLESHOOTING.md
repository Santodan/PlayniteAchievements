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
