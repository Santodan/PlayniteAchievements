# Fork source and behavior map

Paths are relative to the repository root. Confirm current names with `rg --files`;
use this map to narrow discovery rather than assuming every fork feature has a
Santodan filename. The detailed behavior inventory lives in
`tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md`.

| Area | Start here | Behavior/reference to inspect |
| --- | --- | --- |
| Settings ownership | `source/Views/SettingsControl.xaml`, `source/Views/SettingsControl.xaml.cs` | `SantodanForkTab` contains fork General, Platforms, Achievement Notifications, Performance, and Theme Migration; adjacent top-level tabs are upstream-owned. Trace instantiated controls and bindings before editing shared settings classes. |
| Fork General and Performance | `source/Views/Settings/General/ForkGeneralSettingsSection.xaml`, `ForkPerformanceSettingsSection.xaml`, `source/Services/Settings/ForkPerformanceDefaults.cs` | `docs/ForkPerformanceOptions.md`, `LOCAL_PROVIDER_TROUBLESHOOTING.md`, checklist Experimental Performance section. Reset the edit copy; Save applies, Cancel discards. Keep reset coverage aligned with current controls. |
| Local detection, identity, definitions | `source/Providers/Local/LocalSavesProvider.cs`, `source/Providers/Local/Santodan/` | Checklist Local provider and game overrides; troubleshooting guide. Separate save-source identity from Steam schema identity. Explicit App ID overrides take precedence over links/notes for schema selection. |
| Polling and refresh | `source/Services/InGameMonitoring/SantodanLocalPolling.cs`, `SantodanPollingScope.cs`, `SantodanPollingConfiguration.cs`, `SantodanStartupBaselineStore.cs`; `source/Services/Refresh/RefreshRuntime.SantodanPolling.cs` | Performance doc maintenance boundaries. Scope must stay isolated across async/concurrent refreshes. Unknown fingerprints fall back to refresh. Account, source, folder, schema, and override changes must invalidate relevant reuse. |
| Refresh completion and capstones | `source/Providers/Local/Santodan/LocalSavesProvider.SantodanPolling.cs`, `source/Services/Achievements/AutoCapstone*.cs`, `SantodanAutoCapstoneTextRecovery.cs`, `CapstoneResolver.cs` | Checklist Local refresh completion section. Successful normal and custom-poll refreshes must report written game IDs in RebuildPayload, after update callbacks succeed. Preserve intentional deletion and one-time generation markers. |
| Steam/SteamHunters | `source/Providers/Metadata/SteamHunters/SteamHuntersApiClient.SantodanSchema.cs`, `source/Providers/Steam/SteamDataProvider.SantodanSteamHuntersSchema.cs`, `SteamDataProvider.SantodanPolling.cs`, `SteamHttpClient.SantodanPerformance.cs` | Public SteamHunters definitions need no authenticated achievement request and must not import another user's unlock state. Respect source preference ordering and localization limitations. Steam CEF authentication is separate from notification WebView2. |
| Custom data and overrides | `source/Services/GameCustomData/`, `source/Models/Settings/CustomAchievementDefinition.cs`, `source/ViewModels/ManageAchievementsViewModel.ForkOverrides.cs` | Checklist custom schema cache isolation, custom data, Local overrides. Preserve schema migrations, group/category metadata, percentages, and per-game isolation. |
| Notifications | `source/Services/NotificationPublisher*.cs`, `source/Services/Achievements/AchievementDataService.SantodanNotifications.cs`, `source/Views/LegacyNotificationSettingsControl*`, `source/Services/UI/SantodanMemoriesCapturePolicy.cs`; full map in `santodan-fork-notifications` | `ACHIEVEMENT_NOTIFICATIONS_GUIDE.md`, checklist notification/progress/text sections, `tools/NotificationTemplates/`. Distinguish Test/Preview from real unlock detection, baseline suppression, and deduplication. Preserve upstream-template opt-in behavior. |
| Capture and sound helpers | `source/Services/Captures/`, `tools/NotificationTemplates/`, `tools/SAN-source/notify/` | Checklist Memories and capture requirements; inspect helper callers and build scripts before changing protocols. A helper source change may also require a rebuilt packaged helper. Do not overwrite saved styles or paths. |
| Overview, rarity, session projections | `source/Services/Library/LibraryProjectionService.SantodanPolling.cs`, `source/Services/Showcase/`, `source/ViewModels/` | Checklist Overview and manual grid configuration. Preserve saved filters, custom data and score summaries. Do not replace the upstream loading UI as a fork optimization. |
| Theme/PS5/StartPage | `source/Services/ThemeMigration/`, `source/Services/ThemeMigration/ThemeDiscoveryService.SantodanCache.cs`, `tools/ThemeMigration/`, `tools/StartPageCompatibility/` | Checklist Theme and StartPage migration. Use migration/runtime adapters; do not resurrect obsolete schema-v19 DB reconciliation or patch PS5Core.dll. Theme XAML must load before plugin assemblies are available. |
| Logging and release identity | `source/Services/Logging/`, `source/Services/ReleaseMonitoring/ForkReleaseMonitor.cs`, manifests | Debug diagnostics should remain opt-in. Preserve separate upstream/fork release targets and add-on identity. Never infer credentials from logs or expose tokens. |
| Build/tests | `source/PlayniteAchievements.csproj`, `tests/PlayniteAchievements.Tests/PlayniteAchievements.Tests.csproj` | Production is .NET Framework 4.6.2/WPF; MSTest project links selected production sources and uses stubs. Check actual test inclusion before interpreting a passing test as production coverage. |
| Maintenance bundle | `tools/ForkMaintenance/README.md`, `FORK_FEATURE_CHECKLIST.md`, `fork-maintenance.json`, `bundle/bundle.json` | Generated overlays, full-index shared patch, semantic localization recipes, protected-file checks. `validation-progress-rows-20261022/` is historical and is not the active replay source. |

Some documentation under `docs/` is ignored by Git in this workspace. Use it if
present, but do not depend on it being available in a clean clone or force-add it
without a task reason. Prefer maintained tracked sources/checklists when absent.

Useful targeted searches:

```powershell
rg -n 'SantodanForkTab|ForkPerformanceTab' source/Views/SettingsControl.xaml
rg --files source tests/PlayniteAchievements.Tests | rg 'Santodan|Fork|Capstone|Polling|SteamHunters|Notification|Ps5'
rg -n '^##' tools/ForkMaintenance/FORK_FEATURE_CHECKLIST.md
```
