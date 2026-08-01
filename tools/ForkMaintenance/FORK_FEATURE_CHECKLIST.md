# Santodan fork feature checklist

Use this checklist after applying the bundle to a new upstream release. The
bundle captures the source automatically; this list records the behavior that
must still be verified in Playnite.

## Achievement notifications

- The separate Achievement Notification settings page is populated with its
  fork defaults.
- Global and per-provider notification styles work, including Local and
  Exophase.
- Custom templates, custom sounds, screenshots, recordings, and test
  notifications still work.
- Upstream v3 live polling is the single unlock detector for the custom
  Achievement Notification; own-player events use the fork renderer without
  also displaying the upstream toast.
- The Achievement Notification polling interval drives upstream live polling
  whenever the custom notification is enabled, including values from 1 to 60
  seconds.
- Friend unlocks, game-completion events, screenshots, and recordings remain
  on the upstream event pipeline, and per-game real-time notification
  exclusions are respected.
- When the custom notification is enabled, the upstream screenshot and
  recording services use the custom tab's capture settings. Screenshot and
  video destinations and filenames retain the fork wildcard support, and
  enabling both upstream and custom controls does not duplicate captures.
- Collection and Prestige score snapshots are compared after library-state
  refreshes. Configured level-up/tier-up notifications fire once, tier takes
  priority when both change, and initial/zero snapshots initialize silently.
- The obsolete separate Exophase/RetroAchievements API-verification controls
  are not shown.
- Enabling **Debug achievement notification logging** creates
  `AchNotifDebug.log` in the extension data directory. If the file already
  exists, **Yes** recreates it and **No** appends to it. Reopening Playnite with
  Debug already enabled appends without prompting, disabling Debug stops the
  dedicated logger, and detailed notification diagnostics are not duplicated
  into `playniteachievements.log`.

Primary fork areas:

- `source/Services/NotificationPublisher.cs`
- `source/Services/Logging/AchievementNotificationDebugLog.cs`
- `source/Services/InGameAchievementPoller.cs`
- `source/Services/UI/ToastNotificationService.cs`
- `source/Services/UI/UnlockScreenshotService.cs`
- `source/Services/Recording/UnlockRecordingService.cs`
- `source/Services/ThemeIntegration/ThemeIntegrationService.cs`
- `source/PlayniteAchievementsPlugin.cs`
- `source/Services/Local/`
- `source/Services/Exophase/`
- `source/Views/LegacyNotificationSettingsControl.*`
- `source/Resources/AdditionalSounds/`
- `tools/notification-template-builder.html`

## Local provider and game overrides

- Local appears with its default icon and settings defaults.
- Local folder discovery, extra/excluded paths, import, and refresh work.
- Steam App ID and Steam-user overrides remain in the Steam sub-tab.
- LumaPlay App ID and `LumaPlay.ini` remain in the LumaPlay sub-tab.
- Custom schema loading, editing, creation, and per-game enable/disable work.
- Manage Achievements contains Overrides → Main/Local and Local →
  Local Saves & Schema/Steam/LumaPlay.
- Local right-click commands and Manage Achievements shortcuts are present.
- View Achievements shows `Edit Local Achievements` only when the game resolves
  through the Local provider and has a writable `achievements.json` or
  `achievements.ini`.
- The Local achievement editor opens with the current v3 achievement cell
  resources and can save unlock state and unlock times back to the Local file.

Primary fork areas:

- `source/Providers/Local/`
- `source/ViewModels/LocalAchievementEditorViewModel.cs`
- `source/Views/LocalAchievementEditorControl.*`
- `source/ViewModels/ViewAchievementsViewModel.cs`
- `source/Views/ViewAchievementsControl.*`
- `source/Services/UI/PluginWindowService.cs`
- `source/PlayniteAchievementsPlugin.LocalMenus.cs`
- `source/ViewModels/*Local*`
- `source/ViewModels/ManageAchievementsViewModel.*.cs`
- `source/Views/ManageAchievements/`
- `source/Views/GameOptionsLocalOverridesSection.*`

## Overview and manual grid configuration

- All Achievements loads achievements from every cached game.
- Recent Achievements, All Achievements, and Selected Game expose their
  intended independent columns.
- Custom (Manual) columns and multi-column sorting take priority over Display
  → Overview defaults.
- Selecting a game or refreshing graphs does not reset manual sorting.
- Filters survive leaving and reopening the extension page.
- Column headers keep the fork's three-state click behavior.

Primary fork areas:

- `source/Views/OverviewControl.*`
- `source/Views/ManualAchievementSortDialog.*`
- `source/Services/Overview/`
- `source/Services/Achievements/AchievementSortHelper.cs`
- `source/Models/Settings/PersistedSettings.ForkCompatibility.cs`

## Theme and StartPage migration

- The fork Theme Migration page remains separate from upstream's migration
  pages.
- The newest unlocked achievement is highlighted above the compact/scrollable
  unlocked-achievement row.
- Both legacy `PluginCompactUnlocked` and modern
  `AchievementCompactUnlockedList` migration paths retain the highlighted row.
- Automatic migration handles first-time themes and upgraded themes.
- The non-scrollable legacy option, scrollable option, revert, and StartPage
  compatibility apply/revert actions work.
- Fullscreen library summaries normalize legacy `Local` rows whose platform was
  stored as `Unknown`; their displayed provider/platform remains `Local`.
- Solaris Limited migration adds `Local` to both its hardcoded Dynamic provider
  filter and Preset list, backed by the `LocalGames` compatibility collection.
- Re-running Solaris migration updates an already-migrated theme without
  duplicating its `Local` button, preset, list, or visibility triggers.
- The StartPage add-on's Recent Achievements widget sorts achievements globally
  by unlock time while still respecting both its total maximum and its
  maximum-per-game setting.

Primary fork areas:

- `source/Services/ThemeMigration/`
- `source/Views/ThemeIntegration/Legacy/PluginCompactUnlockedControl.*`
- `source/Views/ThemeIntegration/Modern/AchievementCompactUnlockedListControl.*`
- `source/Views/SettingsControl.*`

## Steam and imports

- Steam browser authentication and Web API-key accounts both work.
- Per-game Steam-account overrides use the correct account.
- Owned-games and family-sharing import retain their fork behavior.
- Imported-game metadata source selection works for Steam and Local imports.

Primary fork areas:

- `source/Providers/Steam/`
- `source/Providers/ImportedGameMetadata/`
- `source/ViewModels/GameOptionsOverrideTab.cs`

## Release and manifest policy

- Upstream and fork release monitoring still point at their intended
  repositories.
- `source/extension.yaml` and `InstallerManifest.yaml` are edited manually and
  are never exported or applied by the fork bundle.

## Minimum validation

1. Run `Apply-ForkBundle.ps1 -DryRun` against a clean upstream worktree.
2. Apply the bundle.
3. Rebuild `source/PlayniteAchievements.csproj` in Release.
4. Install the newly timestamped `.pext` and restart Playnite.
5. Exercise each behavioral section above before replacing the previous
   working fork.
