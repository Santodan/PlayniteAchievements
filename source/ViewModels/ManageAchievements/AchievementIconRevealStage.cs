namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// How much of a masked achievement's icon the editor is currently showing. The values are a
    /// scale rather than a set, so a row steps through the ones that apply to it and stops at the
    /// art itself.
    /// </summary>
    /// <remarks>
    /// A hidden achievement that is still locked has both masks to step through, and revealing the
    /// hidden one should not also give away that it is locked art underneath; a merely locked
    /// achievement starts at <see cref="Locked"/>, and an unlocked one has nothing to step through
    /// at all. Ordering matters: the stages are compared, so <see cref="Hidden"/> must stay the
    /// most masked and <see cref="Revealed"/> the least.
    /// </remarks>
    public enum AchievementIconRevealStage
    {
        /// <summary>The hidden-achievement placeholder.</summary>
        Hidden,

        /// <summary>The locked placeholder.</summary>
        Locked,

        /// <summary>The achievement's own art.</summary>
        Revealed
    }
}
