# Fork maintenance bundle

## Local refresh completion and automatic capstones - 2026-11-07

Preserve Local's completed-game reporting for normal refreshes as well as custom
polling. `RecordSantodanPollingRefresh` must record every successful write without
checking `SantodanPollingScope.IsActive`, and `LocalSavesProvider.RefreshAsync`
must return that summary in its `RebuildPayload` in both modes. Keep recording
after `onAchievementsUpdated` succeeds. Previously normal refreshes saved data
but reported zero refreshed games, leaving automatic capstone generation and
maintenance with no game IDs after clearing and refreshing achievements.

The required Release rebuild passed with zero errors and two existing warnings.
Live clear/refresh verification in Playnite remains pending. Preserve the existing
one-time generation markers and intentional capstone deletion behavior.

Bundle verification passed clean three-way preflight and actual replay onto a
temporary v4.2.1 checkout: 220 overlays, three localization recipes, component
hashes and protected-file checks passed. Both changed Local provider files
matched the working fork after newline normalization. The export also reflects
the existing removal of `UPSTREAM_V4.0.1_CONFLICT_REVIEW.md`.

## Experimental Performance controls and restore defaults - 2026-11-07

Santodan-Fork > Performance now has an Experimental badge and a fixed notice
above the scrolling settings. Preserve the two-row Grid: notice/reset controls
in the Auto row, options in the star-sized ScrollViewer row. The notice describes
as-is use, appearance/loading tradeoffs and restoring defaults when troubleshooting.

The Restore defaults button resets exactly the 24 settings on this tab, through
`Services/Settings/ForkPerformanceDefaults.cs`, using the Local provider edit copy.
It must not persist immediately: Save applies and Cancel discards. Other Local
settings and General Debug mode remain intact. Startup/artwork/cache changes need
a Playnite restart for a clean comparison. Keep reset coverage synchronized with
all checkbox and slider bindings when adding options.

Additional controls preserve existing behavior by default:

- Browser startup: defer Steam CEF cookie loading (off) and preload the fork
  WebView2 notification browser (on). Cookie deferral preserves authenticated
  requests, including direct token resolution without a session probe. CEF and
  WebView2 are separate engines; these switches do not disable required browser
  authentication or metadata requests.
- Background work: build overview data on demand (off); preload Steam-user menu
  choices (on); watch capture folders for external changes (on); defer scheduled
  refreshes during gameplay (off). Deferred scheduler checks resume within about
  15 seconds, re-evaluate staleness, and are cancellable on shutdown. Live polling,
  manual/game-close refreshes and already-running refreshes are unaffected.
  Disabling capture watchers requires restart; extension writers still invalidate
  their game data, and reopening the viewer rescans external changes.
- Artwork/previews: static WPF artwork (off), manual fork notification previews
  (off), release inline previews when leaving the page (off), and reduced fork
  notification effects (off). Static WPF artwork does not suppress animated images
  embedded in custom HTML templates. Reduced effects simplify WPF window
  transitions/shadows/outlines and CSS effects in generated SAN documents; custom
  scripts may still run. Saved appearance settings must not be overwritten.
- Memory: animated WebP cache budget 8-128 MB (default 128), and decoded-sound cache
  0-60 seconds (default 60). Cache budgets do not bound total process RAM: visible
  animation frames and active sound playback retain their own data. Smaller
  caches can trade memory for repeated decoding. Zero sound cache skips decoded
  clip retention and preload decoding. Preserve the helper process launch argument
  `--sound-cache-seconds`, its bounded parsing and the updated helper EXE in the
  package; the helper does not load Playnite provider settings itself.

The inline preview-loop timer now stops unconditionally on page unload. Optional
preview release also prevents hidden property changes from rebuilding inline
content. Manual mode suppresses queued automatic popups and replay loops while
retaining explicit Preview/Test actions.

Fork behavior is in Santodan partial/helper files; shared files contain routing
hooks. Preserve `ForkUiPerformance` for WPF image choices, the notification
browser/effects and legacy preview partials, Steam HTTP performance partial,
plugin startup partial, capture watcher partial, background updater partial,
`GameplayRefreshDeferral`, and the overview warm-up helper. Default-off behavior
must retain upstream behavior; do not disable schema upgrades or data migrations.
All controls remain under Santodan-Fork rather than upstream settings tabs.

The user-facing meanings, defaults, suggested configurations and reset/restart
instructions are in `LOCAL_PROVIDER_TROUBLESHOOTING.md`, below Collecting useful
diagnostics. README links are protected from bundle replay and stay manually
maintained. Version/release manifests are also protected.

Validation already completed for the implementation: 41 targeted background/cache
and settings tests passed; 66 targeted image, sound protocol, preview-resource
and settings tests passed; five reset/settings tests passed. These runs overlap
and must not be added together. Release rebuilds succeeded with zero errors and
two existing warnings. The package ZIP passed integrity checking and contains the
sound helper's cache argument. All 24 setting bindings and reset assignments match;
header/scroll row placement and XML were checked. Live layout, notification,
artwork, sound and memory comparisons for the newest options remain pending.

Bundle verification passed clean three-way preflight and actual replay onto the
existing v4.2.1 baseline (`4b2f60f29`): 221 overlays, three localization recipes,
component hashes and protected-file checks passed. All 377 compared working-fork
paths matched after newline normalization, including previously untracked helper
and test files. Validation used a temporary worktree; no snapshot is bundled.

## Account-free SteamHunters definitions for Local - 2026-11-05

Preserve the public SteamHunters schema path introduced after the v4.2.1
integration. SteamHunters metadata matching must not call the authenticated
`SteamManualSource.GetAchievementsAsync`. It reads public definitions through
`SteamHuntersApiClient.SantodanSchema`, with the existing browser transport,
request pacing and retries. Public definitions are English and must not be
marked as localized when the plugin language differs.

Local's **Prefer SteamHunters first (no login required)** selection tries those
definitions before any Steam authentication probe, including when an authenticated
schema was previously cached. **Prefer Steam sources first, then SteamHunters**
retains its Steam attempt and account-free SteamHunters fallback. Preserve the
Local settings wording, provider routing partial, schema parser and regression
tests. Achievement definitions must not import another user's unlock state.

Validation of the implementation: required Release rebuild succeeded and all
42 SteamHunters tests passed. Live no-account refresh verification in Playnite
remains pending. The bundle continues to target upstream v4.2.1; protected
release manifests are excluded.

Bundle validation passed clean three-way preflight and actual replay onto a
separate upstream v4.2.1 checkout: 208 overlays, two localization recipes,
component hashes and protected-file checks passed. All nine SteamHunters
implementation/test files match the replay after newline normalization. Unrelated
workspace changes were excluded from this bundle refresh.

## Upstream v4.2.1 integration - 2026-11-04

Updated to upstream tag `v4.2.1` (`4b2f60f29`), preserving the fork identity and
setting `source/extension.yaml` to `4.2.1.1`. InstallerManifest.yaml remains unchanged.
The original bundle failed preflight; shared conflicts were resolved through a
normal uncommitted merge, then the refreshed bundle completed the Update resume.

The integration retains fork account selection, Local polling and diagnostics,
RetroAchievements subset selection, unlock deduplication, custom tabs and saved
overview filters alongside the upstream metadata, tags and notification changes.
SteamHunters cache hooks follow its new metadata namespace and retain category
kinds and tags without replacing fresh progress. Custom-data schema 10 applies
the upstream category/tag migration to existing fork schema 9 files.

Validation: the required Release rebuild and package ZIP check passed. The full
suite passed 5,668 tests with eight skipped. The refreshed bundle applied cleanly
to a separate v4.2.1 checkout (205 overlays and two localization recipes). English
localization checks passed. Live Playnite verification remains pending. Changes
are uncommitted, with v4.2.1 retained as the pending upstream merge parent.

## Optional polling performance controls and diagnostics - 2026-11-04

Santodan-Fork > General owns Debug mode. Santodan-Fork > Performance owns
custom Local polling and the optional resource controls from the CPU investigation.
The full option/default/scope table and test instructions are in
`docs/ForkPerformanceOptions.md`, included in the bundle overlay.

Preserve these fork-owned implementations when merging upstream:

- `SantodanLocalPolling`, fingerprint gate, directory cache, and startup-baseline
  store: skip unchanged Local refreshes, preserve the configured interval, and
  optionally reuse validated startup data and achievement-file discovery.
- `SantodanPollingConfiguration`: fingerprint source-relevant settings without
  serializing notification templates and unrelated settings on each poll.
- `SantodanPollingScope`: isolate the custom async refresh path and per-poll
  discovery results. Its lifetime includes monitor reconfiguration; nested and
  concurrent upstream work must not inherit another poll's discovery results.
- `RefreshRuntime.SantodanPolling`: optional Local-only authentication preflight,
  including second-chance probes. Never mark remote accounts authenticated.
  Manual refreshes and upstream polling retain their original preflight.
- `LocalSavesProvider.SantodanDiscovery`: optional reuse of identical folder
  resolutions within one poll, including negative results. The original resolver
  body remains `TryResolveLocalFolderUncached`; the wrapper delegates directly
  when disabled. Configuration/provider/game changes separate entries. Results
  expire at the end of the poll, so missing files are searched again next poll.
- The optional waiting-source check attempts fingerprints even when an expected
  achievement file is missing. Available supported sources can skip unchanged
  refreshes; unknown fingerprints always retain the full-refresh fallback.
- SteamHunters metadata reuse, notification-summary caching, deferred overview
  precomputation, and theme-content caching remain individually controlled.
  Theme-content caching applies only to the fork Theme Migration page.
- Debug-only polling stages, CEF caller tracing, and optional process/thread CPU
  sampling remain disabled during normal use. Sampling adds its own overhead.

The three latest switches default off: Limit authentication checks to Local;
Check available sources while waiting for an achievement file; Reuse folder
discovery within each poll. Each requires custom Local polling. Do not change
upstream defaults or add these controls to upstream settings tabs. Shared files
retain only the necessary routing hooks; fork behavior lives in partial/helper
files. The overview's original loading animation remains restored.

Evidence: the supplied affected-user recording had 25 full fallback polls with
unavailable fingerprints, each initiating a Steam browser authentication check.
PerfView attributed about 12.47 CPU seconds inclusively to FindLocalFolders and
2.00 seconds to CEF out of 20.71 total process CPU seconds. These are sampled CPU
times, not elapsed time, and must not be compared as a controlled benchmark
against a different machine/game.

The developer's 2026-11-04 runtime recording with the options enabled showed no
sampled full Local refreshes or Steam authentication probes during the idle
section, with lightweight fingerprint checks instead. Idle memory stayed near
344 MB. psrecord's largest idle peak was 9.3%; most remaining peaks were 4.7% or
less. The user reported a maximum of 0.3% in Task Manager during idle. These use
different CPU scales and sample windows. Unlocks began at about 70 seconds,
after which full Local refresh work appeared. A 52.9% psrecord startup peak
remains separate. This supports an improvement on the developer's machine, not
proof that every spike or the affected user's case is resolved.

Validation: 111 targeted tests passed; the required Release rebuild succeeded
with zero errors and existing warnings. The package ZIP integrity check passed.
Affected-user runtime retesting and independent A/B comparison of each switch
remain pending. No release/version manifest changes are part of this update.

The refreshed bundle passed clean three-way preflight and actual replay onto
upstream v4.1.1 (`002eaa8b2`): 205 overlays, two localization recipes, component
hashes, and protected-file checks passed. All 347 compared non-protected,
non-localization fork paths matched the replay after newline normalization.
Validation used a temporary worktree; no validation snapshot is bundled.

## Missing English labels - 2026-11-01

The localization recipes now preserve the Default sort label and 55 additional
labels missing from US English. Four Epic local-save labels were also added to
British English. Existing British English labels and source fallback text supply
the English wording. The source scan found no remaining missing complete literal
plugin keys; both English dictionaries passed XML, duplicate-key, and empty-label
checks. The Release rebuild passed with zero errors and two existing warnings.

The localization-only bundle update passed clean three-way preflight and actual
replay onto upstream v4.1.1 (002eaa8b2), with 174 overlays and two localization
recipes. The existing patch and overlays are preserved. Live Playnite label
verification remains pending.

## Steam Community achievement icons and hidden flags - 2026-11-01

The Local provider now reads the embedded `valve-ssr-data` schema on Steam's
new Community achievement pages. It preserves API names, unlocked and locked
icons, and explicit hidden flags, while retaining the existing legacy parser.
The bundled LocalSavesProvider overlay and its manifest hash match the source.

The Release rebuild and packaging passed. Parsing the captured Phantom Blade
Zero page recovered 66 achievements, 66 icon pairs, and 31 hidden flags;
wrong-app rejection and legacy-page fallback checks also passed. In-app refresh
verification remains pending.

Bundle validation passed clean three-way preflight and actual replay onto upstream
v4.1.1 (002eaa8b2), including 174 overlays, localization, component hashes, and
protected-file checks. The replayed Local provider matches the source byte for
byte. Validation used a temporary checkout.

## Memories folder and filename wildcards - 2026-11-01

Live Memories screenshots now use the wildcard writer for clean, notification,
and framed variants instead of the standard achievement-index filename writer.
Folder and filename tokens expand independently. Date/time tokens use the
reported unlock time converted to local time; recording templates use the same
reported time, falling back to the observed unlock time when unavailable.
The screenshot test-folder option expands tokens before appending the Test folder.

The refreshed bundle includes the three changed services and the new
MemoriesWildcardTests regression test. The fix passed its Release rebuild and
focused test. Bundle validation passed clean three-way preflight and actual
replay onto upstream v4.1.1 (002eaa8b2), including 174 overlays, localization,
component hashes, and protected-file checks. All four fix files match the replay
after newline normalization. Replay used a temporary checkout. Live capture
verification in Playnite remains pending.

## Upstream notification template and General sections - 2026-10-25

Santodan-Fork > Achievement Notifications > General offers **Use upstream
notification template**, disabled by default. Enabled, live unlocks use the
upstream toast and capture pipeline for screenshots and video overlays. Disabled,
the fork renderer remains active. The Test Notification action follows the
selection, and the setting survives JSON serialization.

General groups controls into Notification template, Live unlock detection, and
Notification delivery cards. Help text is shorter; debug logging is inside a
collapsed Troubleshooting expander. Fork-only delivery controls are disabled and
dimmed with the upstream template selected, with guidance to upstream settings.
This supersedes the flat General layout described in the 2026-10-23 entry.

Validation: required Release rebuild and three targeted settings tests passed.
The refreshed bundle passed clean three-way preflight and actual replay onto
upstream v4.1.1 (002eaa8b2), including 173 overlays, one localization recipe,
component hashes, and protected-file checks. All five changed source/test files
match the replay after newline normalization. Live Playnite verification remains
pending. Replay used a temporary checkout; no validation snapshot was added.


## Single custom notification and General layout - 2026-10-23

Santodan-Fork > Achievement Notifications > General now offers **Use a single
custom notification**. Disabled by default, it preserves separate achievement
overlays. Enabled, WPF and SAN reuse the visible overlay for the latest achievement
and restart its display time. Closing or incompatible overlays are replaced;
persistent settings previews remain separate. The option survives settings clone
and apply. Standard notification tabs have no UI changes.

General's delivery and notification options now align with the settings above,
without a nested card background or extra card padding.

Validation: the required Release rebuild and both settings round-trip tests passed.
Live Playnite verification remains pending. The refreshed bundle passed clean
three-way preflight and actual replay onto upstream v4.1.1 (002eaa8b2), including
173 overlays, one localization recipe, component hashes, and protected-file checks.
All six relevant notification/settings files match the replay after newline
normalization. Validation used a temporary checkout; no snapshot is included.

## Conditional notification text and settings visibility - 2026-10-23

`<achievementName>` now expands to the name alone. Progress is explicitly placed
with `<progress>`, `<progressCurrent>`, `<progressMax>`, or `<progressPercent>`.
Optional groups such as `<achievementName>[[ (<progress> -)]]` include their
spaces and punctuation only when every wildcard inside has a value.

The condition editor sits below the wildcards and above the first text row.
Choose a wildcard, has-value/empty, equals/not-equals/contains, or a numeric
comparison (greater than, at least, less than, at most). Combine conditions with
All/AND or Any/OR, apply to a selected range or an entire chosen row, and optionally
supply otherwise text. Example: `[[if <progressPercent> gte 50::Halfway::Starting]]`.
Comparisons ignore case; numeric comparisons accept a trailing percent sign and
use invariant numbers. AND binds more tightly than OR. Groups cannot be nested;
combine conditions or use separate groups instead. WPF, SAN, previews, and the
standalone HTML builder support the syntax. Missing progress does not cause an
empty expression to revert to default notification text. Saved templates stay intact.

Text buttons use automatic sizing; the preview action row wraps instead of
constraining its labels to fixed columns. Settings/provider labels wrap while
icon and custom content templates remain intact. Collapsible settings groups
have borders, a subtle background, and inner padding, including the condition
editor. The duplicate implicit Button resource introduced during sizing changes
was removed: both Achievement Notification and Theme Migration load the same
legacy control and previously failed with the duplicate-key XAML exception.

Validation: Release rebuilds passed; 15 focused notification tests and the
legacy-resource duplicate-key regression test passed. Five browser formatting
checks and 12 WPF button layout checks passed. The full legacy resource dictionary
loaded in WPF with all 30 resources and every style resolved/sealed. Compiled
settings/provider dictionaries load and expander border thickness/padding were
checked collapsed and expanded. Final visual verification in Playnite is pending.
The refreshed bundle passed clean three-way preflight and actual replay onto
upstream v4.1.1 (002eaa8b2): 173 overlays, one localization recipe, component
hashes, and protected-file checks. All 11 changed source/test/builder files match
the replay after line-ending normalization. No validation snapshot was added to
the repository; the replay used a temporary checkout.

## PS5 Experience rank badge compatibility - 2026-10-22

The migration now covers all 25 achievement ranks in PS5 Experience's badge
sections. Existing tier 1/2/3 artwork and triggers retain their meanings; ranks
4 and 5 use tier 3 artwork, and Master1-5 use the top platinum badge (Plat, or
Plat1 when the theme has no Plat image). Actual achievement ranks, scores, and
levels remain unchanged. This repairs issue #80: level 54 produces Silver5,
which previously matched none of the theme's visibility triggers.

Discovery detects the missing aliases in already migrated themes. Limited
migration applies the repair, including fullscreen themes, and repeat migration
adds no duplicate triggers. The existing selective backup/revert flow remains
available. Install the rebuilt extension, apply migration again, and restart
Playnite; reinstalling the theme is unnecessary.

The required Release rebuild and 16 focused migration tests passed, including
all-rank coverage, repeated migration, discovery, and backup restoration.
An in-memory migration of the installed theme verified exactly one badge for
each of 25 ranks in all five sections (65 aliases), valid XML, and idempotence.
The installed theme was not modified during this check. Live verification of
the reporter's environment remains pending. The refreshed bundle passed three-way
preflight and actual replay onto clean upstream v4.1.1 with 171 overlays,
localization, component hashes, and protected-file checks. All four changed
source/test files match the replay byte for byte. Validation used a temporary
checkout outside the repository; no additional validation snapshot is included.

## Progress row layout and editor defaults - 2026-10-22

Progress bar settings are grouped by row. Each row owns its all-around and
per-side margins plus **Space between bars** (default 6 pixels; 0 joins adjacent
bars). Margins move every bar in the row together. Hidden row leaders retain
shared margins, empty rows reserve no space, detaching copies row settings,
and removing the leader preserves them for surviving bars. Negative margins
remain available for deliberate overlap; zero spacing attaches progress rows
to occupied main content instead of unused fixed-height space.

Rows and individual bars have independent saved collapse states. New bar
editors default to collapsed; existing saved choices are preserved. Headings
and row selectors use a plain hyphen, for example **Bar 1 - Progress**.
WPF, SAN, the standalone builder, style slots, and JSON templates carry the
row settings. The SAN host also updates its height limits to accommodate bars.

The required Release rebuild and all 34 focused notification tests passed.
A browser check verified grouped editors, margin editing, and independent row
collapse. The refreshed bundle passed three-way preflight and actual replay onto
clean upstream v4.1.1 with 170 overlays, localization, component hashes, and
protected-file checks. All eight changed code/test/guide files match the replay
byte for byte. Interactive verification of the latest package in Playnite is pending.

## Custom notification provider filters - 2026-10-22

Custom live notifications now resolve the same provider/global notification policy
and saved per-game behavior overrides as standard notifications before dispatching
in-app messages, popups, or sound. Unlock, progress, and friend-unlock switches
are resolved independently; explicit previews bypass the filters. Screenshot and
recording event processing remains separate.

Platform filters follow the achievement provider. Steam games monitored through
Local use Local's notification switches, including when Local reads Steam appcache
stats. The reported Steam-game notification was confirmed in the installed log as
provider Local; no change to provider selection is needed.

The Release rebuild and 15 existing provider-policy tests passed during fix
verification. The refreshed bundle passed three-way preflight and actual replay
onto clean upstream v4.1.1 with 170 overlays, localization, component hashes,
and protected-file checks. The replayed notification publisher matches the
working source byte for byte.

## General notification preview counters - 2026-10-21

General's Show Notification action and the selected/all provider-style previews
now explicitly request settings-preview counters. They share the same counter
factory as the style popup and automatic preview: 16/100 for the individual
achievement, plus game completion, points, collection/prestige scores, and level
experience samples. Show Progress Bar hides only the individual sample bar.
Sample values are cloned per preview and never enter saved settings or live
unlock processing. Existing delivery modes and test sounds are preserved.

The Release rebuild and all 28 focused notification tests passed. New tests
cover every preview source, the hidden-individual fallback, and sample isolation.
The refreshed bundle passed three-way preflight and actual replay onto clean
upstream v4.1.1: 170 overlays, localization, hashes, and protected-file checks.
All five changed code/test/guide files match the working source byte for byte.

## Excluded-folder variables - 2026-10-21

Excluded folders accepts the same nine path variables and environment variables
as Custom folders, with shared cursor-insertion buttons and updated help text.
Exclusions expand per game for folder discovery and schema scanning; unavailable
details skip the path, and template exclusions bypass the shared discovery cache.
The Release rebuild, 13 variable tests, and production exclusion checks passed,
including per-game expansion, missing emulator context, descendant matching,
case-insensitive comparisons, and sibling preservation.

## Custom notification progress bar elements - 2026-10-21

The notification editor supports multiple independent graphical bars for
achievement progress, game achievement counts and points, collection/prestige
scores, and level experience. Bars share a row through the persistent
**Same row as** selector. Changing either linked bar's placement moves the row;
removing its leader preserves the remaining links. Previous-bar flags migrate
automatically. Unavailable counters hide their bar, automatic-width neighbors
expand, and empty rows reserve no height.

Caption/counter positions, alignments, fonts, sizes, colors, decorations,
outline, and shadow are independent. Gradient fill/track colors have Add color,
Pick, Remove, and alpha-capable hex fields. WPF draws effects behind sharp,
pixel-aligned foreground text; SAN paints stroke behind fill. Each bar's editor
can be collapsed, and its expanded state is saved. The preview checkbox
is labeled **Show Progress Bar**; it controls the individual sample counter,
while game/account sample bars remain available. Show Notification and automatic
preview share sample counters. The shared image picker is labeled
**Custom banner/background image**. Slots, import/export, and the standalone
HTML builder preserve the complete bar definitions.

The Release rebuild passed with no errors. All 26 focused notification tests
passed during feature verification. The refreshed v4.1.1 bundle passed both
three-way preflight and actual replay in an isolated checkout: 169 overlays,
localization recipes, component hashes, and protected-file checks. All 304 replayed
code/tool/test files match the working source; two differ only in Git-normalized
line endings. Interactive testing of the rebuilt package in Playnite remains pending.

The preview PNG under build is a diagnostic artifact, excluded from the bundle.

## Custom schema cache isolation - 2026-10-20

Applying a game's custom schema now replaces achievement rows on a separate
schema container instead of modifying the shared Steam schema cache. Clearing
or disabling the override therefore exposes the original Steam rows again,
including for other games that use the same app ID. Global percentages, groups,
and schema hash are preserved. Two regression tests cover cache isolation;
all eleven focused cache/progress tests and the Release rebuild passed.
Bundle preflight and actual replay against clean upstream v4.1.1 passed with
165 overlays. All three changed code/test files match after replay; protected
files, component hashes, and localization validation passed.

The local schema-inspection JSON under build is a diagnostic artifact and is
excluded from this bundle refresh.

## Custom notification progress - 2026-10-20

Custom text notifications carry each achievement's current and maximum progress.
Automatic name suffixing described in the original progress implementation has
been superseded by explicit placement and conditional text (2026-10-23 above).
Tokens include `<progress>`, `<progressCurrent>`, `<progressMax>`,
and `<progressPercent>`; the editor and HTML template builder expose them.
Per-notification settings copies preserve saved templates and keep progress
popups silent. Unlock events also carry their final counter when available.

The experimental cat schema, native-unlock aggregation, and schema-only test
script were removed. The existing Custom Schema editor and native numeric
`progress`/`max_progress` support remain. Nine focused notification tests pass;
live display of the final correction remains to be verified in Playnite.
The Release rebuild and clean v4.1.1 bundle dry run passed, including patch
preflight, 164 overlays, localization recipes, hashes, and protected-file checks.

## v4.1.1 integration - 2026-10-20

Fork-specific log calls append -Santodan to the logger name, for example
[PlayniteAchievementsPlugin-Santodan]. Upstream log calls retain their existing
names. The wrapper preserves severity, exceptions, and destination, reuses named
fork loggers, and avoids duplicate suffixes. The Release rebuild and seven logging
tests passed. That logging-only refresh excluded the then-ongoing custom-schema
experiment; the notification progress refresh above supersedes that exclusion.
The isolated logging-only Release rebuild and bundle dry run passed against
upstream v4.1.1, including 162 overlays, localization recipes, and component hashes.

Custom folders and Local save-folder/file overrides support {InstallDir},
{GameDirName}, {PlayniteDir}, {EmulatorDir}, {SteamAppId}, {GameName},
{ExecutableDir}, {RomDir}, and {GameId}, alongside environment variables.
Templates resolve per game; unavailable details skip that path. Override templates
remain stored as templates and resolve for reads and monitoring. Shared variable
buttons insert at the cursor or replace selected text in both input fields.
Automatic folders and variables use matching side-by-side reference tables with
descriptions, wrapping, and aligned headers. The Release rebuild, 13 targeted tests,
layout checks, and compiled WPF cursor-insertion checks passed. Live verification
of the final table layout remains pending.

The first settings tab is Santodan-Fork, containing General, Platforms,
Achievement Notification, and Theme Migration. General hosts cached-unlock
protection and the log tools; upstream General/Maintenance retains its upstream
content. Fork Platforms hosts Local and Steam's Secondary settings, while the
original Steam settings remain under the main Platforms tab. Local's General,
Import, and SuccessStory tabs use settings, download, and trophy icons; Secondary
uses an accounts icon. Steam import is a full-width section below Active account
with the same heading style. Release rebuilds and layout checks passed; live
verification of the final import layout remains pending.

The migration pages under Display and Themes use the upstream v4.1.1 layout,
retaining the two-way Revert selection fix. Fork migration controls remain in the
separate Theme Migration tab on the main settings page. The Release rebuild passed;
live page verification remains pending.

Installation discovery also finds nested steam_settings folders, including their
settings/stats subfolders, and lists each matching location. The nested search
respects excluded paths and skips directory links. The Automatic folders display
describes this behavior with a Bin\x64 example. Folder dropdown entries and source
path displays use the casing stored on disk with uppercase drive letters.
Discovery checks covered multiple depths/candidates, exclusions, root deduplication,
and a junction loop; path-casing checks passed. Live Playnite verification is pending.

Local folder discovery uses the same candidate list with and without an override,
so applying a folder override keeps the game's installation folder in the dropdown.
The Release rebuild passed; live dropdown verification remains pending.

The localization recipes preserve the missing Clear, Configure, OK, Reset, Save,
and Search button labels and the Providers header/tooltip. All 22 referenced
button keys and XAML button content/tooltip localization references resolve in
the English resources. The Release rebuild passed; live rendering remains pending.

View Achievements labels the upstream action `Edit Achievements` and the local-file
action `Edit Local Achievements`. The local action remains visible for Local-provider
games even when file discovery fails, and also appears when a writable local file
is detected. Opening the local editor resets folder discovery before resolving the
file; unsupported or missing files produce an editor message. The Release rebuild
passed; live Playnite verification remains pending.

The Local Automatic folders display also includes each game's Playnite installation
folder and supported subfolders, Documents OnlineFix, ProgramData Steam, and the
Steam installation roots already searched by discovery.

The bundle preserves the English `LOCPlayAch_Button_Remove` resource used by
Local settings, including the custom-folder and excluded-folder Remove buttons.
The fix passed the Release rebuild with no errors and two existing CS0649 warnings.
Live button rendering remains pending.
The refreshed bundle passed a dry run against a clean upstream v4.1.1 checkout,
including patch preflight, 155 overlays, localization, hashes, and protected files.

The bundle also preserves the Overview tab label fix using upstream's existing
`LOCPlayAch_Achievements` resource ("Achievements"). No fork localization key
is added; the obsolete `LOCPlayAch_RecentAchievements` reference is removed.
The refreshed bundle passed a dry run against clean upstream v4.1.1.

The bundle now targets upstream v4.1.1
(`002eaa8b2cdcc5df4c6f4bf2d886c5fbbc2390b2`). The in-place update
retains that commit as the pending merge parent; no commit or push was made.
Steam scanning combines upstream cached-category preservation with the fork's
per-game credentials. Notifications retain upstream progress coalescing and
exception handling alongside custom capture content and selective overlay tracks.
The badge test follows upstream's default/custom color sequence, and the Workshop
package field fixture includes the fork's RetroAchievements subset selection.

The required Release rebuild produced version 4.1.1.1 successfully. The refreshed
bundle passed validation in a clean v4.1.1 checkout, including the three-way patch,
155 overlays, localization recipes, component hashes, and protected-file checks.
The full regression suite passed: 5,027 tests passed, eight skipped, none failed.
Live Playnite verification remains pending.

## Bundle refresh — 2026-10-20

This refresh also preserves the Solaris resource-key collision fix and its
cross-file resource-reference regression test, the Revert button label and
two-way selection bindings, and the detached migration page's name scope.
The Release rebuild, 13 theme discovery/migration tests, and WPF rehosting
binding check passed. Full Revert interaction in Playnite remains pending.
The regenerated bundle passed a dry run in an isolated checkout of the v4.1.0
baseline, including patch preflight, 155 overlays, localization, hashes, and
protected-file checks.

The bundle captures the current working fork against the existing v4.1.0
baseline, including the missing English `LOCPlayAch_Button_Cancel` resource
used by all three Maintenance log selection dialogs. The fix's Release rebuild
passed with no errors and two existing CS0649 warnings. Live dialog verification
remains pending. This refresh does not fetch upstream, commit, or push.
The regenerated bundle passed the dry run in an isolated checkout of that
baseline: three-way patch preflight, 155 overlay files, localization recipes,
component hashes, and protected-file checks.

## v4.1.0 integration — 2026-10-18

The current bundle targets upstream v4.1.0
(`bd9a01d8a87c82ae90c5278ac82f324f227dafff`). The in-place update retains
that commit as the pending merge parent; committing and pushing the reviewed
merge is required before GitHub's behind count changes.

The Release rebuild passed. The regression suite passed 4,997 tests, with eight
skipped and none failing. The PS5 completion-icon regression script also passed.
Live Playnite checks remain necessary for notification rendering, Memories
capture, account switching, and the updated Overview UI.

Export now constructs the shared patch using a temporary index seeded from the
upstream baseline. This preserves upstream-added files during an uncommitted
integration without changing the user's staging. The refreshed bundle is checked
by replaying it into an isolated upstream checkout and comparing project files.

This directory turns the Santodan fork into a repeatable layer over an upstream
PlayniteAchievements checkout. It intentionally does not modify or package:

- `README.md`
- `AddonDBManifest.yaml` (the fork's add-on database identity)
- `source/extension.yaml`
- `InstallerManifest.yaml`

The bundle has three parts:

1. **Overlay files** — files that exist only in the fork. These are copied into
   a new upstream checkout and normally have no merge conflicts.
2. **A three-way Git patch** — only upstream-owned files changed by the fork.
   Full-index blob IDs let Git merge unchanged portions automatically.
3. **Localization recipes** — translation entries are merged by `x:Key`
   instead of by line, avoiding conflicts caused by reordered or reformatted
   resource dictionaries.

Git `rerere` is enabled while applying a bundle, so a conflict resolution Git
has seen before can be reused on later upstream updates.

The behavioral inventory in
[`FORK_FEATURE_CHECKLIST.md`](FORK_FEATURE_CHECKLIST.md) is the post-merge test
checklist for the fork features that have historically been lost during
upstream integrations.

The v4.0.1 recovery status and remaining live checks are recorded in
[`../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md`](../../UPSTREAM_V4.0.1_CONFLICT_REVIEW.md).
The 2026-10-07 bundle refresh captures the complete current working tree,
including uncommitted fixes; it is not a new upstream fetch, commit, or push.
Implementation coverage does not replace the pending live checks in that review.
This refresh replaces the obsolete cache schema-v19 reconciliation with the
working PS5 Experience Theme Migration/runtime adapter. The migrated theme
enables correction of PS5Core Local trophy progress in memory, including after
refreshes, without changing PS5Core.dll or the achievement database. Theme XAML
contains no dependency on plugin assemblies/resources during startup. The user
confirmed corrected fullscreen percentages on 2026-10-07, including the selected
game's trophy-detail header. That header uses a separate PS5Core selected-game
object. The adapter covers its percentage, earned count and progress bar, as well
as the trophy overlay. PS5Core can omit Provider or populate it with a platform
label such as `PC (Windows)`. Both list and detail progress use trophy counts
consistently rather than gating correction on that unreliable provider label.
The 2026-10-08 refresh also captures synchronization of individual achievement
states and unlock dates from the fork cache by game ID and exact achievement API
name. The adapter refreshes PS5Core filtering/sorting and notifies
`CanSelectUnlocked`/`CanSelectLocked`, so the Unlocked filter becomes selectable
after synchronization. The user confirmed the progress and filter fixes on
2026-10-08. Regression coverage includes actual PS5Core 0.7.4 objects and a live
WPF filter-button binding.
The refresh also captures persistent Theme Migration scrollable/highlight and
per-control choices, the automatic Limited/Full/Custom mode selector, and use of
those saved options for first-time and upgraded themes. Desktop defaults to Full;
Fullscreen retains Limited migration. The 15 targeted regression tests and
Release rebuild passed; live settings-dialog/startup checks remain to be verified.
Automatic theme migration notifications now restart Playnite when clicked, using
Playnite's built-in restart method on the UI thread. The English notification
explains this action. The Release rebuild passed; a live notification-click
restart check remains pending.
Git output and generated JSON/XML/patch reads explicitly use UTF-8, including
under Windows PowerShell 5.1, to preserve non-ASCII text during export/application.
The bundle also captures session unlock tracking to prevent cached achievements
from replaying notifications after a refresh relocks them. Starting-cache unlocks,
silent baseline observations, and announced unlocks remain remembered until the
game stops. Seven targeted tests passed and the Release rebuild succeeded.
The refresh also includes General > Maintenance log actions with file selection
for opening, copying, and deleting logs, theme-aware dialog instructions, and the
localized Cancel button. Opening logs only reports errors. The Release rebuild
passed; live dialog checks are listed in the feature checklist.

The refreshed bundle was dry-run validated against a separate clean checkout of
upstream v4.0.1 (`79108753e3f8ddc775aa569ff8f5ed0362f3f22b`): the three-way
patch, fork-only overlays, localization recipes, hashes, and protected-file
checks passed. This validates replay against that baseline, not conflict-free
application to an as-yet-unseen future upstream release.

## Refresh the bundle from the working fork

Run this after the fork is working and tested:

```powershell
cd E:\Programs\Playnite\CustomExtension\PlayniteAchievements-main
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Export-ForkBundle.ps1 `
    -Baseline upstream/main `
    -Force
```

The export compares the complete working tree (staged and unstaged changes)
with the selected upstream baseline. Fork-only files, shared-file changes, and
localization keys are captured separately. Ignored build output is never
included.

Review the result:

```powershell
git diff -- tools/ForkMaintenance
```

The generated `bundle/bundle.json` records the exact upstream commit and SHA-256
hashes for every generated component.

## Update the current checkout in place

Run the `Update` action from the normal project directory. It fetches upstream,
replaces the tracked non-protected project files with `upstream/main`, and applies
the fork bundle directly in the current checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -UpstreamRef upstream/main `
    -NoPause
```

The changes remain uncommitted and appear immediately in the current checkout's
VS Code Source Control panel. The action preserves `README.md`,
`source/extension.yaml`, `InstallerManifest.yaml`, and `tools/ForkMaintenance`.
`AddonDBManifest.yaml` is also protected to retain the fork's update identity.
It refuses to start if other local changes exist, preventing unrelated work from
being overwritten. When upstream is not already an ancestor, the action also
opens a no-content merge before assembling the updated files. After review,
stage and commit normally in VS Code or Git: that commit will have the previous
fork tip and the selected upstream commit as its two parents, so GitHub will not
report the fork as behind those upstream commits. Use `-SkipFetch` only when the
selected upstream ref is already current locally.

If the patch reports conflicts, resolve and stage those files, then resume the
same in-place workflow without resetting the checkout again:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\ForkMaintenance\Run-ForkMaintenance.ps1 `
    -Action Update `
    -ResumeAfterConflict `
    -NoPause
```

`-ExecutionPolicy Bypass` applies only to that PowerShell process. It does not
change the machine-wide or user-wide execution policy.

The dry run checks:

- patch applicability using Git's three-way merge;
- collisions with fork-only overlay files;
- independent upstream edits to localization keys;
- bundle hashes;
- protected manifests.

The same validation can be invoked through `Test-ForkBundle.ps1`.

If application leaves a real shared-file conflict, resolve it normally. Git
`rerere` records that resolution for later updates. Do not use
`-ForceOverlay` or `-ForceSemantic` until the reported collision has been
reviewed; those switches deliberately choose the fork's version.

## Recommended upstream workflow

1. Run `Run-ForkMaintenance.ps1 -Action Update` in the normal checkout.
2. Resolve only the reported shared-file conflicts, if any.
3. Build and test the extension.
4. Review every change in VS Code.
5. Stage and commit the reviewed update; the pending upstream merge parent is
   retained automatically.
6. Export a fresh bundle against the new upstream baseline.
7. Update the protected manifests manually, outside this tooling.

## Reducing the remaining shared patch

The bundle makes future updates repeatable immediately. Conflicts can be reduced
further over time by moving fork implementations into additive files:

- C# partial classes named `*.Fork.cs`;
- fork-specific services behind small upstream integration hooks;
- separate XAML controls/resource dictionaries;
- one registration table for providers, tabs, and context-menu commands.

After such a refactor, the shared patch should contain mostly small registration
hooks while the implementation remains in conflict-free overlay files.
