using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Result of checking one configured provider path.
    /// </summary>
    public sealed class ProviderPathValidation
    {
        private ProviderPathValidation(bool isValid, string messageKey)
        {
            IsValid = isValid;
            MessageKey = messageKey;
        }

        public bool IsValid { get; }

        /// <summary>
        /// Localization key describing why the path is not usable; null when valid.
        /// </summary>
        public string MessageKey { get; }

        public static ProviderPathValidation Valid { get; } = new ProviderPathValidation(true, null);

        public static ProviderPathValidation Invalid(string messageKey) => new ProviderPathValidation(false, messageKey);
    }

    /// <summary>
    /// Helpers for provider settings that hold an ordered list of install or data paths.
    /// </summary>
    public static class ProviderPathList
    {
        /// <summary>
        /// Trims whitespace and quotes, drops blank entries, and removes case-insensitive
        /// duplicates while keeping the first occurrence's position.
        /// </summary>
        public static List<string> Normalize(IEnumerable<string> paths)
        {
            var result = new List<string>();
            if (paths == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                var path = (raw ?? string.Empty).Trim().Trim('"').Trim();
                if (path.Length > 0 && seen.Add(path.TrimEnd('\\', '/')))
                {
                    result.Add(path);
                }
            }

            return result;
        }

        /// <summary>
        /// The list a legacy single-path setting maps to: one entry, or none when blank.
        /// </summary>
        public static List<string> FromLegacy(string legacyPath)
        {
            return Normalize(new[] { legacyPath });
        }
    }
}
