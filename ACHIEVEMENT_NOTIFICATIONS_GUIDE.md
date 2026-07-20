# Achievement Notifications Guide

Achievement notifications monitor a running game's achievement source and notify you when the unlocked set changes. The visual settings are shared by supported real-time providers, while Local monitoring is the core path described here.

## Enable notifications

1. Enable the **Local** provider under **Providers > Local**.
2. Open **Add-ons > Extension settings > Playnite Achievements > Achievement Notifications**.
3. Enable **real-time Local achievement monitoring**.
4. Choose a polling interval. The supported range is 1–60 seconds; the default is 5 seconds.
5. Select a delivery mode and configure a sound/style.
6. Choose a preview game and achievement, the provider (if you have the provider icon or wildcard), then click **Test Notification** before launching a game.

The monitor runs only for the currently running game. On start it creates a baseline; achievements already unlocked in that first snapshot are not announced as new. It then checks the source file for changes and compares the new unlocked set with the baseline.

## Delivery modes

- **Windows toast only:** sends a system/Playnite notification without an overlay.
- **Overlay only:** shows the styled in-app overlay.
- **Hybrid (Overlay + Toast):** uses both.

The **Show in-app notification when achievement is unlocked** and **Send System notification** switches can further enable or disable the two outputs. If a selected delivery mode appears to do nothing, confirm that its matching switch is enabled.

Overlay behavior includes position, duration, fade/slide transition, opacity, and scale. Multiple unlocks can be stacked. Some SAN templates require overlay delivery; the test action forces overlay mode for those templates.

## Styles and templates


The Custom style editor supports saved slots, layout, up to six text lines, colors, font treatments, icon sources, rarity glow, game cover/banner artwork, animation, and imported templates.

SAN preset can be imported. Steam Achievement Notifier's Native OS preset is not an overlay skin; use Windows-toast delivery for native OS behavior.

Use **Test Notification** after changing a style. A successful preview proves rendering and sound, but not that the game has a valid Local source; detection is tested by launching and unlocking in the game.

There are no templates provided, you can create your own as you like.

I would advice to use the builder html file, can be open from the `Open Builder` button next to the saved slots, for more customization like the drag-edit available in it.

## Sounds

Choose a bundled sound or a custom sound file. The default bundled sound is `Resources\Sounds\Steam.wav`; additional sounds are under [`source/Resources/AdditionalSounds`](https://github.com/Santodan/PlayniteAchievements/tree/main/source/Resources/AdditionalSounds).

If no sound plays:

- run **Test Notification**;
- verify the custom path still exists and is readable;
- switch temporarily to a bundled WAV file;
- check the Windows volume mixer and the active output device;
- confirm notifications are enabled for this game.

Sound lead timing controls the relative timing between audio and the visual notification. Extreme custom timing can make the sound seem disconnected from the overlay.

## Screenshots

Unlock screenshots can capture the full desktop or the active window, after a configurable delay from 0–10,000 ms, as PNG or JPEG.

If the save folder is blank, files go to `Pictures\PlayniteAchievements\UnlockScreenshots`. The filename and folder supports wildcards that are present in the extension.

Invalid filename characters are cleaned automatically. A short delay may capture the game before its own animation settles; a longer delay may capture a different active window. Use **Full desktop** when exclusive/fullscreen window capture is unreliable.

## Per-game controls

Right-click one or more games and use the Playnite Achievements menu to disable or enable real-time notifications. This per-game switch overrides the global monitoring setting for those games.

The monitor also skips games excluded from achievement refreshes. If one game is silent while Test Notification works, check both its notification toggle and refresh exclusion.

## Optional refresh behavior

- **Refresh achievements when a game closes** performs a normal refresh after the process exits.
- **Refresh achievements on real-time unlock** queues an extension refresh after displaying a detected unlock.

The live monitor already writes the current Local data to the cache. The extra refresh option is useful when other extension views or integrations need a full refresh immediately, but it may perform more work.

## Troubleshooting decision path

### Test Notification does not appear

1. Confirm real-time monitoring is enabled; the notification controls are disabled otherwise.
2. Check delivery mode and its matching in-app/system switch.
3. Try a built-in style and bundled sound.
4. If only a custom/SAN template fails, re-import it and verify referenced asset paths still exist.
5. Check whether an overlay is positioned on another monitor or hidden behind an exclusive-fullscreen game. Test in windowed/borderless mode.

### Test works, but real unlocks do not

1. Confirm the game resolves through the Local provider and a manual refresh shows its achievements.
2. Check the per-game notification toggle and refresh exclusion.
3. Confirm the Local achievement file changes while the game is running. Some games write only on exit.
4. Ensure the correct save folder and Steam user are selected.
5. Remember that the initial snapshot establishes a baseline and deliberately does not announce old unlocks.
6. Increase the chance of catching delayed writes by leaving Playnite running until the game fully exits; the monitor performs a final post-stop recheck.

### Notification appears without an icon or useful text

The unlock was detected, but its metadata schema was incomplete or did not correlate with the local API key. Set the correct App ID, select a better metadata priority, or configure a custom schema as described in [Local Provider Troubleshooting](LOCAL_PROVIDER_TROUBLESHOOTING.md).

### Duplicate or unexpected notifications

- Do not run overlapping save folders that point to duplicated copies of the same achievement state.
- Check whether both overlay and system toast are enabled in Hybrid mode; two visible surfaces are expected there.
- A batch of multiple achievements may be displayed as stacked notifications.
- If an external achievement notifier is also monitoring the same files, disable one of the two applications.

### Screenshots are missing

- Screenshot capture is tied to a real detected unlock, not merely to the visual preview.
- Confirm screenshot capture is enabled and the target folder is writable.
- Try the default folder and PNG format.
- For active-window failures, use full-desktop capture and add a modest delay.

## Logs for bug reports

Relevant log messages include `Started active Local achievement monitor`, `Initialized ... baseline`, `Detected ... newly unlocked`, `Skipped ... notification`, and `Active Local achievement refresh failed`. Include the game name, delivery mode, selected style, polling interval, and whether **Test Notification** worked. Redact personal paths if necessary.
