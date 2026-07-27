# StartPage compatibility DLL patch

`Patch-GlobalRecentAchievements.ps1` applies the Santodan compatibility change
to the bundled `StartPagePlugin.dll`.

The compatibility provider reads the current user's achievements globally by
their real `UnlockTimeUtc`, then applies both StartPage settings:

- `MaxNumberRecentAchievements`
- `MaxNumberRecentAchievementsPerGame`

The StartPage view continues to group those results by game. The patch does not
remove or alter that grouping.

The original compatibility query used the game's cache `LastUpdatedUtc` when an
achievement had no unlock timestamp. Importing or refreshing an unplayed game
could therefore make its achievements look recent. The patch removes that
fallback and excludes achievements without a genuine unlock timestamp.

PlayniteAchievements v3 stores current-user and friend progress in the same
achievement tables. The patch joins the owning `Users` row and accepts either:

- an authenticated/current user (`IsCurrentUser = 1`); or
- a Local-provider game owned by the synthetic Local user.

The second condition supports legacy and migrated Local rows whose synthetic
user may not be marked current. It cannot admit friends from Steam,
RetroAchievements, or Exophase.

Run after replacing the bundled StartPage DLL:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\StartPageCompatibility\Patch-GlobalRecentAchievements.ps1
```

The script verifies the per-game setting, game grouping, unlock-date sorting,
personal/Local ownership filter, and corrected query before replacing the
bundled file.
