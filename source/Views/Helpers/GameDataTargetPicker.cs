using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Workshop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Asks which library games a Workshop game data item goes onto, for Browse and the Library
    /// page alike. Games that already have the item are listed first, checked and locked; the
    /// games its keys match follow, the best match checked when the item is on no game yet; then
    /// the rest of the library. A newly picked game that already has custom data asks Merge or
    /// Replace; one without any takes the package as it is.
    /// </summary>
    internal static class GameDataTargetPicker
    {
        /// <summary>
        /// The new targets: the games picked that do not have the item yet. Null when the user
        /// cancels; empty when nothing new was picked.
        /// </summary>
        public static IReadOnlyList<GameDataTarget> Pick(
            PlayniteAchievementsPlugin plugin,
            Window owner,
            string workshopItemId,
            string itemName,
            IReadOnlyList<PortableGameKey> gameKeys)
        {
            var library = plugin?.PlayniteApi?.Database?.Games?.ToList() ?? new List<Game>();
            if (library.Count == 0)
            {
                return null;
            }

            var applied = new HashSet<Guid>(plugin.GameDataLinks.All
                .Where(pair => GameDataLinkService.Names(pair.Value, workshopItemId))
                .Select(pair => pair.Key));
            var matcher = plugin.CreateWorkshopGameMatcher();
            var match = matcher.Match(gameKeys);
            var candidates = matcher.Candidates(gameKeys).Select(game => game.Id).ToList();
            var checkedIds = new HashSet<Guid>();
            if (applied.Count == 0 && match != null)
            {
                checkedIds.Add(match.PlayniteGameId);
            }

            var ordered = library
                .OrderBy(game => applied.Contains(game.Id) ? 0 : candidates.Contains(game.Id) ? 1 : 2)
                .ThenBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var picked = PlayniteGamePickerDialog.PickMany(
                owner,
                ordered,
                string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_PickGame"), itemName),
                checkedIds,
                applied);
            if (picked == null)
            {
                return null;
            }

            var targets = new List<GameDataTarget>();
            foreach (var game in picked.Where(game => !applied.Contains(game.Id)))
            {
                var mode = WorkshopGameDataInstallMode.Replace;
                if (plugin.GameCustomDataStore?.HasPortableData(game.Id) == true)
                {
                    var question = string.Format(ResourceProvider.GetString("LOCPlayAch_Workshop_InstallChoiceGameData"), itemName, game.Name);
                    var chosen = ChooseMergeOrReplace(plugin, question);
                    if (chosen == null)
                    {
                        return null;
                    }

                    mode = chosen.Value;
                }

                targets.Add(new GameDataTarget(game.Id, mode));
            }

            return targets;
        }

        /// <summary>Merge, Replace or Cancel through Playnite's dialog; null on Cancel.</summary>
        public static WorkshopGameDataInstallMode? ChooseMergeOrReplace(PlayniteAchievementsPlugin plugin, string message)
        {
            var dialogs = plugin?.PlayniteApi?.Dialogs;
            if (dialogs == null)
            {
                return null;
            }

            var merge = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Common_Merge"), isDefault: true);
            var replace = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Button_Replace"));
            var cancel = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Common_Cancel"), isCancel: true);
            var picked = dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxImage.Question,
                new List<MessageBoxOption> { merge, replace, cancel });
            if (picked == merge)
            {
                return WorkshopGameDataInstallMode.MergeKeepingExisting;
            }

            return picked == replace ? WorkshopGameDataInstallMode.Replace : (WorkshopGameDataInstallMode?)null;
        }
    }
}
