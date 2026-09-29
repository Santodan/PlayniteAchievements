namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Which of an achievement's looks the editor is currently drawing for its icon. The values are
    /// a scale rather than a set, so a row steps through the ones that apply to it.
    /// </summary>
    /// <remarks>
    /// Past the spoiler cover the steps are the two ways the achievement's own art is drawn, not
    /// further masks, so an achievement that is still locked can be seen as it will look once it is
    /// not. Ordering matters: the stages are compared, so <see cref="Covered"/> must stay the most
    /// masked and <see cref="Unlocked"/> the least.
    /// </remarks>
    public enum AchievementIconRevealStage
    {
        /// <summary>
        /// The spoiler cover: the hidden-achievement placeholder for a hidden achievement, the
        /// locked placeholder otherwise. Only exists while a display setting is masking the row.
        /// </summary>
        Covered,

        /// <summary>The achievement's own art as it is drawn while locked.</summary>
        Locked,

        /// <summary>The achievement's own art as it is drawn once unlocked.</summary>
        Unlocked
    }
}
