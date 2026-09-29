using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The capstone and goal toggle decisions, shared by the achievement row context menu and the
    /// theme-bindable toggle commands so the two surfaces cannot drift. Holds the decision and the
    /// write only; callers own their own UI feedback (row re-stamping, re-sorting, error dialogs).
    /// </summary>
    public sealed class AchievementMarkerToggle
    {
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly Func<PersistedSettings> _resolveSettings;
        private readonly Func<GameCustomDataStore> _resolveStore;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;

        public AchievementMarkerToggle(
            AchievementOverridesService achievementOverridesService,
            Func<PersistedSettings> resolveSettings,
            Func<GameCustomDataStore> resolveStore,
            Func<Guid, GameAchievementData> resolveGameData = null)
        {
            _achievementOverridesService = achievementOverridesService
                ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _resolveSettings = resolveSettings ?? throw new ArgumentNullException(nameof(resolveSettings));
            _resolveStore = resolveStore ?? throw new ArgumentNullException(nameof(resolveStore));
            _resolveGameData = resolveGameData;
        }

        /// <summary>What clicking the capstone entry would do to this achievement.</summary>
        public enum CapstoneAction
        {
            Add,

            /// <summary>Another achievement already stands for this one's category.</summary>
            Replace,

            Remove
        }

        /// <summary>
        /// Whether setting this achievement as a capstone would add one or displace the one already
        /// standing for its category, so a surface can say which before the click.
        /// </summary>
        /// <param name="displacedDisplayName">
        /// The capstone that would be displaced, when the action is <see cref="CapstoneAction.Replace"/>.
        /// </param>
        public CapstoneAction ResolveCapstoneAction(
            AchievementMarkerTarget target,
            out string displacedDisplayName)
        {
            displacedDisplayName = null;
            if (!target.IsValid)
            {
                return CapstoneAction.Add;
            }

            if (IsEffectiveCapstone(target))
            {
                return CapstoneAction.Remove;
            }

            // Without the game's achievements there is no way to tell which category this one sits
            // in, so the honest answer is the one that promises least.
            var achievements = _resolveGameData?.Invoke(target.GameId)?.Achievements;
            if (achievements == null || achievements.Count == 0)
            {
                return CapstoneAction.Add;
            }

            var capstones = GameCustomDataLookup.GetCapstoneSet(
                target.GameId,
                _resolveSettings(),
                _resolveStore());
            var resolver = CapstoneResolver.Resolve(
                achievements,
                capstones.Assignments,
                capstones.Materialized);

            var category = achievements
                .FirstOrDefault(a => a != null && Matches(a.ApiName, target.ApiName))
                ?.Category;

            // Its own category only. A capstone inherited from an ancestor is not displaced by
            // nominating one here, so calling that a replacement would promise the wrong thing.
            if (!resolver.HasOwnCapstone(category))
            {
                return CapstoneAction.Add;
            }

            var standing = resolver.ResolveForCategory(category);
            if (string.IsNullOrWhiteSpace(standing))
            {
                return CapstoneAction.Add;
            }

            displacedDisplayName = achievements
                .FirstOrDefault(a => a != null && Matches(a.ApiName, standing))
                ?.DisplayName;
            return CapstoneAction.Replace;
        }

        /// <summary>
        /// Re-stamps a game's rows from its stored capstone set, so a surface can settle after a
        /// toggle without waiting for a reload.
        /// </summary>
        /// <remarks>
        /// Deterministic on purpose. A capstone write is followed by a debounced invalidation
        /// elsewhere, and a surface that re-read on its own schedule would show the state from
        /// before the write on one click and catch up on the next.
        ///
        /// Only a materialized game can be stamped this way: an untouched one still takes its
        /// capstones from provider flags, which only hydration knows.
        /// </remarks>
        /// <returns>False when the rows could not be settled and the caller must reload.</returns>
        public bool TryRestampCapstones(Guid gameId, IEnumerable<AchievementDisplayItem> items)
        {
            if (gameId == Guid.Empty || items == null)
            {
                return false;
            }

            var capstones = GameCustomDataLookup.GetCapstoneSet(gameId, _resolveSettings(), _resolveStore());
            if (!capstones.Materialized)
            {
                return false;
            }

            var effective = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in capstones.Assignments ?? new List<CapstoneAssignment>())
            {
                var apiName = (assignment?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    effective.Add(apiName);
                }
            }

            foreach (var item in items)
            {
                if (item != null)
                {
                    item.IsCapstone = effective.Contains((item.ApiName ?? string.Empty).Trim());
                }
            }

            return true;
        }

        private static bool Matches(string left, string right)
        {
            return string.Equals(
                (left ?? string.Empty).Trim(),
                (right ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether this achievement is a capstone right now: either hydration flagged it, which
        /// covers the provider seed, or the game's stored set names it.
        /// </summary>
        public bool IsEffectiveCapstone(AchievementMarkerTarget target)
        {
            if (!target.IsValid)
            {
                return false;
            }

            var capstones = GameCustomDataLookup.GetCapstoneSet(
                target.GameId,
                _resolveSettings(),
                _resolveStore());

            if (capstones.Materialized)
            {
                return capstones.Assignments?.Any(capstone => capstone.Matches(target.ApiName)) == true;
            }

            return target.IsCapstone;
        }

        /// <summary>
        /// Adds this achievement to the game's capstones, or drops it when it is already one.
        /// </summary>
        /// <remarks>
        /// A capstone belongs to its achievement's category, so adding one displaces whatever stood
        /// for that category before. The editor spells that out on its button; this toggle cannot,
        /// so the row menu labels itself instead.
        /// </remarks>
        public async Task<CapstoneToggleResult> ToggleCapstoneAsync(AchievementMarkerTarget target)
        {
            if (!target.IsValid)
            {
                return CapstoneToggleResult.Skipped();
            }

            var isCapstone = !IsEffectiveCapstone(target);
            var result = await _achievementOverridesService.SetCapstoneAsync(
                target.GameId,
                target.ApiName,
                isCapstone);
            return result.Success
                ? CapstoneToggleResult.Wrote(isCapstone ? target.ApiName : null)
                : CapstoneToggleResult.Failed(result.ErrorMessage);
        }

        /// <summary>
        /// Adds this achievement to the game's goal list, or removes it when it is already a goal.
        /// Unlocked achievements are ignored: unlocking retires a goal, so toggling one would be a
        /// dead end.
        /// </summary>
        public GoalToggleResult ToggleGoal(AchievementMarkerTarget target)
        {
            if (!target.IsValid || target.Unlocked)
            {
                return GoalToggleResult.Skipped();
            }

            var goalIndex = _achievementOverridesService.SetAchievementGoal(
                target.GameId,
                target.ApiName,
                !target.IsGoal);

            return GoalToggleResult.Wrote(goalIndex);
        }

        public struct CapstoneToggleResult
        {
            private CapstoneToggleResult(bool attempted, bool success, string capstoneApiName, string errorMessage)
            {
                Attempted = attempted;
                Success = success;
                CapstoneApiName = capstoneApiName;
                ErrorMessage = errorMessage;
            }

            /// <summary>False when the target was unusable and no write was attempted.</summary>
            public bool Attempted { get; }

            public bool Success { get; }

            /// <summary>The capstone that was written; null means the capstone was cleared.</summary>
            public string CapstoneApiName { get; }

            public string ErrorMessage { get; }

            /// <summary>
            /// True when a capstone was added rather than dropped. Rows cannot be re-stamped from
            /// this alone: a game carries several capstones, so the write says nothing about the
            /// other rows, and only the resolver knows what a category now stands on.
            /// </summary>
            public bool WasSet => Attempted && Success && CapstoneApiName != null;

            internal static CapstoneToggleResult Skipped() =>
                new CapstoneToggleResult(false, false, null, null);

            internal static CapstoneToggleResult Wrote(string capstoneApiName) =>
                new CapstoneToggleResult(true, true, capstoneApiName, null);

            internal static CapstoneToggleResult Failed(string errorMessage) =>
                new CapstoneToggleResult(true, false, null, errorMessage);
        }

        public struct GoalToggleResult
        {
            private GoalToggleResult(bool attempted, int goalOrderIndex)
            {
                Attempted = attempted;
                GoalOrderIndex = goalOrderIndex;
            }

            /// <summary>False when the target was unusable or unlocked and no write was attempted.</summary>
            public bool Attempted { get; }

            /// <summary>
            /// The achievement's position in the goal list after the write, or
            /// <see cref="int.MaxValue"/> when it is no longer a goal.
            /// </summary>
            public int GoalOrderIndex { get; }

            public bool IsGoal => Attempted && GoalOrderIndex != int.MaxValue;

            internal static GoalToggleResult Skipped() => new GoalToggleResult(false, int.MaxValue);

            internal static GoalToggleResult Wrote(int goalIndex) =>
                new GoalToggleResult(true, goalIndex >= 0 ? goalIndex : int.MaxValue);
        }
    }
}
