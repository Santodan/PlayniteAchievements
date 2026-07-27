namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Result of validating and normalizing a raw override value entered by the user.
    /// </summary>
    public sealed class ProviderOverrideValidation
    {
        private ProviderOverrideValidation(
            bool isValid,
            string normalizedValue,
            string errorMessageKey)
        {
            IsValid = isValid;
            NormalizedValue = normalizedValue;
            ErrorMessageKey = errorMessageKey;
        }

        public bool IsValid { get; }

        /// <summary>The normalized value to persist when <see cref="IsValid"/> is true (may be null).</summary>
        public string NormalizedValue { get; }

        /// <summary>Localization key for the validation error when <see cref="IsValid"/> is false.</summary>
        public string ErrorMessageKey { get; }

        public static ProviderOverrideValidation Valid(string normalizedValue)
            => new ProviderOverrideValidation(true, normalizedValue, null);

        public static ProviderOverrideValidation Invalid(string errorMessageKey)
            => new ProviderOverrideValidation(false, null, errorMessageKey);
    }
}
