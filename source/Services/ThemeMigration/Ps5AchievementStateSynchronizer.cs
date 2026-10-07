using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.ThemeMigration
{
    internal static class Ps5AchievementStateSynchronizer
    {
        internal static bool Synchronize(object viewModel, GameAchievementData data, ILogger logger)
        {
            if (viewModel?.GetType().FullName != "PS5Core.AchievementsViewModel" || data?.Achievements == null) return false;
            var type = viewModel.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var items = type.GetField("_allAchievements", flags)?.GetValue(viewModel) as IEnumerable;
            var refresh = type.GetMethod("ApplyAchievementFilterSort", flags, null, Type.EmptyTypes, null);
            if (items == null || refresh == null) return false;
            // Match provider API identities, never localized display names or list order.
            var states = data.Achievements.Where(item => item != null && !string.IsNullOrEmpty(item.ApiName))
                .GroupBy(item => item.ApiName, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            var changed = 0;
            foreach (var item in items)
            {
                var itemType = item.GetType();
                var apiName = itemType.GetProperty("ApiName")?.GetValue(item) as string;
                if (apiName == null || !states.TryGetValue(apiName, out var state)) continue;
                var unlocked = itemType.GetProperty("IsUnlocked");
                var date = itemType.GetProperty("UnlockDate");
                if (unlocked?.CanWrite != true || date?.CanWrite != true) continue;
                var expectedDate = state.Unlocked ? state.UnlockTimeUtc : null;
                if (Equals(unlocked.GetValue(item), state.Unlocked) && Equals(date.GetValue(item), expectedDate)) continue;
                unlocked.SetValue(item, state.Unlocked);
                date.SetValue(item, expectedDate);
                changed++;
            }
            if (changed == 0) return false;
            refresh.Invoke(viewModel, null);
            // The filter buttons bind to computed availability properties. The
            // item setters/filter refresh do not notify those VM-level bindings.
            var notify = type.GetMethod("OnPropertyChanged", BindingFlags.Public | flags, null, new[] { typeof(string) }, null);
            notify?.Invoke(viewModel, new object[] { "CanSelectUnlocked" });
            notify?.Invoke(viewModel, new object[] { "CanSelectLocked" });
            logger?.Info($"[PS5Progress] Synchronized achievement states from fork cache: matched={states.Count}, changed={changed}.");
            return true;
        }
    }
}
