namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Whose unlock state an achievement's unlock state is. Kept pure and separate from the menus
    /// and services that apply it, because it is the guard that keeps a real provider's unlocked
    /// counts and completion out of reach of a context menu.
    /// </summary>
    public static class UserOwnedUnlockRules
    {
        /// <summary>
        /// True when the user may mark this achievement unlocked.
        /// </summary>
        /// <remarks>
        /// Two kinds qualify: one the user authored, which has no provider behind it, and any
        /// achievement of a manually tracked game, where recording unlocks by hand is the entire
        /// feature. Everything else is provider-owned, where an edit would move unlocked counts and
        /// completion and read as a real unlock to the in-game monitor. An already-unlocked
        /// achievement is excluded because there is nothing to do.
        /// </remarks>
        public static bool CanUserUnlock(bool isUnlocked, bool isCustomAchievement, bool gameHasManualLink)
        {
            if (isUnlocked)
            {
                return false;
            }

            return isCustomAchievement || gameHasManualLink;
        }
    }
}
