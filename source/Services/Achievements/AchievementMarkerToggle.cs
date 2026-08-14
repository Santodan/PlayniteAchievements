using System;
using System.Threading.Tasks;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

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

        public AchievementMarkerToggle(
            AchievementOverridesService achievementOverridesService,
            Func<PersistedSettings> resolveSettings,
            Func<GameCustomDataStore> resolveStore)
        {
            _achievementOverridesService = achievementOverridesService
                ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _resolveSettings = resolveSettings ?? throw new ArgumentNullException(nameof(resolveSettings));
            _resolveStore = resolveStore ?? throw new ArgumentNullException(nameof(resolveStore));
        }

        /// <summary>
        /// Whether this achievement is the capstone the user would see marked right now: either
        /// hydration flagged it (which also covers provider-assigned capstones) or the stored
        /// manual capstone points at it.
        /// </summary>
        public bool IsEffectiveCapstone(AchievementMarkerTarget target)
        {
            if (!target.IsValid)
            {
                return false;
            }

            var manualCapstone = GameCustomDataLookup.GetManualCapstone(
                target.GameId,
                _resolveSettings(),
                _resolveStore());

            return target.IsCapstone ||
                string.Equals(manualCapstone, target.ApiName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets this achievement as the game's manual capstone, or clears the manual capstone when
        /// it is already the effective one.
        /// </summary>
        public async Task<CapstoneToggleResult> ToggleCapstoneAsync(AchievementMarkerTarget target)
        {
            if (!target.IsValid)
            {
                return CapstoneToggleResult.Skipped();
            }

            var nextCapstone = IsEffectiveCapstone(target) ? null : target.ApiName;
            var result = await _achievementOverridesService.SetCapstoneAsync(target.GameId, nextCapstone);
            return result.Success
                ? CapstoneToggleResult.Wrote(nextCapstone)
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
            /// True when a capstone was set. Callers may re-stamp rows in place for this case,
            /// because setting one makes every other row a non-capstone, exactly as hydration
            /// would. Clearing one lets provider-assigned capstones reappear, and only hydration
            /// knows those, so that case needs a full reload.
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
