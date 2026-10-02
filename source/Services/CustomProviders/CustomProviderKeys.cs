using System;

namespace PlayniteAchievements.Services.CustomProviders
{
    /// <summary>
    /// Key conventions for user-defined custom providers. A custom-only game with no custom
    /// provider assigned keeps the bare <see cref="BaseKey"/>; an assigned game displays as
    /// <c>Custom:&lt;id&gt;</c>, which flows through the same provider name/icon/color chokepoints
    /// as registered providers.
    /// </summary>
    public static class CustomProviderKeys
    {
        public const string BaseKey = "Custom";

        public const string Prefix = "Custom:";

        // Unassigned custom-only games borrow the Manual provider's icon and color.
        public const string FallbackProviderKey = "Manual";

        public const string BaseIconKey = "ProviderIconManual";

        public const string IconKeyPrefix = "ProviderIconCustom:";

        public const string DefaultColorHex = "#FF652C";

        public const int MaxNameLength = 64;

        public static string NormalizeId(string id)
        {
            var normalized = (id ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public static string GenerateId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        public static string Build(string id)
        {
            var normalized = NormalizeId(id);
            return normalized == null ? null : Prefix + normalized;
        }

        public static string BuildIconKey(string id)
        {
            var normalized = NormalizeId(id);
            return normalized == null ? null : IconKeyPrefix + normalized;
        }

        public static bool IsCustomProviderKey(string providerKey)
        {
            return TryGetId(providerKey, out _);
        }

        public static bool IsBaseKey(string providerKey)
        {
            return string.Equals(providerKey?.Trim(), BaseKey, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TryGetId(string providerKey, out string id)
        {
            return TryStripPrefix(providerKey, Prefix, out id);
        }

        public static bool TryGetIdFromIconKey(string iconKey, out string id)
        {
            return TryStripPrefix(iconKey, IconKeyPrefix, out id);
        }

        private static bool TryStripPrefix(string value, string prefix, out string id)
        {
            id = null;
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.Length <= prefix.Length ||
                !trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            id = NormalizeId(trimmed.Substring(prefix.Length));
            return id != null;
        }
    }
}
