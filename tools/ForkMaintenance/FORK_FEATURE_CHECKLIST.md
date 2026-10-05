# Santodan fork feature checklist

Use this checklist after applying the bundle to a new upstream release. The
bundle captures the source automatically; this list records the behavior that
must still be verified in Playnite.

## v4.0.1 recovery update — 2026-10-05

These items describe the current integration, not a promise that every older
fork feature below has been restored. See `UPSTREAM_V4.0.1_CONFLICT_REVIEW.md`
at the repository root for remaining missing features and live-verification tasks.

- Manage Achievements preserves the upstream shell and provides Local in
  Platform Override, a dedicated Local Overrides page, and a separate Custom
  Schema tab. Local Overrides uses a left-side platform-icon sidebar with
  Local Saves & Schema, Steam, Epic, LumaPlay, and RetroAchievements pages.
  Local icons use the configured provider color and custom/borrowed icon.
- Overview Custom Data shows override values, including RetroAchievements
  base/subset names and IDs. The top-level Clear clears cache, custom data,
  platform routing, and local overrides; upstream's Clear Custom Data label stays.
- Custom Schema loads JSON or accepts manual schema editing and correlates with
  the Local achievement file before updating the unified Editor. Legacy manual
  navigation and portable-package entry points still require review.
- View Achievements retains the upstream editor action and adds Edit Local File
  plus the title-bar refresh button. The separate file editor accepts 24-hour
  times, writes intentional relocks to disk/cache, and has Open File Location.
  Verify save/restart persistence in Playnite.
- Local settings have General, Import, and SuccessStory tabs; General shows
  custom folders on the left and excluded folders on the right.
- Steam settings have Original (upstream browser controls) and Secondary
  (API-key account management/imports). Profile recognition uses an API key
  plus Steam ID/profile URL; secondary accounts do not require another login.
  Selected-account credentials and per-game account overrides drive refreshes.
  Override-only custom records persist; Apply/Clear start a single-game refresh.
  Verify real-account switching and imports. Family-sharing discovery remains
  Original-browser-session-only, not secondary-account parity.
- Preserve cached unlocks on refresh is enabled by default under General >
  Maintenance. The per-game three-state control in Local Saves & Schema applies
  to every provider: keep cached unlocks, accept refreshed states, or use global.
  Apply refreshes immediately. Disabled protection allows valid lower/zero
  unlock counts; failed/empty payloads remain protected. Explicit editor saves
  still permit intentional relocks regardless of normal-refresh protection.
- The upstream Overview layout includes the fork's All Achievements tab,
  Custom (Manual) selector, and working Configure/manual ordering actions.
- Desktop scrollable-plus-highlight migration, compact migration, and Fullscreen
  migration were user-confirmed working. Score-snapshot persistence still needs review.
- Live custom notifications and Memories captures use the fork template and
  settings. WPF and WebView/SAN frames, top/bottom-center placement, custom
  transitions, shared sanitized folder names, and SAN Screenshot View 1/2 were
  restored; captures were user-confirmed working. Active-card glow, effective
  quality selection, and recorder reconfiguration still need comparison.
- Locale-dependent score and Guild Wars 2 separator differences are accepted
  by the user; they are not outstanding fork-restoration tasks. Historical test
  expectations may still need alignment, without forcing a display change.
- Latest verification: required Release rebuild passed; 19 Steam-focused tests
  and 93 cache/custom-data-focused tests passed. The historical full suite has
  not been rerun since these fixes; do not treat its old failures as current.
- Preserve AddonDBManifest.yaml, source/extension.yaml, InstallerManifest.yaml,
  and README.md during bundle application. No commit/push without user approval.

## Achievement notifications

- The separate Achievement Notification settings page is populated with its
  fork defaults.
- Opening extension settings does not construct the large legacy Achievement
  Notifications editor until its tab is selected; opening the compatibility
  Theme Migration tab may also construct it because that page shares the same
  host control.
- Global and per-provider notification styles work, including Local and
  Exophase.
- Custom templates, custom sounds, screenshots, recordings, and test
  notifications still work.
- The Achievement wildcard group includes `<gameUnlockedCount>` and
  `<gameAchievementCount>`. Custom notification text resolves them to the
  current game's unlocked and total achievement counts in live notifications,
  previews, WebView/SAN overlays, and captures.
- Custom notification primary and secondary icon backgrounds can each be
  enabled or disabled and assigned an independent color. The settings survive
  style-slot save/load and JSON template export/import, and the HTML builder
  previews and serializes the same values.
- Manually resized and positioned notification elements retain matching
  geometry between the HTML builder and the extension after template import.
  The no-achievement placeholder uses the configured primary-icon footprint,
  and game covers preserve their full aspect ratio inside the cover element
  instead of being cropped when its width or height is changed. The cover
  background can be independently enabled or disabled and assigned a color;
  both settings survive style-slot save/load and JSON template export/import.
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
  into `playniteachievements.log`. The log includes a structured snapshot of
  the effective Achievement Notification settings on enable/startup and writes
  another snapshot only when those settings change; provider credentials and
  unrelated Local-provider settings are not included.

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
- Local settings show **Use SteamHunters for Categories** enabled by default
  immediately above **Steam path (optional)**. When enabled, Local refreshes
  use the resolved Steam schema App ID to populate SteamHunters category labels,
  Base/DLC types, and default category artwork through Steam's shared enricher.
- After category enrichment, Manage Achievements shows only categories that
  contain achievements; it does not retain a synthetic `Default` category with
  `0/0` achievements. A real Default category remains visible when at least one
  achievement belongs to it.
- Steam App ID and Steam-user overrides remain in the Steam sub-tab.
- Epic remains between Steam and LumaPlay. Its sub-tab accepts Nemirtingas
  `achievements_db.json` and `achievements.json` overrides plus an optional
  32-character Epic artifact/namespace ID.
- Automatically resolved Epic schema, save, and artifact values populate the
  three text fields and use a `Detected:` status; the fields are not left blank
  merely because their values came from discovery instead of an override.
- Nemirtingas Epic progress is correlated by `AchievementId`. Local schema
  names and descriptions are preserved while missing icons are enriched from
  Epic's public achievement schema. The artifact ID is detected from the save
  directory and resolved to an Epic namespace when necessary.
- When `achievements_db.json` is absent, a selected/detected save and Epic
  identity load the complete official schema; if the public lookup is offline
  or unavailable, local progress still works whenever the local schema exists.
- LumaPlay App ID and `LumaPlay.ini` remain in the LumaPlay sub-tab.
- A RetroAchievements Game ID override can load the base achievement set and
  available subsets, save any combination through multi-selection, and limit
  refresh results to those selected sets. Leaving every set unchecked includes
  all available sets, including when global subset scanning is disabled. After
  restarting Playnite, the saved selection remains active and stale achievements
  from previously included sets do not return from the SQLite cache.
- Custom schema loading, editing, creation, and per-game enable/disable work.
- Manage Achievements contains a dedicated Local Overrides page with a left
  sidebar: Local Saves & Schema/Steam/Epic/LumaPlay/RetroAchievements.
- In Manage Achievements, selecting Automatic or Clear under Change Provider
  removes both the preferred-provider selection and any higher-priority forced
  provider override.
- Manual tracking can fetch a selected Exophase achievement set even when the
  game previously had a forced or preferred provider route; the wizard proceeds
  to editing instead of returning to the Exophase search results.
- Local right-click commands and Manage Achievements shortcuts are present.
- View Achievements shows `Edit Local Achievements` only when the game resolves
  through the Local provider and has a writable `achievements.json` or
  `achievements.ini`.
- The Local achievement editor opens with the current v3 achievement cell
  resources and can save unlock state and unlock times back to the Local file.
- Opening Playnite's manual-game editor does not refresh the temporary
  library-less `New Game` record. Auto-refresh starts only after the record has
  a real name, and an in-progress RetroAchievements platformless lookup stops
  when the game is explicitly routed to Local or another provider.

Primary fork areas:

- `source/Providers/Local/`
- `source/Providers/Steam/SteamDataProvider.cs`
- `source/Providers/RetroAchievements/`
- `source/Services/Refresh/NewGameAutoRefreshPolicy.cs`
- `source/Services/GameCustomData/`
- `source/ViewModels/LocalAchievementEditorViewModel.cs`
- `source/Views/LocalAchievementEditorControl.*`
- `source/ViewModels/ViewAchievementsViewModel.cs`
- `source/Views/ViewAchievementsControl.*`
- `source/Services/UI/PluginWindowService.cs`
- `source/PlayniteAchievementsPlugin.LocalMenus.cs`
- `source/ViewModels/*Local*`
- `source/ViewModels/ManageAchievementsViewModel.*.cs`
- `source/ViewModels/ManageAchievements/ManageAchievementsCategoryViewModel.cs`
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
- Automatic migration handles first-time themes and upgraded themes when its
  Theme Migration toggle is enabled, and skips both when disabled. The toggle
  remains persisted and defaults to enabled.
- The non-scrollable legacy option, scrollable option, revert, and StartPage
  compatibility apply/revert actions work.
- Fullscreen library summaries normalize legacy `Local` rows whose platform was
  stored as `Unknown`; their displayed provider/platform remains `Local`.
- Solaris Limited migration adds `Local` to both its hardcoded Dynamic provider
  filter and Preset list, backed by the `LocalGames` compatibility collection.
- Re-running Solaris migration updates an already-migrated theme without
  duplicating its `Local` button, preset, list, or visibility triggers.
- Solaris migration preserves distinct theme-owned `SuccessStory` and
  `PlayniteAchievements` panel/toggle names and their references, while migrating
  plugin bindings and custom-control prefixes. Restore the migration backup
  before migrating themes whose names were merged by the old migration.
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
- With a non-English achievement language selected, refreshing a Steam game
  obtains localized hidden-achievement titles and descriptions from Steam's
  full schema. This also works without a Steam account in the extension when
  SteamHunters supplies the base schema: the official anonymous Steam schema
  overlays its localized hidden text, with the Community-page bridge retained
  as fallback. Revealing a locked hidden achievement uses the extension
  language rather than English, the Windows locale, or the Playnite UI
  language, while already-localized visible achievement text remains intact.
- Per-game Steam-account overrides use the correct account.
- Owned-game imports retain metadata-source and overwrite/skip controls.
  Family-sharing discovery requires the Original browser account; API-key
  imports read the selected secondary account's owned games only.
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
