using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    // Shared assembly of a RetroAchievements game's achievement sets (the base/core set plus any
    // subsets) into a single achievement list with consistent category labels and types. Both the
    // current-user scan (RetroAchievementsScanner) and the friends scan
    // (RetroAchievementsFriendsProvider) route through this so the two paths cannot drift: the base
    // set is labeled "Base" and typed "Base"; each subset is labeled with its extracted name (or
    // "Subset") and typed "Subset". Callers vary only in how a set's progress is fetched (current
    // user, a friend, or GetGameExtended), supplied via the fetchSetInfo delegate.
    internal static class RetroAchievementsSetAssembler
    {
        internal sealed class AssembledSets
        {
            public List<AchievementDetail> Achievements { get; } = new List<AchievementDetail>();

            // Per-set (category label -> set info) pairs in scan order, for optional category-art
            // planning. The base set is always first. Friends ignore this.
            public List<(string CategoryLabel, RaGameInfoUserProgress Info)> CategoryImageSources { get; } =
                new List<(string, RaGameInfoUserProgress)>();
        }

        public static async Task<AssembledSets> AssembleAsync(
            RaGameInfoUserProgress baseGameInfo,
            int baseGameId,
            int? subsetConsoleId,
            RetroAchievementsHashIndexStore hashIndexStore,
            RetroAchievementsSettings settings,
            Func<int, CancellationToken, Task<RaGameInfoUserProgress>> fetchSetInfo,
            ILogger logger,
            CancellationToken cancel,
            string logPrefix = "[RA]")
        {
            var result = new AssembledSets();

            var baseAchievements = RetroAchievementsAchievementMapper.ParseAchievements(
                baseGameInfo,
                settings.RaRarityStats,
                categoryLabel: "Base",
                enableAutomaticCapstoneAssignment: settings.EnableAutomaticCapstoneAssignment,
                setCategoryType: "Base");
            result.Achievements.AddRange(baseAchievements);
            result.CategoryImageSources.Add(("Base", baseGameInfo));

            logger?.Info($"{logPrefix} Parsed {baseAchievements.Count} achievements for '{baseGameInfo?.GameTitle}'.");

            if (!settings.EnableRaSubsetScanning)
            {
                return result;
            }

            if (!subsetConsoleId.HasValue)
            {
                logger?.Info($"{logPrefix} Skipping subset lookup for gameId={baseGameId} because no console ID was resolved.");
                return result;
            }

            List<RaSubsetEntry> subsets;
            try
            {
                subsets = await hashIndexStore.GetSubsetsForGameAsync(baseGameId, subsetConsoleId.Value, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"{logPrefix} Failed to look up subsets for gameId={baseGameId}: {ex.Message}");
                return result;
            }

            if (subsets == null || subsets.Count == 0)
            {
                return result;
            }

            foreach (var subset in subsets)
            {
                cancel.ThrowIfCancellationRequested();
                if (subset == null || subset.Id <= 0)
                {
                    continue;
                }

                try
                {
                    var subsetInfo = await fetchSetInfo(subset.Id, cancel).ConfigureAwait(false);
                    var categoryLabel = RetroAchievementsAchievementMapper.ExtractCategoryLabel(subset.Title) ?? "Subset";
                    var subsetAchievements = RetroAchievementsAchievementMapper.ParseAchievements(
                        subsetInfo,
                        settings.RaRarityStats,
                        categoryLabel: categoryLabel,
                        enableAutomaticCapstoneAssignment: settings.EnableAutomaticCapstoneAssignment,
                        setCategoryType: "Subset");

                    result.Achievements.AddRange(subsetAchievements);
                    result.CategoryImageSources.Add((categoryLabel, subsetInfo));

                    logger?.Info($"{logPrefix} Parsed {subsetAchievements.Count} achievements for subset '{subset.Title}' (category={categoryLabel}).");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"{logPrefix} Failed to fetch subset '{subset.Title}' (ID={subset.Id}): {ex.Message}");
                }
            }

            return result;
        }
    }
}
