using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>What automatic capstone generation should do with one game.</summary>
    public enum AutoCapstoneGenerationAction
    {
        /// <summary>Already handled; nothing to do, now or later.</summary>
        Skip,

        /// <summary>Not enough known yet; look again after a later refresh.</summary>
        Wait,

        /// <summary>The game already has what it needs; record it as handled and stop.</summary>
        MarkHandled,

        /// <summary>Nominate the game's own platinum as its capstone.</summary>
        NominatePlatinum,

        /// <summary>Author an auto capstone for the whole game.</summary>
        Author
    }

    public sealed class AutoCapstoneGenerationDecision
    {
        public AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction action, AchievementDetail platinum = null)
        {
            Action = action;
            Platinum = platinum;
        }

        public AutoCapstoneGenerationAction Action { get; }

        /// <summary>The platinum to nominate, for <see cref="AutoCapstoneGenerationAction.NominatePlatinum"/>.</summary>
        public AchievementDetail Platinum { get; }
    }

    /// <summary>
    /// Decides what automatic capstone generation does with a game, apart from the store and the
    /// refresh so the rules can be read and tested on their own.
    /// </summary>
    public static class AutoCapstoneEligibility
    {
        /// <param name="alreadyHandled">The game's generation marker.</param>
        /// <param name="hasAutoCapstone">Whether one of its authored achievements is already an auto capstone.</param>
        /// <param name="achievementsInOrder">
        /// Its hydrated achievements, authored ones included, with capstones resolved and in the
        /// order the game shows them.
        /// </param>
        public static AutoCapstoneGenerationDecision Decide(
            bool alreadyHandled,
            bool hasAutoCapstone,
            IReadOnlyList<AchievementDetail> achievementsInOrder)
        {
            if (alreadyHandled)
            {
                return new AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction.Skip);
            }

            var achievements = (achievementsInOrder ?? Array.Empty<AchievementDetail>())
                .Where(achievement => achievement != null)
                .ToList();

            // Nothing a provider supplied means a game not scanned yet, or one whose scan found
            // nothing. A capstone authored now would stand for nothing and freeze out whatever
            // platinum the first real scan brings, so the game waits for one.
            if (!achievements.Any(achievement => !achievement.IsCustom))
            {
                return new AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction.Wait);
            }

            // Filtered or not: a capstone the user set aside is still one they have, and giving the
            // game a second would undo setting it aside.
            if (hasAutoCapstone || achievements.Any(achievement => achievement.IsCapstone))
            {
                return new AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction.MarkHandled);
            }

            var platinum = AutoCapstoneTemplate.SelectPlatinum(
                achievements,
                achievement => achievement.TrophyType,
                achievement => achievement.CategoryType);
            return platinum != null
                ? new AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction.NominatePlatinum, platinum)
                : new AutoCapstoneGenerationDecision(AutoCapstoneGenerationAction.Author);
        }
    }
}
