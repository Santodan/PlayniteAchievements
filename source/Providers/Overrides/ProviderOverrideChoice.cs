namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// A single selectable option for a <see cref="ProviderOverrideValueKind.Choice"/> override.
    /// </summary>
    public sealed class ProviderOverrideChoice
    {
        public ProviderOverrideChoice(string value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        /// <summary>The persisted override value (e.g. an enum name).</summary>
        public string Value { get; }

        /// <summary>The localized label shown to the user.</summary>
        public string DisplayName { get; }
    }
}
