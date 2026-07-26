namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Reusable override value validators shared by providers whose override is a free-text
    /// identifier with no provider-specific format (e.g. Epic/GOG/EA store IDs).
    /// </summary>
    public static class ProviderOverrideValidators
    {
        public const string RequiredValueErrorKey = "LOCPlayAch_ManageAchievements_Overrides_ProviderValueRequired";
        /// <summary>Accepts any non-empty trimmed value; rejects empty input.</summary>
        public static ProviderOverrideValidation RequiredText(string rawValue)
        {
            var trimmed = (rawValue ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(trimmed)
                ? ProviderOverrideValidation.Invalid(RequiredValueErrorKey)
                : ProviderOverrideValidation.Valid(trimmed);
        }
    }
}
