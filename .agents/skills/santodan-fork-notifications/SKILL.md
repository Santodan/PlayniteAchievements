---
name: santodan-fork-notifications
description: Map of the Santodan-Fork custom Achievement Notification, Memories screenshot/clip capture, score progress notifications, and their settings UI. Use before changing or debugging notification detection, routing, rendering, delivery, sounds, templates, previews, or capture timing, to jump straight to the right files instead of searching.
---

# Fork notifications and Memories map

Read `santodan-fork-context` rules first (Santodan-Fork tab only, protected files,
no commit/push unless asked). Paths are relative to the repository root. Files
are large: search by the symbol names below instead of reading whole files.

## Pipeline (live unlock)

1. **Detection** is the shared upstream monitor `source/Services/InGameAchievementMonitor.cs`.
   It polls the running game for every provider, not only Local. Fork hooks:
   - `ShouldPollGame`: runs when upstream in-game polling is on **or**
     `LocalSettings.EnableActiveGameMonitoring` is on; skips refresh-excluded games.
   - `GetPollInterval`: uses `LocalSettings.ActiveGameMonitoringIntervalSeconds`
     (1-60, default 5) while the fork notification is on.
   - `GetMissingAchievementFilePollInterval`: Local missing-file rescan (1-300 s).
   - Custom Local polling: `PrepareSantodanLocalPolling`, `CompleteSantodanLocalPolling`,
     `source/Services/InGameMonitoring/Santodan*.cs`.
2. **Routing**: `source/PlayniteAchievementsPlugin.cs`, `UsesCustomAchievementNotification`
   and `HandlePolledAchievementUnlocked`. The fork renderer is used when
   `EnableActiveGameMonitoring` is on, `UseUpstreamNotificationTemplate` is off, and
   the game is not in `DisabledRealtimeNotificationGameIds`. Nothing writes that list
   from the UI any more. The event still goes to `NotifyAchievementUnlocked`, so
   screenshots, clips, friends and completion stay on the upstream pipeline;
   `SuppressStandardToast` hides only the upstream toast.
3. **Rendering and delivery**: `source/Services/NotificationPublisher.cs`,
   `ShowLocalAchievementUnlocked`. It first applies `ProviderNotificationPolicy.Resolve`
   (upstream switches, provider overrides, per-game Manage Achievements > Notifications).
   Delivery: `UnlockNotificationDeliveryMode` (Toast/Overlay/Hybrid, default Hybrid)
   ANDed with `EnableInAppUnlockNotifications` / `EnableWindowsToastNotifications`.
   Fork partials: `NotificationPublisher.Santodan{BrowserStartup,Effects,Scores}.cs`.
   Score level/tier notifications: `NotificationPublisher.SantodanScores.cs`,
   `source/Services/Achievements/AchievementDataService.SantodanNotifications.cs`.
4. **Memories capture** (screenshots/clips), active only when enabled on the fork page:
   - Screenshots: `source/Services/UI/ToastNotificationService.cs`,
     `ResolveEffectiveScreenshotSettings` maps `LocalSettings.Screenshot*` onto a cloned
     `PersistedSettings` when `LocalSettings.EnableUnlockScreenshots` is on. Variants
     are resolved by `UnlockScreenshotVariantPolicy`.
   - Delay: `source/Services/UI/SantodanMemoriesCapturePolicy.cs`. Precedence is the
     game's capture override, then the Memories delay (dual capture forced off), then
     upstream globals. Tests: `SantodanMemoriesCapturePolicyTests`.
   - Clips: `source/Services/Recording/UnlockRecordingService.cs`,
     `ResolveEffectiveRecordingSettings` (when `EnableUnlockRecordings` is on). Clips
     keep their own delay; don't make a screenshot delay move a clip anchor.
   - Path/filename wildcards: static `source/Services/Local/LocalAchievementScreenshotService.cs`
     (`ReplaceTokens`, `SanitizeFileName`, `BuildUniquePath`). Saving:
     `UnlockScreenshotService.SaveUsingCustomSettings`. Output is PNG only.

Removed on 2026-11-22; do not restore: `ActiveGameAchievementMonitor`,
`ExophaseGameAchievementMonitor`, `RetroAchievementsGameAchievementMonitor`, the
full-desktop/active-window/JPEG capture, and `ScreenshotCaptureMode`/`ScreenshotImageFormat`.

## Settings model and UI

- Model: `source/Providers/Local/LocalSettings.cs`. Notification settings live on the
  Local provider settings even though the UI is the fork notification tab. Defaults
  are field initializers (`private ... _name = value;`). Clamp constants are `Min*`/`Max*`.
- UI: `source/Views/LegacyNotificationSettingsControl.xaml` (5k lines) and `.xaml.cs`
  (9k lines). `SettingsControl.xaml.cs` moves its `AchievementNotificationsTab` content
  into Santodan-Fork > Achievement Notification via `DetachAchievementNotificationsContent`.
- Page sections (grep the literal text): `Text="Real-time Achievement Notifications"`
  (General: template, live detection, delivery, debug log), `Text="Notification Style"`
  (styles, previews, slots, custom editor), `Text="Sound Notification"`,
  `Text="Score Progress Notifications"`, `Text="Memories"`.
- Handlers: `NotificationsTestUnlockSoundButton_Click` (Test Notification; forces Custom
  style and forces overlay for SAN transitions/elements), `NotificationsPreviewSelectedStyle_Click`,
  `NotificationsImportCustomStyleTemplate_Click`, `NotificationsOpenTemplateBuilder_Click`.
- Style lists are built in code: grep `_notificationStyleOptions.Add` and
  `_notificationProviderOptions.Add`.
- Most fork notification labels are hard-coded English in XAML, not localization keys.

## Related files

- Builder and templates: `tools/notification-template-builder.html`, `tools/NotificationTemplates/`
  (`PROGRESS_BAR.md`), `tools/SAN-source/`, `source/Resources/AdditionalSounds/`.
- Debug log: `source/Services/Logging/AchievementNotificationDebugLog.cs` (`AchNotifDebug.log`).
- User doc to keep in sync: `ACHIEVEMENT_NOTIFICATIONS_GUIDE.md`.
- Checklist section: `## Achievement notifications` in `tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md`.

## Pitfalls

- A Test Notification success proves rendering and sound only, not detection.
- Fork settings must only take effect while the fork feature is enabled; with it off,
  upstream behavior must be unchanged.
- Notifications also obey upstream notification switches and per-game Manage
  Achievements > Notifications settings.
