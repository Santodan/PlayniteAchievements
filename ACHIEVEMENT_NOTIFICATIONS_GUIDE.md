# Achievement Notifications Guide

The fork's custom Achievement Notification replaces the extension's standard unlock toast with a styled overlay and/or Windows toast while a game is running. Everything described here is under **Add-ons > Extension settings > Playnite Achievements > Santodan-Fork > Achievement Notification**, which has three pages: **General**, **Notification Style**, and **Memories**.

Unlock detection uses the extension's normal in-game monitor, so it works for every provider that can be refreshed while you play (Local, Steam, RetroAchievements, and so on), not only Local games. Local-specific setup is covered in [Local Provider Troubleshooting](LOCAL_PROVIDER_TROUBLESHOOTING.md).

## Enable notifications

1. Open **Santodan-Fork > Achievement Notification > General**.
2. Under **Live unlock detection**, enable **Use custom Achievement Notification for live unlocks**. Most notification controls stay disabled until this is on.
3. Choose a **Polling interval (seconds)**. The supported range is 1–60 seconds and the default is 5 seconds. While this option is enabled, it replaces the upstream in-game polling interval.
4. Under **Notification delivery**, select a **Delivery mode** and check the in-app/system switches (see below).
5. Open **Notification Style**, pick a style, a preview game and a preview achievement, then click **Test Notification** in **Sound Notification** before launching a game.
6. Click **Save** in the settings window.

For Local games, also enable the **Local** provider under **Santodan-Fork > Platforms > Local**.

When a game starts, the monitor takes a baseline. Achievements already unlocked in that first read are not announced. It then refreshes the running game at the polling interval and announces only achievements that were newly unlocked since the baseline.

## Notification template

**Use upstream notification template** (General page) switches between the two renderers:

- **Unchecked (default):** the Santodan-Fork custom notification is used for live unlocks, screenshots and video overlays. Delivery and style are configured on these pages.
- **Checked:** the extension's upstream notification is used instead. Configure its delivery and appearance in the upstream **Notifications** settings tab. **Test Notification** then sends an upstream preview.

## Delivery

- **Windows toast only:** sends a system notification without an overlay.
- **Overlay only:** shows the styled in-app overlay.
- **Hybrid (Overlay + Toast):** uses both. This is the default.

The **Show in-app notification when achievement is unlocked** and **Send System notification** switches can further enable or disable the two outputs. If a selected delivery mode appears to do nothing, confirm that its matching switch is enabled.

Other delivery options:

- **Use a single custom notification:** a new unlock replaces the visible notification and restarts its display time. Disable it to show separate, stacked notifications for a batch of unlocks.
- **Show overlay on the active game's monitor:** the overlay follows the game between monitors. Use windowed or borderless fullscreen.
- **Debug achievement notification logging:** writes notification diagnostics to `AchNotifDebug.log` in the extension data folder.

The custom notification still respects the upstream notification switches, provider notification overrides and the game's own notification settings in **Manage Achievements > Notifications**. If those disable unlock notifications for a provider or game, the custom notification is skipped too.

## Styles and templates

**Notification Style** contains:

- **Default style** and **Provider override:** choose Steam, PlayStation, Xbox, Minimal or Custom globally, then optionally choose a different style for Local, Steam, Epic, GOG, PlayStation, Xbox or RetroAchievements.
- **Preview game** and **Preview achievement:** sample data for previews, including the game's cover and banner. **Preview Selected Style** and **Preview All Styles** show the styles without sound.
- **Custom style slot:** **Save Slot**, **Load Slot**, add/remove slots and **Clear all slots**.
- **Import Template**, **Export Template** and **Open Builder**.

The Custom style editor supports layout, up to six text lines, text templates with wildcards, conditional text rows, colors and gradients, font treatments, primary/secondary icon sources and custom icons, rarity glow, game cover and banner artwork, progress bars, transitions (fade, slide, circle and SAN transitions), SAN elements, opacity and size. **Inline real-time preview** updates while you edit.

Text templates support optional groups such as `<achievementName>[[ (<progress> -)]]`. A group appears only when every wildcard inside it has a value; nesting is not supported. See [the progress bar notes](tools/NotificationTemplates/PROGRESS_BAR.md) for progress bar sources and placement.

**Import Template** accepts exported JSON templates and Steam Achievement Notifier (SAN) presets: select the preset's `index.html` or `styles.css`. SAN's **Native OS** preset is not an overlay skin; use **Windows toast only** for native OS behavior.

There are no templates provided; you can create your own as you like.

I would advise using the builder HTML file, which can be opened from the **Open Builder** button next to the saved slots, for more customization such as the drag-edit available in it.

Use **Test Notification** after changing a style. It always uses the Custom style. A SAN transition or SAN element forces overlay delivery for the test. A successful test proves rendering and sound, but not that the game's achievement source is detected; detection is tested by launching and unlocking in the game.

## Sounds

Choose a sound in **Sound Notification**. The default bundled sound is `Resources\Sounds\Steam.wav` and cannot be removed. Use **Add File** or **Add Folder** to add more `.wav` files; additional sounds are under [`source/Resources/AdditionalSounds`](https://github.com/Santodan/PlayniteAchievements/tree/main/source/Resources/AdditionalSounds).

**Sound lead (ms)** controls the relative timing between audio and the visual notification. Positive values start the sound before the overlay; negative values show the overlay first and delay the sound. Extreme values can make the sound seem disconnected from the overlay.

If no sound plays:

- run **Test Notification**;
- verify the custom path still exists and is readable;
- switch temporarily to the bundled Steam sound;
- check the Windows volume mixer and the active output device;
- check **Decoded sound cache** under **Santodan-Fork > Performance** if you changed it.

## Score progress notifications

**Score Progress Notifications** (Notification Style page) can show a notification when your Collection or Prestige score reaches a new level or tier. For each score, enable **Notify on level up** and/or **Notify on tier up**, pick a **Saved style** slot, a sound and a sound lead, and use **Test**. A tier notification takes priority when both happen together.

## Memories: screenshots and video clips

The **Memories** page captures unlocks. Captures are tied to a real detected unlock, not to a preview.

### Screenshots

Enable **Save a screenshot when a new achievement is detected**, then choose one or more variants:

- **Clean:** the game without a notification;
- **With notification:** the game with the notification card composited in;
- **Framed:** a framed composition.

Each variant has its own **Capture rarities** (Common, Uncommon, Rare, Ultra rare) and **Always capture game completion**. **Filename suffixes** distinguish the variants. For SAN notifications, **SAN screenshot view** and **Capture before view ends (ms)** choose which view is captured.

If **Save folder** is blank, files go to `Pictures\PlayniteAchievements\UnlockScreenshots`. The folder and **Filename template** support the wildcards shown on the page (`<gameName>`, `<achievementName>`, `<date>`, `<time>`, `<dateTime>`, `<source>`, `<provider>`, `<gameId>`, `<unlockIndex>`, `<unlockCount>`). Screenshots are saved as PNG. Invalid filename characters are cleaned automatically.

### Video clips

Enable **Save a video clip when an achievement is detected**. Choose capture rarities and **Always capture game completion**, a save folder (blank uses the screenshot folder) and filename template, **Pre-roll length (seconds)** (5–60, default 15), **Frames per second** (10–60, default 30) and **Resolution** (Native, 1080p, 720p). Optionally include system audio (full system or game only) and the microphone. Recording uses Windows Graphics Capture and Media Foundation.

## Per-game controls

There is no separate per-game real-time toggle. To silence or change notifications for one game, use **Manage Achievements > Notifications** for that game. The monitor also skips games excluded from refreshes (**Playnite Achievements > Maintenance > Exclude from Refreshes**). If one game is silent while **Test Notification** works, check both.

## Refresh when a game closes

**Refresh achievements when a game closes** is on the Local page (**Santodan-Fork > Platforms > Local**). It performs a normal refresh after the process exits. You can override it per game in **Manage Achievements > Local Overrides > Local Saves & Schema > Refresh when game closes**.

## Troubleshooting decision path

### Test Notification does not appear

1. Confirm **Use custom Achievement Notification for live unlocks** is enabled; the notification controls are disabled otherwise.
2. If **Use upstream notification template** is checked, the test uses the upstream notification and its settings.
3. Check the delivery mode and its matching in-app/system switch.
4. Try a built-in style and the bundled Steam sound.
5. If only a custom or SAN template fails, re-import it and verify referenced asset paths still exist.
6. Check whether the overlay is on another monitor or hidden behind an exclusive-fullscreen game. Test in windowed or borderless mode, and try **Show overlay on the active game's monitor**.

### Test works, but real unlocks do not

1. Confirm a manual refresh of the game shows its achievements. For Local games, see [Local Provider Troubleshooting](LOCAL_PROVIDER_TROUBLESHOOTING.md).
2. Check **Manage Achievements > Notifications** for that game, the upstream notification switches, and refresh exclusion.
3. For Local games, confirm the achievement file changes while the game is running. Some games write only on exit.
4. Remember that the first read establishes a baseline and deliberately does not announce old unlocks.
5. If the game writes on exit, keep Playnite running until the game fully exits and enable **Refresh achievements when a game closes**.

### Notification appears without an icon or useful text

The unlock was detected, but its metadata was incomplete or did not match the save's API keys. Set the correct App ID, change the metadata source priority, or configure a custom schema as described in [Local Provider Troubleshooting](LOCAL_PROVIDER_TROUBLESHOOTING.md).

### Duplicate or unexpected notifications

- Do not let multiple scanned folders point at duplicate copies of the same achievement state.
- In Hybrid mode, two visible surfaces (overlay and toast) are expected.
- With **Use a single custom notification** off, a batch of unlocks is shown as stacked notifications.
- If an external notifier is also monitoring the same files, disable one of the two applications.

### Screenshots or clips are missing

- Captures follow a real detected unlock, not the preview.
- Confirm the capture is enabled, at least one screenshot variant is selected, and the unlock's rarity is selected for that variant.
- Confirm the target folder is writable, or clear it to use the default folder.
- Use windowed or borderless fullscreen if exclusive fullscreen capture fails.

## Logs for bug reports

Use **Santodan-Fork > General > Logs** (**Open logs**, **Copy Logs**) to collect `playniteachievements.log`. Relevant messages include `[InGameMonitor]` lines, `[AchievementNotification] Upstream poll detection routed to custom notification`, and `[LocalToast]` lines. For notification issues, also enable **Debug achievement notification logging** and include `AchNotifDebug.log`.

Include the game name, provider, delivery mode, selected style, polling interval, and whether **Test Notification** worked. Redact personal paths if necessary.
