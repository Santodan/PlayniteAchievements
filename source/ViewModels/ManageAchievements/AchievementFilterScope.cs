using System.Collections.Generic;
using Playnite.SDK;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// How far an achievement is hidden. The two stored filter flags are a scale rather than
    /// independent toggles, so the editor presents them as one choice.
    /// </summary>
    public enum AchievementFilterScope
    {
        /// <summary>Shown everywhere.</summary>
        None,

        /// <summary>Still listed, but left out of summary surfaces and their counts.</summary>
        Summary,

        /// <summary>Hidden from achievement views as well as summaries, and from all counts.</summary>
        All,

        /// <summary>
        /// Display only, for a multi-selection whose rows disagree. It is never stored, never
        /// returned for a single achievement, and never offered as a choice: the dropdown lists
        /// only the three real scopes, so a proxy row holding this renders blank and picking any
        /// real scope compares as a change.
        /// </summary>
        Mixed
    }

    /// <summary>
    /// One choice in the editor's filter dropdown, pairing the scope with its localized name.
    /// </summary>
    public sealed class AchievementFilterScopeOption
    {
        public AchievementFilterScopeOption(AchievementFilterScope value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public AchievementFilterScope Value { get; }

        public string DisplayName { get; }
    }

    /// <summary>
    /// The one definition of what a filter scope means, shared by every surface that reads or
    /// writes the two stored flags.
    /// </summary>
    /// <remarks>
    /// The scale lived twice over: once in the editor row that stages a bulk edit, and once in
    /// whatever else wanted to set the same pair. Two copies of a three-way mapping is two chances
    /// for a surface to disagree about what "Summaries" stores, so both now call this.
    /// </remarks>
    public static class AchievementFilterScopes
    {
        /// <summary>
        /// The flags a scope stores. The two are never both set: filtering an achievement out
        /// entirely already removes it from summaries, so carrying both would encode the stronger
        /// state twice and let a later read disagree with itself.
        /// </summary>
        public static void ToFlags(
            AchievementFilterScope scope,
            out bool isFiltered,
            out bool isSummaryFiltered)
        {
            isFiltered = scope == AchievementFilterScope.All;
            isSummaryFiltered = scope == AchievementFilterScope.Summary;
        }

        /// <summary>The scope a single achievement's stored flags represent.</summary>
        public static AchievementFilterScope FromFlags(bool isFiltered, bool isSummaryFiltered)
        {
            if (isFiltered)
            {
                return AchievementFilterScope.All;
            }

            return isSummaryFiltered ? AchievementFilterScope.Summary : AchievementFilterScope.None;
        }

        /// <summary>
        /// The scope a group of achievements agrees on, or <see cref="AchievementFilterScope.Mixed"/>
        /// when they disagree.
        /// </summary>
        /// <param name="total">How many achievements the group holds.</param>
        /// <param name="filteredCount">How many are filtered out entirely.</param>
        /// <param name="summaryEffectiveCount">
        /// How many are kept out of summaries by either flag. A fully filtered achievement counts
        /// here too, matching what the surfaces actually do with the pair.
        /// </param>
        public static AchievementFilterScope FromMemberCounts(
            int total,
            int filteredCount,
            int summaryEffectiveCount)
        {
            if (total <= 0)
            {
                return AchievementFilterScope.None;
            }

            if (filteredCount == total)
            {
                return AchievementFilterScope.All;
            }

            if (summaryEffectiveCount == 0)
            {
                return AchievementFilterScope.None;
            }

            // Every member is out of summaries, but not every member is out of the views as well,
            // so the group agrees on Summaries and nothing stronger.
            return summaryEffectiveCount == total && filteredCount == 0
                ? AchievementFilterScope.Summary
                : AchievementFilterScope.Mixed;
        }

        /// <summary>
        /// Sets one scope on a group of achievements within the two stored sets, leaving every
        /// achievement outside the group where it was.
        /// </summary>
        /// <remarks>
        /// The writer replaces both lists wholesale, so a caller pushing a scope onto part of a
        /// game has to hand it the complete result. Mutating the full sets here is what keeps the
        /// untouched achievements' filters from being dropped by the write that follows.
        /// </remarks>
        public static void Apply(
            ISet<string> filtered,
            ISet<string> summaryFiltered,
            IEnumerable<string> apiNames,
            AchievementFilterScope scope)
        {
            if (filtered == null || summaryFiltered == null || apiNames == null)
            {
                return;
            }

            ToFlags(scope, out var isFiltered, out var isSummaryFiltered);
            foreach (var apiName in apiNames)
            {
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                ApplyFlag(filtered, apiName, isFiltered);
                ApplyFlag(summaryFiltered, apiName, isSummaryFiltered);
            }
        }

        private static void ApplyFlag(ISet<string> set, string apiName, bool isSet)
        {
            if (isSet)
            {
                set.Add(apiName);
            }
            else
            {
                set.Remove(apiName);
            }
        }

        /// <summary>
        /// The three real scopes as dropdown choices. <see cref="AchievementFilterScope.Mixed"/> is
        /// never offered: a control holding it matches no item and renders blank, which is the
        /// display a disagreeing group wants.
        /// </summary>
        public static IReadOnlyList<AchievementFilterScopeOption> CreateOptions()
        {
            return new[]
            {
                new AchievementFilterScopeOption(
                    AchievementFilterScope.None,
                    ResourceProvider.GetString("LOCPlayAch_Common_None")),
                new AchievementFilterScopeOption(
                    AchievementFilterScope.Summary,
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries")),
                new AchievementFilterScopeOption(
                    AchievementFilterScope.All,
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Filters_FilterOut"))
            };
        }
    }
}
