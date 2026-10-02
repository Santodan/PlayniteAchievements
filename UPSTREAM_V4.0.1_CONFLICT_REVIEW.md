# Fork features to review after the upstream v4.0.1 update

This is a checklist of functionality from this fork that may have been lost, reduced, or redirected while resolving the v4.0.1 conflicts in favor of upstream. It is intended to help restore fork behavior one feature at a time.

No commit or push has been made. The resolved merge is staged and can still be reviewed before it is committed.

## Protected and already preserved

- [x] `AddonDBManifest.yaml` was not changed. The add-on database connection identity is preserved.
- [x] `source/extension.yaml` keeps the fork version `4.0.1.1`.
- [x] `InstallerManifest.yaml` was not changed.
- [x] The fork's separate Theme Migration settings area was not intentionally replaced by upstream's Display migration page.
- [x] RetroAchievements selected base/subset IDs were carried into the new custom-data format.
- [x] RetroAchievements can explicitly include or exclude the base set.
- [x] The combined custom-data schema is version 9 and includes both upstream achievement overrides and the fork's RetroAchievements selection data.
- [x] `Local` is available in the Manage Achievements Platform Override selector.
- [x] The fork's local-provider controls are restored as a main `Local Overrides` tab below `Notification`, including its `Local`, `Steam`, `Epic`, and `LumaPlay` internal tabs.

## Fork functionality that may currently be missing

### Manage Achievements and manual editing

- [ ] **The fork's separate Custom Schema tab is not present as the old tab.** Upstream's unified **Editor** is being used instead.
- [ ] **The old Manual Tracking tab/navigation is not present.** Calls that depended on that tab now open the unified Editor.
- [ ] **The dedicated local-achievement editor window is not used.** Its entry point now opens Manage Achievements on the unified Editor.
- [ ] **The dedicated local-folder override window is not used.** Its entry point also opens the unified Editor because there was no direct upstream equivalent.
- [ ] **Fork controller-navigation behavior for the old Custom Schema host may be missing.** Test the Manage Achievements screen using a controller.
- [ ] **Portable package actions from the fork's old Overview layout may be missing or moved.** Check export/import actions and restore them in the new UI if absent.
- [ ] **Some fork provider-summary presentation may be missing.** Compare provider cards, exclusion status, and provider-specific actions with the previous fork.

What to restore: move the useful parts of Custom Schema, manual/local editing, portable package actions, and controller navigation into upstream's unified Editor instead of restoring the obsolete tab container.

### Steam settings and importer

- [ ] **The fork's owned-game import panel may be missing from Steam settings.**
- [ ] **The imported-game metadata-source selector may be missing.**
- [ ] **The default Steam account selector may be missing.**
- [ ] **The family-sharing option may be missing.**

What to restore: add these controls to upstream's new Steam `SettingRow` layout and reconnect the existing fork settings/code where appropriate.

### Overview and achievement ordering

- [ ] **The fork's Recent/All Achievements sidebar tabs may be missing.** Upstream's new overview header/control bar won the conflict.
- [ ] **The All Achievements materialization path may be missing.** Verify that the fork can still show the complete achievement list where expected.
- [ ] **Custom/source ordering may differ in Recent Achievements.**
- [ ] **Custom/source ordering may differ in the selected-game sidebar.**
- [ ] **A full overview refresh may no longer explicitly reload the selected game in the same way as the fork.** This is represented by one remaining test failure.

What to restore: reintroduce the All Achievements view and fork ordering rules without removing upstream's newer projection and performance improvements.

### Notifications and achievement recordings

- [ ] **Horizontally centered custom notification placement may be missing.** Upstream's independent X/Y gap and inset placement is active.
- [ ] **Custom-media notification positioning may differ from the fork.**
- [ ] **HTML/WebView notification frames may not be included in recorded achievement clips.**
- [ ] **The fork's active-card glow behavior may be missing or different.**
- [ ] **Session-effective recording quality selection may be missing.**
- [ ] **Changing recording settings may not reconfigure every part of the recorder exactly as the fork did.** A compatibility bridge exists so the project compiles, but behavior should be tested.

What to restore: port centered/custom-media positioning and WebView capture into upstream's new multi-overlay export and placement APIs. A remaining toast-position test identifies the old centered behavior.

### Theme integration

- [ ] **The fork's scrollable unlocked-achievement control alias may no longer be registered.**
- [ ] **The fork's featured/latest compact layout may have been replaced by upstream's compact item layout.**
- [ ] **Fork score-snapshot persistence may be missing.**
- [ ] **The separate fork Theme Migration page must be opened and checked manually.** Upstream also has a Display migration page; they are different features and both may exist.

What to restore: re-register the fork theme alias and selectively reapply the featured layout and score snapshot behavior without removing upstream mastery bindings, rarity glow, or rarity bar support.

### Refresh, cache, and local-provider behavior

- [ ] **The fork's quieter realtime-poll logging may be missing.** Upstream logging is active.
- [ ] **The fork's ten-minute quiet-poll heartbeat may be missing.**
- [ ] **Local-provider refresh ordering may differ.**
- [ ] **The fork's polled-unlock callback behavior may be missing.**
- [ ] **Temporary manual-game refresh deferral may be missing.**
- [ ] **Some fork projection invalidation/performance behavior may be missing.**

What to restore: compare refreshes, local achievement updates, and polling behavior against the previous fork. These are mostly background behaviors, so verify logs as well as UI results.

### Score and display formatting

- [ ] **Score digit grouping may differ by locale.** Two score tests expect non-breaking-space grouping but the integrated behavior produced comma grouping in the test environment.
- [ ] **Guild Wars 2 multi-tier threshold labels may use different locale grouping.** One test expects a comma while the result used a non-breaking space.
- [ ] **Fork-specific achievement display ordering needs UI verification.** A compatibility property was retained so the project builds, but the final sort behavior should be checked.

## Validation performed

- [x] Required Release rebuild succeeds with zero errors:
  `msbuild PlayniteAchievements.csproj /p:Configuration=Release /t:Rebuild`
- [x] No unresolved Git conflict markers remain.
- [x] The test project compiles and runs.
- [ ] Test suite is not fully green: 4,181 passed, 5 failed, and 1 skipped out of 4,187.

The five failures correspond to:

1. Guild Wars 2 locale-specific number formatting.
2. Two score-card locale-specific number-format expectations.
3. The fork's deferred bottom-center custom notification positioning.
4. The fork's deferred selected-game overview reload structure.

## Suggested review order

1. Open Manage Achievements and identify missing Custom Schema/manual/local-editor actions.
2. Check Steam settings for the importer, account, metadata-source, and family-sharing controls.
3. Check Recent/All Achievements and custom ordering in Overview and the sidebar.
4. Trigger and record a centered custom-media notification.
5. Check both Theme Migration areas and the fork's compact theme controls.
6. Exercise local-provider refresh and realtime polling while reviewing logs.
7. Decide the desired locale formatting, then update either implementation or tests.

Remove this file only after every unchecked item has either been restored or intentionally retired.
