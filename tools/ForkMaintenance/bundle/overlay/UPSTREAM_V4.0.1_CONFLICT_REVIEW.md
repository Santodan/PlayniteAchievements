# Fork features to review after the upstream v4.0.1 update

This is a checklist of functionality from this fork that may have been lost, reduced, or redirected while resolving the v4.0.1 conflicts in favor of upstream. It is intended to help restore fork behavior one feature at a time.

Last reviewed: 2026-10-05. Checked implementation items mean the code is restored, not necessarily that live behavior has been user-verified. Remaining verification is listed separately below.

No commit or push has been made. The resolved merge is staged and can still be reviewed before it is committed.

## Protected and already preserved

- [x] `AddonDBManifest.yaml` was not changed. The add-on database connection identity is preserved.
- [x] `source/extension.yaml` keeps the fork version `4.0.1.1`.
- [x] `InstallerManifest.yaml` was not changed.
- [x] The fork's separate Theme Migration settings area was not intentionally replaced by upstream's Display migration page.
- [x] RetroAchievements selected base/subset IDs were carried into the new custom-data format.
- [x] RetroAchievements can explicitly include or exclude the base set.
- [x] The combined custom-data schema is version 18 and includes both upstream achievement overrides and the fork's RetroAchievements selection data.
- [x] `Local` is available in the Manage Achievements Platform Override selector.
- [x] The fork's local-provider controls are restored as a main `Local Overrides` tab below `Notification`, including its `Local`, `Steam`, `Epic`, and `LumaPlay` internal tabs.
- [x] RetroAchievements game-ID and achievement-set selection is available in a dedicated `RetroAchievements` internal tab under `Local Overrides`, using the canonical custom-data subset fields.
- [x] The `Local Overrides` tab now loads its content instead of opening as a blank page.
- [x] Local Overrides uses a Settings-style left sidebar with platform icons. The Local entry uses the configured provider color and custom/borrowed icon rather than a fixed grey icon. Final visual confirmation of custom icons remains pending.
- [x] Local platform settings are separated into `General`, `Import`, and `SuccessStory`. Custom folders are on the left and excluded folders on the right; existing controls and handlers are retained.
- [x] Manage Achievements `Custom Data` includes the values configured through `Local Overrides`, including the RetroAchievements game ID and explicitly selected base/subset names with their IDs.
- [x] The top-level `Clear` action beside `Refresh` and `View Achievements` clears cached achievements, custom data, Platform Override, and Local overrides together.
- [x] The upstream `Clear Custom Data` label is preserved.
- [x] `Local Saves > Change Provider` now writes the canonical custom-data `Platform Override` shown in Manage Achievements instead of the obsolete preferred-provider setting.
- [x] View Achievements keeps the upstream `Edit Local Achievements` action and also has a separate `Edit Local File` action.
- [x] The View Achievements title-bar refresh button is restored next to the Minimize button and runs the single-game refresh command.
- [x] The dedicated local-file editor is restored for writable Local `achievements.json` and `achievements.ini` files.
- [x] The local-file editor accepts 24-hour times through `23:59`, including values such as `15:22` when the row previously held an AM/PM state.
- [x] Invalid capture-folder templates such as a literal `<gameName>` no longer abort creation of Playnite game-menu items; the scan failure is logged and contained.

## Fork functionality that may currently be missing

### Manage Achievements and manual editing

- [x] **The fork's separate Custom Schema tab is restored.** It can browse/apply an existing JSON schema or create and edit the schema parameters manually in the embedded JSON editor.
- [x] **Custom Schema is connected to the upstream Editor.** Applying or saving a schema selects the Local provider, clears stale game data, starts a single-game refresh, correlates schema entries with the Local achievement file by achievement key/API name, and reloads the Editor from the correlated result.
- [ ] **The old Manual Tracking tab/navigation is not present.** Calls that depended on that tab now open the unified Editor.
- [x] **The dedicated local-achievement editor window is restored.** `Edit Local File` opens it while the upstream `Edit Local Achievements` button still opens the unified Editor.
- [ ] **The dedicated local-folder override window is not used.** Its entry point also opens the unified Editor because there was no direct upstream equivalent.
- [x] **Custom Schema is included in Manage Achievements controller tab navigation.** Final controller focus-flow confirmation in Playnite is still recommended.
- [ ] **Portable package actions from the fork's old Overview layout may be missing or moved.** Check export/import actions and restore them in the new UI if absent.
- [ ] **Some fork provider-summary presentation may be missing.** Compare provider cards, exclusion status, and provider-specific actions with the previous fork.

What remains to restore: review the old Manual Tracking navigation, the dedicated local-folder override entry point, portable package actions, and provider-summary presentation. Custom Schema and its Editor integration are restored; the dedicated local-file editor remains a separate action intentionally.

### Steam settings and importer

- [x] **Steam settings have `Original` and `Secondary` tabs.** Original retains the upstream controls. Secondary restores API-key account management without adding a second browser login flow.
- [x] **The fork's owned-game import panel is restored in Secondary.** API-key imports read the selected account's owned games, assign its game-specific Steam account override, and retain the existing overwrite/skip options.
- [x] **The imported-game metadata-source selector is restored.**
- [x] **The active/default Steam account selector is restored.** Secondary accounts accept an API key plus Steam ID/profile URL; `Recognize account` resolves the profile and displays its name.
- [x] **Steam achievement refreshes use the selected account's credentials.** Game-specific Steam account overrides take priority; browser probes do not replace the selected API-key identity.
- [x] **Steam Account Override persistence is fixed.** An account override alone is now retained as custom data rather than discarded as an empty record. Database reload persistence is covered by a regression test.
- [x] **Apply and Clear on Steam Account Override trigger a single-game refresh automatically.** No separate Refresh click is required after a successful change.
- [x] **The family-sharing option is restored, with a limitation explicitly shown.** Family-sharing discovery uses the Original browser session; secondary API-key imports read owned games only. Full secondary-account family-sharing parity is not restored.

What remains to verify: real-account profile recognition, account switching, account-specific achievement counts, and imports with each metadata/overwrite option. The earlier wrong-account/empty-override report led to persistence and refresh fixes; these fixes still need live confirmation. Decide whether secondary-account family-sharing support is required.

### Overview and achievement ordering

- [x] **The fork's Recent/All Achievements sidebar tabs are restored.** The upstream v4.0.1 header and score-card layout remain intact; the additional tabs are hosted in the right achievements-panel header.
- [x] **The All Achievements materialization path is restored.** The `All Achievements` tab uses the fork's complete sidebar collection while retaining upstream's achievement grid and projection behavior.
- [x] **The `Custom (Manual)` ordering selector and `Configure` action are restored.** They are located in the achievements-panel header so they do not compress or replace upstream's top-level layout.
- [x] **Saved manual ordering is reapplied immediately.** The configuration flow reloads and applies the four independent sort definitions for game summaries, Recent Achievements, All Achievements, and the selected-game sidebar.
- [ ] **A full overview refresh may no longer explicitly reload the selected game in the same way as the fork.** This is represented by one remaining test failure.

What remains to restore: investigate the fork's deferred selected-game reload behavior without removing upstream's newer projection and performance improvements. The main Overview layout, All Achievements view, and manual ordering controls are restored.

### Notifications and achievement recordings

- [x] **Centered custom notification placement is restored.** Top-center and bottom-center positions are respected in screenshots and recorded clips.
- [x] **Custom-media notifications are restored in captures.** The selected custom template and transitions are used; screenshot and video folders use the same sanitized game name.
- [x] **HTML/WebView SAN frames are included in screenshots and recorded clips.** `SAN Screenshot View` respects View 1/View 2 selection. The user confirmed screenshots and videos working after these fixes on 2026-10-03.
- [ ] **The fork's active-card glow behavior may be missing or different.**
- [ ] **Session-effective recording quality selection may be missing.**
- [ ] **Changing recording settings may not reconfigure every part of the recorder exactly as the fork did.** A compatibility bridge exists so the project compiles, but behavior should be tested.

What remains to review: active-card glow, session-effective quality, and recorder reconfiguration. The live custom-notification/Memories integration, centered placement, WPF/WebView capture, and SAN screenshot-view selection are restored and user-verified.

### Theme integration

- [x] **The fork's scrollable unlocked-achievement control alias is registered.** `AchievementCompactUnlockedScrollableList` resolves to the modern unlocked list with its featured row disabled.
- [x] **The featured/latest layout works together with the scrollable Desktop control.** With both migration options enabled, the newest achievement is highlighted above the scrollable row; scroll-only and compact modes continue to work independently.
- [ ] **Fork score-snapshot persistence may be missing.**
- [x] **Theme Migration was checked manually.** The Desktop scrollable, highlighted-plus-scrollable, and compact combinations work, and the Fullscreen theme migration paths were reported working.

What remains to restore: determine whether the fork's score-snapshot persistence is still needed or missing. The scrollable alias and featured/latest Desktop layout are restored without removing upstream rarity glow or rarity bar support.

### Refresh, cache, and local-provider behavior

- [ ] **The fork's quieter realtime-poll logging may be missing.** Upstream logging is active.
- [ ] **The fork's ten-minute quiet-poll heartbeat may be missing.**
- [ ] **Local-provider refresh ordering may differ.**
- [x] **Polled unlocks use the fork's custom notification and Memories capture callback.** Standard toasts are suppressed when the custom notification is selected.
- [x] **Explicit Local editor saves can persist intentional relocks in the database.** Editor saves bypass the database's Local relock protection and report cache-write failure. `Open File Location` selects the actual achievement file in Explorer. Restart verification in Playnite is still pending.
- [x] **Cached-unlock protection is configurable.** `General > Maintenance`, below Clear Achievement Data, has `Preserve cached unlocks on refresh`, enabled by default. A per-game three-state override under `Local Overrides > Local Saves & Schema` can preserve cached unlocks, accept refreshed states, or inherit the global setting. It applies to all providers, including Steam, and Apply triggers a refresh.
- [x] **Disabling protection accepts a valid zero-unlock or lower-unlock result.** Both refresh rejection/preservation and the database's Local relock protection honor the effective setting. Failed or empty results remain protected. This supersedes the earlier automatic single-game Local bypass: ordinary Local refreshes now follow the explicit global/per-game policy.
- [ ] **Temporary manual-game refresh deferral may be missing.**
- [ ] **Some fork projection invalidation/performance behavior may be missing.**

What to restore: compare refreshes, local achievement updates, and polling behavior against the previous fork. These are mostly background behaviors, so verify logs as well as UI results.

## Restored code still awaiting live confirmation

- [ ] Select a secondary Steam account, recognize its profile, save settings, and confirm a refresh reads that account rather than the Original browser account.
- [ ] Apply a game-specific Steam Account Override; confirm the selection survives reopening/restarting and Apply refreshes automatically. Confirm Clear returns to the selected default account and refreshes.
- [ ] For the reported Destiny 2 case, disable per-game cached-unlock protection and Apply; confirm the old 23 unlocks are replaced by the selected account's zero unlocks. Also test lower nonzero counts, enabled protection, and global inheritance.
- [ ] Import a secondary account's games and verify their assigned account overrides, metadata selection, and overwrite/skip behavior. Family-sharing discovery remains browser-session-only.
- [ ] Save Local editor locks/unlocks, inspect the disk file, and restart Playnite. Ordinary refresh protection should follow the new policy; explicit editor saves should persist regardless of that policy.
- [ ] Confirm Local custom/borrowed icons and color in the left sidebar, and the General/Import/SuccessStory tabs plus side-by-side folder lists in Settings.
- [ ] Confirm Custom Schema controller focus flow and Overview manual ordering visually where not already verified.

### Score and display formatting

- [x] **Score digit grouping differences are accepted.** The user accepted locale-dependent comma/non-breaking-space separators on 2026-10-05; no display change is requested. Historical test expectations may still need alignment.
- [x] **Guild Wars 2 multi-tier threshold separator differences are accepted.** No display change is requested; these are formatting differences, not an outstanding fork-feature loss or evidence of incorrect score values.
- [x] **Fork-specific achievement display ordering is wired into the restored Overview controls.** Saving `Custom (Manual)` refreshes the relevant collections and grids immediately; final visual confirmation in Playnite is still recommended.

## Validation performed

- [x] Required Release rebuild succeeds with zero errors:
  `msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild`
- [x] No unresolved Git conflict markers remain.
- [x] The test project compiles and runs.
- [x] Focused local-file editor tests pass: 5 passed, 0 failed, including the `15:22` 24-hour regression case.
- [x] The restored upstream Overview layout plus `All Achievements` and manual-sort controls compiles in the required Release rebuild with zero errors.
- [x] The restored Custom Schema tab and its upstream Editor/cache-refresh integration compile in the required Release rebuild with zero errors.
- [x] Desktop Theme Migration was user-verified with scrollable plus highlight enabled; compact Desktop mode and Fullscreen migration were also confirmed working.
- [x] Custom WPF and WebView/SAN screenshots and videos, including SAN screenshot View 2, were user-confirmed working on 2026-10-03.
- [x] Steam-focused tests: 19 passed, including secondary-account API identity/unlock-time parsing and rejection of unreadable/private results.
- [x] Latest cache/custom-data focused tests: 93 passed, including retention of Steam-only account overrides, true/false per-game protection persistence, and disabled-protection zero-unlock acceptance while still rejecting empty payloads.
- [x] Latest required Release rebuild on 2026-10-05 succeeded with zero errors and four existing warnings.
- [ ] Historical full-suite result: 4,181 passed, 5 failed, and 1 skipped out of 4,187. The full suite has not been rerun after these later fixes; this is not a current failure count.

The five failures correspond to:

1. Guild Wars 2 locale-specific number formatting.
2. Two score-card locale-specific number-format expectations.
3. A bottom-center custom notification positioning expectation (historical test result; positioning has since been restored and user-verified, and this full-suite result has not been rerun).
4. The fork's deferred selected-game overview reload structure.

## Suggested review order

1. In Manage Achievements, load an existing Custom Schema and create one manually; confirm both correlate with the Local achievement file and populate the Editor after refresh, then review the remaining Manual Tracking actions.
2. Verify the restored Steam Secondary account selection/imports and cache-protection policy using the live-confirmation checklist above.
3. Visually confirm the restored Recent/All Achievements tabs and each saved manual ordering in Playnite, then investigate the remaining selected-game refresh difference.
4. Review the remaining capture glow/quality/reconfiguration items; verify a Local editor lock/unlock save survives restarting Playnite.
5. Review whether fork score-snapshot persistence is still required.
6. Exercise local-provider refresh and realtime polling while reviewing logs.
7. If rerunning the full suite still reports the accepted locale-format differences, align the tests with locale-aware behavior; do not change the display solely to satisfy old separator expectations.

Remove this file only after every unchecked item has either been restored or intentionally retired.
