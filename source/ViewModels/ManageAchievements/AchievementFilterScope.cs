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
        /// The rows in a multi-row selection do not agree. Display only: it is never stored, never
        /// returned for a single achievement, and picking it applies nothing. It exists so that
        /// "they disagree" is a distinct choice from None, which is itself a real setting -- without
        /// it a mixed selection shows None already and choosing None raises no change at all.
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
}
