# Santodan fork feature checklist

## Experimental Performance tab - 2026-11-07

- Verify the Experimental badge and as-is notice. Scroll the options: the notice
  and Restore defaults button must stay visible. Check narrow settings windows.
- Change all Performance options, restore defaults, and Save/reopen: verify the
  original defaults. Repeat with Cancel: saved choices must survive. Unrelated
  notification styles, provider overrides and General Debug mode must be preserved.
- Keep all 24 bindings covered by `ForkPerformanceDefaults`; new options need an
  explicit reset assignment. No immediate disk write from the reset button.
- Compare browser startup choices after restarting. Verify authenticated Steam
  requests/direct token resolution and first WPF/SAN notification/preview use.
- With overview on-demand enabled, background warm-up must skip both scheduling
  and delayed completion; opening a consumer still builds fresh data. Compare
  Steam-user menu preload off/on, allowing asynchronous discovery to complete.
- With capture watching off, extension-created captures still update and viewer
  reopening rescans externally changed files. Restart between watcher comparisons.
- With gameplay scheduler deferral enabled, test multiple running games, last-game
  exit, disabling the option while waiting and shutdown cancellation. Current-user
  and friend jobs reassess staleness; live polling and manual refresh stay active.
- Test static WPF GIF/WebP artwork, including grayscale and still-image fallback.
  HTML templates can animate their own images independently. Compare 8/32/128 MB
  animation cache budgets after restart; do not interpret this as a process RAM cap.
- Manual previews must not auto-rebuild or loop, including previously queued timer
  callbacks; explicit Preview/Test buttons must work. Leaving the page always stops
  the inline loop timer. With release enabled, hidden edits must not recreate it.
- Compare reduced effects off/on in WPF and generated SAN notifications, including
  custom templates, notification timing, positioning, screenshots and recordings.
  Saved template/effect choices must remain intact.
- Compare sound cache 0/10/60 seconds after restart, including first/repeated sound,
  preload, playback duration and helper relaunch. At zero, do not retain decoded
  clips or decode them for preload; playback must still work. Package the new helper.
- Follow the expanded Local troubleshooting guide. Preserve optional defaults and
  avoid adding controls that disable required migrations or alter upstream tabs.
- Automated validation: the targeted test runs and Release builds in the matching
  maintenance README entry passed. New runtime UI/RAM comparisons remain pending.

## Account-free SteamHunters definitions for Local - 2026-11-05

- Without a Steam or SteamHunters login, refresh a Local game linked to Steam
  app ID 71340 (Sonic Generations). Public definitions and metadata must load
  without `ManualSourceAuthenticationException` from the SteamHunters path.
- Select **Prefer SteamHunters first (no login required)**: try SteamHunters
  before any Steam token resolution. A previously cached authenticated Steam
  schema must not override this selection.
- Select **Prefer Steam sources first, then SteamHunters**: keep Steam first;
  when authentication or Steam is unavailable, use SteamHunters without login.
- Preserve API names, hidden flags, icons, percentages and points. Read progress
  and unlock times from the Local source, never a public user's profile.
- SteamHunters schema matching must use public definitions, browser transport,
  pacing and retries; do not restore authenticated Steam manual-schema calls.
- With a non-English plugin language, do not label English public metadata as
  localized. Retain Local's optional anonymous localization enrichment.
- Preserve the schema parser/endpoint tests and existing category tests.
  Implementation validation: 42 SteamHunters tests and Release rebuild passed;
  live Playnite verification remains pending.

## Debug mode and optional polling performance controls - 2026-11-04

- Verify Debug mode is in Santodan-Fork > General, and all performance controls
  are in Santodan-Fork > Performance. New switches default off and are disabled
  until custom Local polling is enabled. Existing defaults are documented in
  `docs/ForkPerformanceOptions.md`.
- Save/cancel/reopen settings; verify all option values survive the intended flow.
- Enable Local-only authentication checks: custom Local polls must not open a
  Steam authentication browser, even without linked accounts. With Debug mode,
  expect `authScope=local-only`. Manual/remote/upstream authentication must work.
- Enable waiting-source fingerprints: inspect `waitingForAchievementFile`,
  `fingerprintChecked`, `fingerprintAvailable`, and `refresh`. A known unchanged
  source skips refresh; unavailable fingerprints still refresh. Add/remove/change
  a source file and change a source override: the next configured poll must
  detect changes. No artificial interval increase is permitted.
- Enable per-poll folder reuse: identical searches are reused across refresh and
  monitor configuration. Missing results must expire before the next poll;
  separate games, settings, providers, nested scopes and concurrent tasks must
  not share incompatible results. Achievement contents remain fresh.
- Compare each switch independently with the game relaunched between recordings.
  Record an idle section first, then unlock achievements at a noted timestamp.
  Verify notifications are neither missed nor duplicated and retain their timing.
- Test startup receipts, lightweight discovery, session directory invalidation,
  metadata reuse, overview deferral, notification-score invalidation and fork-only
  theme caching using the detailed performance document.
- With custom polling disabled, preserve upstream behavior. With Debug mode off,
  do not run the optional CPU sampler; debug CEF tracing must remain passive.
- Validation: 111 targeted tests and Release rebuild passed. Developer runtime
  test showed lightweight idle checks and refresh work after unlocks at 70 seconds.
  Affected-user verification remains pending; retain the startup-peak limitation.

## Upstream template and General sections - 2026-10-25

- In Santodan-Fork > Achievement Notifications > General, verify separate template,
  live detection, and delivery cards, shorter help, and collapsed Troubleshooting.
- Upstream template defaults off and survives save/apply/reopen.
- With it off, verify fork notifications and fork capture overlays.
- With it on, verify upstream live notifications, screenshots, video overlays, and
  Test Notification. Upstream appearance/capture settings should apply.
- Fork-only delivery controls must dim/disable with upstream selected and recover
  when deselected. Keep the single-notification option accessible.
- Expand Troubleshooting and verify debug logging remains available.
- The grouped General layout supersedes the 2026-10-23 flat-layout requirement.
- Validation: Release rebuild and three targeted settings tests passed; clean
  preflight and replay onto upstream v4.1.1 passed (173 overlays, one localization
  recipe, hashes, protected files). All five changed source/test files match
  after newline normalization. Live Playnite verification remains pending.


## Single custom notification and General layout - 2026-10-23

- Find **Use a single custom notification** only under Santodan-Fork > Achievement
  Notifications > General. Keep standard notification tabs unchanged.
- Disabled (default): multiple unlocks retain separate custom overlays.
- Enabled: trigger successive unlocks while an overlay is visible; WPF and SAN
  must show the latest achievement in one overlay and restart its display time.
- Check closing overlays, switching renderers, enabling while several overlays
  exist, and persistent settings previews. Keep preview lifecycle separate.
- Save/apply/reopen settings and verify the selection survives.
- General's delivery mode and notification switches must align with preceding
  settings, without an inset card background or additional card padding.
- Release rebuild and both settings round-trip tests passed; live Playnite
  behavior and visual verification remain pending.
- Bundle preflight and actual replay against clean upstream v4.1.1 passed with
  173 overlays, one localization recipe, hashes, and protected-file checks.
  All six relevant notification/settings files match after newline normalization.

## Conditional text and settings visibility - 2026-10-23

- `<achievementName>` must contain only the name. Place progress explicitly.
  `<achievementName>[[ (<progress> -)]]` must hide the entire suffix when no
  counter exists; zero current progress remains a populated value.
- Use the editor below the wildcard buttons and above row 1. Apply conditions
  to a selected substring and a whole chosen row; check optional otherwise text.
- Verify has-value, empty, case-insensitive equals/not-equals/contains, and numeric
  gt/gte/lt/lte comparisons, including percentage values and nonnumeric input.
- Combine progress and other tokens using All/AND and Any/OR; AND binds first.
  Check WPF, SAN, and browser previews. Nesting is deliberately unsupported.
- Missing progress must not substitute default text into an intentionally empty
  conditional row. Preserve per-notification isolation and saved templates.
- Check preview actions, long wildcard labels, sound buttons, and hotkey buttons
  at larger fonts and narrow widths. Labels grow/wrap rather than clip.
- Check collapsed and expanded settings groups have visible borders, background,
  and padding, especially the condition editor.
- Open Achievement Notification and Theme Migration after restarting Playnite;
  neither may throw the duplicate Button resource exception. Preserve the
  resource-key regression test; do not add a second implicit style in one scope.
- Validation: Release rebuild, 15 notification tests, one resource regression test,
  five browser checks, and 12 WPF button layout checks passed. Full legacy resources
  load/seal; compiled expander styles load with borders in both states. Final
  live Playnite verification remains pending.
- Bundle refresh: clean upstream v4.1.1 preflight and actual replay passed with
  173 overlays, localization, component hashes, and protected-file checks.
  All 11 changed source/test/builder files match replay after newline normalization.

## PS5 Experience rank badge compatibility - 2026-10-22

- Already migrated PS5 Experience themes must be detected when rank aliases are
  missing. Apply Limited migration again, then restart; no theme reinstall needed.
- Verify every Bronze/Silver/Gold/Plat/Master rank (1-5) resolves to exactly one
  visible badge in each theme section. In particular, level 54 / Silver5 must
  display a silver badge beside its level number in the trophy page and menu.
- Preserve existing 1/2/3 triggers and native artwork; 4/5 use tier 3, Master uses
  Plat (or Plat1 if no Plat artwork exists). Do not alter scores or actual ranks.
- Repeat migration without duplicate triggers. Restore the original file through
  backup/revert. Unrelated plugins' rank bindings remain untouched.
- Validation: Release rebuild and 16 migration tests passed. Installed-theme
  in-memory check covered 25 ranks in five sections, valid XML, and idempotence.
  Reporter-side live verification remains pending.

## Progress row layout and editor defaults - 2026-10-22

- Group three bars in one row and another bar in a second row. Editing the first
  row's margins moves all three bars; the second row keeps its own settings.
- Set **Space between bars** to 0 and a positive value. Check WPF, SAN, and the
  builder; hidden bars leave no extra gap, and automatic widths share the rest.
- At zero top margin, bars attach to occupied content rather than blank configured
  height. Check icons, covers, wrapped text, and manually positioned elements.
  Negative margins intentionally overlap; SAN host height limits must not clip bars.
- Collapse the first row and edit the second. Bar settings collapse independently,
  new bars start collapsed, and saved choices survive slots and JSON round trips.
- Check **Bar 1 - Progress** headings and **Same row as** choices use a hyphen.
- Hide or remove the row leader and detach a member; preserve row margins, spacing,
  and collapse settings. Empty rows reserve no space.
- Validation: required Release rebuild and 34 focused notification tests passed;
  grouped-editor browser check passed. Latest live Playnite verification is pending.

## Custom notification progress bar elements - 2026-10-21

- Preserve independent bars for achievement progress, game completion, points,
  collection/prestige scores, and level experience in WPF, SAN, and the builder.
- **Same row as** explicitly selects a bar. Change either linked bar's placement,
  reorder bars, and remove the leader: surviving row members remain together.
- Hide the individual sample counter with **Show Progress Bar**. Its bar disappears,
  automatic-width neighbors fill the row, and empty rows contribute no height.
  Ordinary live achievements without numeric counters use the same fallback.
- Caption position/alignment precedes counter position/alignment. Check each font's
  family, size, color, bold, italic, underline, strikethrough, outline, and shadow.
  Small foreground glyphs remain sharp with effects and equivalent RGB/ARGB colors.
- Add, pick, and remove gradient stops; cancel a picker without changing the list.
  Check alpha hex values, fill/track gradients, direction, and solid fallbacks.
- Collapse a bar's editor and confirm its state survives saving a slot and reloading.
- Preserve all settings through style slots and JSON import/export.
- General Show Notification, selected/all provider-style previews, the style popup,
  and automatic preview display the same sample counters. With Show Progress Bar
  disabled, game/account bars still display while the individual bar disappears.
  Sample counters must never leak into saved settings or live unlock processing.
- Use the single **Custom banner/background image** picker.

## Custom schema cache isolation - 2026-10-20

- Apply a one-row custom schema, then Clear or disable it and refresh. The original
  Steam achievement list must return without restarting Playnite.
- Verify that a second game using the same Steam app ID keeps its original rows.
- Preserve global percentages, achievement groups, and the schema hash when
  building the per-game override container. Two regression tests cover this;
  eleven focused cache/progress tests and the Release rebuild passed.

## Custom notification progress - 2026-10-20

- Automatic progress beside the achievement name is superseded by the explicit
  optional/conditional text checks above (2026-10-23). Preserve progress tokens,
  per-notification isolation, and unchanged saved templates.
- Verify the progress token buttons, template builder sample counters, silent
  progress popups, and final unlock counter. Current focused tests cover formatting
  and notification-copy behavior; final live Playnite verification is pending.
- Keep native `progress`/`max_progress` support and the existing Custom Schema
  editor. Do not reintroduce the removed cat schema or aggregation experiment.

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
- View Achievements retains the upstream Edit Achievements action and adds Edit Local Achievements
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

## Maintenance logs

- Fork-specific messages use logger names ending in -Santodan, including
  [PlayniteAchievementsPlugin-Santodan] for fork calls sharing the plugin logger.
  Upstream messages retain their original names. All severity levels and exception
  overloads preserve their destination; repeated wrapping adds the suffix only once.
- Santodan-Fork is the first settings tab, with General, Platforms, Achievement
  Notification, and Theme Migration inside it. Its General page hosts cached-unlock
  protection and log tools; upstream General/Maintenance retains upstream content.
- General > Maintenance ends with Open logs, Copy Logs, and Delete Logs.
- Each action lets the user select playnite.log, extensions.log, and/or
  playniteachievements.log. Copy then asks for a destination folder and confirms
  overwrites; Delete confirms the selected files before deleting them.
- Playnite logs resolve from the application folder; the achievement log resolves
  from the plugin user data folder. Missing or locked files are reported individually.
- Selection dialog instructions follow the theme text color, and Cancel uses the
  plugin localization key. Successful Open operations show no completion dialog.
- The English resources include `LOCPlayAch_Button_Cancel` = `Cancel`; check
  Open, Copy, and Delete selection dialogs for a translated label rather than
  an unresolved resource-key placeholder (2026-10-20 bundle refresh).
- English resources also preserve Clear, Configure, OK, Reset, Save, Search,
  and the Providers header/tooltip. Check the Local Overrides clear buttons,
  Custom Schema save/clear buttons, import dialog OK, and notification controls
  for visible labels. All 22 referenced button keys and XAML button content/tooltip
  localization references passed the resource audit; live rendering remains pending.
- Verify these dialogs in Playnite, including cancellation, copying active logs,
  and log recreation after deletion. The Release rebuild passed.

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

- In-game monitoring remembers starting-cache unlocks, silent baseline reads,
  and notification claims for the whole game session. With cached-unlock
  protection disabled, a refresh that relocks achievements followed by a local
  Steam read restoring them must not replay notifications. New achievements
  still notify once across both monitoring sources; stopping the game clears
  session tracking. Regression coverage: `InGameSessionUnlocksTests`.

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

- Excluded folders accepts all nine path variables and environment variables, with
  cursor-insertion buttons matching Custom folders. Discovery and schema scanning
  expand exclusions for the current game; missing context skips the path. Template
  exclusions bypass shared discovery caching. Verify excluded descendants remain
  skipped case-insensitively while similarly named sibling folders remain available.
- Custom folders and Local folder/file overrides accept {InstallDir}, {GameDirName},
  {PlayniteDir}, {EmulatorDir}, {SteamAppId}, {GameName}, {ExecutableDir}, {RomDir},
  and {GameId}, plus environment variables. Expansion is case-insensitive and per
  game; unavailable values skip the path. GameName replaces invalid filename characters.
- Override templates remain editable as templates and resolve for reads and monitoring.
  SteamAppId uses the Local App ID resolver, including overrides. ExecutableDir uses
  the first file-based Play action; RomDir uses the first configured ROM.
- Variable buttons in Custom folders and Local Overrides insert at the cursor or
  replace selected text, move the caret after the token, and show description tooltips.
- Automatic folders and variables have matching reference tables with descriptions,
  equal widths, aligned headers, wrapped cells, and matching height limits.
- The 13 folder-template tests, Release rebuild, layout checks, and compiled WPF
  cursor insertion/replacement checks passed; verify the final tables in Playnite.
- Local appears with its default icon and settings defaults.
- Local folder discovery, extra/excluded paths, import, and refresh work.
- Applying a local folder override retains the game's installation folder in the
  multiple-folder dropdown alongside shared save and Steam cache folders.
- Nested steam_settings folders beneath the installation directory are detected,
  including their settings/stats subfolders; multiple matching locations appear in
  the dropdown. Excluded paths and directory links are skipped by the nested search.
- Folder dropdown entries and source path displays use on-disk casing and uppercase
  drive letters, regardless of the casing supplied by Steam or Playnite.
- The Automatic folders display includes each game's installation root and supported
  subfolders, Documents OnlineFix, ProgramData Steam, and Steam installation roots.
  It also explains nested steam_settings discovery with a Bin\x64 example.
- Custom-folder and excluded-folder Remove buttons display their label using
  `LOCPlayAch_Button_Remove` and enable when a folder is selected.
- While a game is running and its Local achievement file has not been found,
  folder discovery bypasses cached misses and rescans all configured roots for
  the supported JSON/INI filenames. The user-configured 1–300 second interval
  appears above **Refresh achievements when a game closes**; normal monitoring
  takes over after a file is detected.
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
- View Achievements routes `Edit Achievements` to the upstream editor and
  `Edit Local Achievements` to the local-file editor. The local action appears
  for Local-provider games or when a writable local file is detected.
- Opening the local editor refreshes folder discovery before resolving the file;
  a missing or unsupported file produces an editor message rather than hiding
  the action for Local-provider games.
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

- Display and Themes migration pages retain the upstream v4.1.1 layout and the
  two-way Revert selection fix. Fork-specific migration controls remain available
  in the separate Theme Migration tab on the main settings page.
- The fork Theme Migration page remains separate from upstream's migration
  pages.
- The newest unlocked achievement is highlighted above the compact/scrollable
  unlocked-achievement row.
- Both legacy `PluginCompactUnlocked` and modern
  `AchievementCompactUnlockedList` migration paths retain the highlighted row.
- Automatic migration handles first-time themes and upgraded themes when its
  Theme Migration toggle is enabled, and skips both when disabled. The toggle
  remains persisted and defaults to enabled.
- Scrollable achievements, highlight-latest, per-control Legacy/Modern choices,
  and automatic migration mode survive settings saves, reopening, JSON
  serialization, cloning, and copying. Custom control dictionaries are independent
  in cloned/copied settings.
- Automatic migration uses the saved Limited/Full/Custom mode and options for
  both first-time and upgraded desktop themes. Full is the desktop default;
  Fullscreen always uses Limited. Full respects scrollable/highlight choices;
  Custom also respects each control choice. Verify the mode selector reflects
  manual migration buttons and changes to custom choices.
- Theme Migration regression coverage: 15 targeted settings and migration tests
  passed and the Release rebuild succeeded on 2026-10-07. Broader settings
  round-trip tests still report the pre-existing missing
  `PreserveCachedUnlocksOnRefresh` clone/copy handling; this is separate from
  the Theme Migration settings.
- Clicking an automatic theme migration notification restarts Playnite directly
  through the host's built-in `PlayniteApplication.Current.Restart(true)` path
  on the UI thread. The English notification explains the click action. Verify
  in Playnite that activation restarts in the current mode and applies the theme;
  notification creation alone must not restart it. If restart is unavailable,
  the failure is logged and an error asks for a manual restart. Release rebuild
  passed; live activation remains pending.
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
- Migration preserves theme resource keys and StaticResource/DynamicResource
  references, including references in separate XAML files. Solaris must retain
  both `SuccessStoryIcon` and `PlayniteAchievementsIcon` without duplicate keys.
  Restore the original backup before migrating a previously damaged theme.
- The Revert button has an English label. Its dropdown updates the selected
  path in both settings pages. The detached Theme Migration page retains the
  original control's name scope so its bindings and click handlers use the same
  theme. Release rebuild and a WPF rehosting binding check passed; verify the
  complete Revert action in Playnite against a theme with a backup.
- PS5 Experience Limited migration marks the PS5Core trophy list with
  `Uid="PlayAch.Ps5TrophyProgress"`. The fork's runtime adapter corrects Local
  rows from their unlocked trophy counts and refreshes percentage, counter,
  and progress-bar bindings after PS5Core replaces rows or resets totals.
- The adapter discovers live trophy windows after startup, including copied
  templates that lose their marker or miss the initial Loaded event. Named-list
  discovery is enabled only when the active fullscreen theme contains the
  migration marker. Reverting the theme and restarting disables it.
- PS5 migration introduces no plugin assembly or converter-resource dependency
  into theme XAML: Playnite loads themes before plugins. It does not modify
  PS5Core.dll or the achievement database. `[PS5Progress]` log entries record
  adapter activation, live-list attachment, and corrected Local counts.
- Re-migration removes the earlier failed markup extensions, inline converters,
  and compiled-dictionary imports before enabling the runtime adapter.
- Opening a game from the PS5 trophy list keeps its detail header consistent
  with the list: four unlocked trophies out of 100 display `4%` and `4/100`.
  Cover both `AchievementsViewModel.SelectedGame` and `OverlayTrophyGame`.
  PS5Core 0.7.4 can omit Provider; the adapter must derive progress for those
  rows rather than falling back to stale zero totals. Provider can also contain
  platform labels such as `PC (Windows)`; do not use it to gate correction.
  Both list and detail progress derive from trophy counts. Navigation and refreshed
  selected-game objects update percentage, earned count and progress bar.
  The empty-provider regression and actual PS5Core tests pass; the user
  confirmed the detail-header correction on 2026-10-07.
- PS5 achievement detail items synchronize unlock states and dates from the
  fork's cached game data using the Playnite game ID and exact achievement API
  name, without rewriting the database or PS5Core.dll. Unmatched or ambiguous
  API names are left untouched. Reapply PS5Core's filter/sorting after changes,
  and notify `CanSelectUnlocked` and `CanSelectLocked` so the filter buttons update.
- With 11 unlocked achievements out of 100, All shows the correct states,
  Unlocked is enabled and lists the 11 earned achievements, and Locked lists
  the remaining 89. The user confirmed the fix on 2026-10-08. Cover changed
  cached states, unlock dates, idempotence and the bound button's enabled state.
- The StartPage add-on's Recent Achievements widget sorts achievements globally
  by unlock time while still respecting both its total maximum and its
  maximum-per-game setting.

Primary fork areas:

- `source/Services/ThemeMigration/`
- `source/Views/ThemeIntegration/Legacy/PluginCompactUnlockedControl.*`
- `source/Views/ThemeIntegration/Modern/AchievementCompactUnlockedListControl.*`
- `source/Views/SettingsControl.*`

## Steam and imports

- Santodan-Fork > Platforms contains Local and Steam Secondary; the main Platforms
  tab retains the original Steam settings. Local navigation links open the fork page.
- Local General, Import, and SuccessStory headers have settings, download, and trophy
  icons. Steam Secondary has an accounts icon and shares the Steam settings edit
  session with the original page.
- Steam Secondary presents import as a full-width section below Active account,
  using the same heading style. Verify account editing, imports, and Save/Cancel
  after switching between the original and fork platform pages.
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

- PS5 Experience shows consistent Local-game trophy counts and completion:
  four unlocked achievements out of 100 display `4%` and `4/100`.
  The obsolete schema-v19 database reconciliation was removed; preserve the
  Theme Migration/runtime adapter instead. Release rebuild and recovery/adapter
  tests passed on 2026-10-07, including actual PS5Core 0.7.4 Local rows, row
  replacement, reset counts, unload cleanup, theme loading
  before the plugin, and late discovery. The user confirmed corrected progress
  in fullscreen Playnite on 2026-10-07.
  Run `tools/ThemeMigration/Test-Ps5TrophyProgress.ps1` for migration recovery and
  `tools/ThemeMigration/Test-Ps5TrophyProgressBridge.ps1` with PowerShell `-STA`
  for the runtime adapter. These scripts use the local Playnite/PS5Core paths;
  adjust those paths when testing on another installation.
- On 2026-10-08, the progress adapter and achievement-state regression tests
  passed against PS5Core 0.7.4, including the `PC (Windows)` provider label and
  actual WPF Unlocked-button availability updates. Run
  `tools/ThemeMigration/Test-Ps5AchievementStates.ps1` with PowerShell `-STA`.
  Release rebuild passed; the user confirmed working progress and filters.

1. Run `Apply-ForkBundle.ps1 -DryRun` against a clean upstream worktree.
2. Apply the bundle.
3. Rebuild `source/PlayniteAchievements.csproj` in Release.
4. Install the newly timestamped `.pext` and restart Playnite.
5. Exercise each behavioral section above before replacing the previous
   working fork.
