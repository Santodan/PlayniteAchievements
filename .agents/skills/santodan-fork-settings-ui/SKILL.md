---
name: santodan-fork-settings-ui
description: Map of the PlayniteAchievements settings window and the Santodan-Fork tab - which sub-tab hosts which control, how fork content is attached, how settings edit/save/cancel works, and how to find or change UI labels and localization strings cheaply. Use before adding or moving a fork setting, changing a label, or deciding whether a control belongs to the fork tab.
---

# Settings window and Santodan-Fork tab

Read `santodan-fork-context` rules first. Only the Santodan-Fork tab is fork-owned;
the other top-level tabs (General, Platforms, Metadata, Friends, Display, Themes,
Notifications, Workshop) are upstream. Change those only for a minimal routing hook.

## Layout

`source/Views/SettingsControl.xaml` (`SantodanForkTab` > `ForkSettingsTabControl`):

| Fork sub-tab | Content set in `SettingsControl.xaml.cs` | Source |
| --- | --- | --- |
| General | `ForkGeneralSettingsContent` | `source/Views/Settings/General/ForkGeneralSettingsSection.xaml` (cached unlock protection, Debug mode, Logs buttons) |
| Platforms | `ForkProviderNavigationItems` master/detail | Local settings view, native source views, Steam secondary view |
| Achievement Notification | `AchievementNotificationsContent` | `LegacyNotificationSettingsControl.DetachAchievementNotificationsContent()` |
| Performance (Experimental) | `ForkPerformanceSettingsContent` | `ForkPerformanceSettingsSection.xaml` (+ `ForkPerformanceDefaults.cs`, Restore defaults) |
| Theme Migration | `ForkThemeMigrationContent` | from the legacy control; `source/Services/ThemeMigration/` |

`LegacyNotificationSettingsControl` is never placed in the visual tree itself; its
tab contents are detached into the shell. It binds to `LocalSettings` via the
inherited DataContext.

## Settings behavior

- Settings are transactional. Controls edit a copy (`GetSettingsForEdit`), **Save**
  persists and **Cancel** discards. Restore defaults changes only the edit copy.
- Provider settings (`LocalSettings`) deserialize with Newtonsoft defaults, so unknown
  keys are ignored. Removing a property is safe for old files; renaming one loses data
  unless you migrate it (`source/Models/Settings/*Migration.cs`).
- Upstream settings are `PersistedSettings` (`source/Models/Settings/PersistedSettings*.cs`);
  fork-compat fields are in `PersistedSettings.ForkCompatibility.cs`.

## Labels and localization

- Most fork UI text is hard-coded English in XAML. Upstream text uses
  `{DynamicResource LOCPlayAch_*}` keys from `source/Localization/en_US.xaml` (39
  locale files; crowdin syncs from en_US).
- `en_US.xaml` keeps nearly everything on a few very long lines, so a line-based grep
  prints huge output. Read one key with:
  `grep -oE 'x:Key="KEY"[^>]*>[^<]*' source/Localization/en_US.xaml | sed -E 's/ xmlns[^>]*>/ => /'`
- List a XAML file's literal labels without reading it:
  `grep -oE '(Text|Content|Header|ToolTip)="[^"{][^"]{2,}"' FILE | sort -u`
- Editing a key with `sed`: escape `&` as `\&` in the replacement (`&gt;` otherwise
  expands to the matched text). Check whether other locales carry the key before
  changing only en_US.
- The bundle compares localization semantically, so `&#10;` and a literal newline are
  equal.

## Manage Achievements (per game)

`source/Views/ManageAchievements/ManageAchievementsControl.xaml`, enum
`source/ViewModels/ManageAchievements/ManageAchievementsTab.cs`: Overview, Editor
(also covers manual tracking, filters, capstones, goals, notes, order and icons),
Category, Tags, Custom Schema, **Local Overrides** (fork; see
`santodan-fork-local-provider`), and Notifications (per-game notification/capture
overrides, upstream).
