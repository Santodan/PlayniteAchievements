namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// The achievements carrying one tag: how many there are and how many are unlocked, with the
    /// tag's name, color and icon as the user set them.
    /// </summary>
    public class AchievementTagStats
    {
        /// <summary>The tag's stored key.</summary>
        public string Key { get; set; }

        /// <summary>The tag's display name.</summary>
        public string Name { get; set; }

        /// <summary>The tag's color as "#RRGGBB"; null for a tag without one.</summary>
        public string Color { get; set; }

        public bool HasColor => !string.IsNullOrEmpty(Color);

        /// <summary>The tag's icon as a glyph of the plugin's icon font; null for a tag without one.</summary>
        public string Icon { get; set; }

        public bool HasIcon => !string.IsNullOrEmpty(Icon);

        public int Total { get; set; }

        public int Unlocked { get; set; }

        public int Locked { get; set; }

        /// <summary>String representation of the statistics (e.g., "5 / 10").</summary>
        public string Stats => $"{Unlocked} / {Total}";
    }
}
