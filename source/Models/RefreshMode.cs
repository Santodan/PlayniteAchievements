using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Refresh mode types for achievement refreshing operations.
    /// </summary>
    public enum RefreshModeType
    {
        Recent,
        Full,
        Installed,
        Favorites,
        Single,
        LibrarySelected,
        Missing,
        Custom
    }

    /// <summary>
    /// Extension methods for RefreshModeType enum.
    /// </summary>
    public static class RefreshModeExtensions
    {
        /// <summary>
        /// Gets the localization resource key for the full display name.
        /// </summary>
        public static string GetResourceKey(this RefreshModeType mode)
        {
            switch (mode)
            {
                case RefreshModeType.LibrarySelected:
                    return "LOCPlayAch_RefreshMode_Selected";
                default:
                    return $"LOCPlayAch_RefreshMode_{mode}";
            }
        }

        /// <summary>
        /// Gets the localization resource key for the short display name.
        /// </summary>
        public static string GetShortResourceKey(this RefreshModeType mode)
        {
            switch (mode)
            {
                case RefreshModeType.LibrarySelected:
                    return "LOCPlayAch_RefreshModeShort_Selected";
                default:
                    return $"LOCPlayAch_RefreshModeShort_{mode}";
            }
        }

        /// <summary>
        /// Gets the string key for this refresh mode.
        /// </summary>
        public static string GetKey(this RefreshModeType mode) => mode.ToString();
    }

    /// <summary>
    /// Represents a refresh mode for achievement refreshing operations.
    /// </summary>
    public class RefreshMode
    {
        /// <summary>
        /// Unique identifier for this refresh mode.
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// The refresh mode type enum value.
        /// </summary>
        public RefreshModeType Type { get; set; }

        /// <summary>
        /// Localized display name for this refresh mode (long version for menus).
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// Localized short display name for this refresh mode (for overview dropdown).
        /// </summary>
        public string ShortDisplayName { get; set; }

        /// <summary>
        /// Description of what this refresh mode does.
        /// </summary>
        public string Description { get; set; }

        public RefreshMode(RefreshModeType type, string displayNameResourceKey, string shortDisplayNameResourceKey)
        {
            Type = type;
            Key = type.GetKey();
            DisplayNameResourceKey = displayNameResourceKey;
            ShortDisplayNameResourceKey = shortDisplayNameResourceKey;
        }

        /// <summary>
        /// Resource key for localized display name (long version).
        /// </summary>
        public string DisplayNameResourceKey { get; set; }

        /// <summary>
        /// Resource key for localized short display name (for overview).
        /// </summary>
        public string ShortDisplayNameResourceKey { get; set; }
    }
}
