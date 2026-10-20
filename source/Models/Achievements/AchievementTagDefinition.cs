namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// One achievement tag. Achievements reference tags by <see cref="Key"/>, so renaming or
    /// recoloring a tag rewrites only this record.
    /// </summary>
    public sealed class AchievementTagDefinition
    {
        /// <summary>
        /// Stable identifier. Built-in tags use their canonical name ("Missable"); user tags use a
        /// generated "u-" key.
        /// </summary>
        public string Key { get; set; }

        /// <summary>User-chosen name. Null for a built-in tag shows its localized default name.</summary>
        public string Name { get; set; }

        /// <summary>#RRGGBB, or null for the theme's default tag color.</summary>
        public string Color { get; set; }

        public AchievementTagDefinition Clone()
        {
            return new AchievementTagDefinition
            {
                Key = Key,
                Name = Name,
                Color = Color
            };
        }
    }
}
