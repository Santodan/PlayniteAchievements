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
