namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// A user-defined provider that custom-only games can display as. The icon is stored as
    /// WPF path mini-language produced when the user's SVG was imported, so rendering never
    /// needs the original file.
    /// </summary>
    public sealed class CustomProviderDefinition
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string ColorHex { get; set; }

        public string IconPathData { get; set; }

        public string IconSource { get; set; }

        public CustomProviderDefinition Clone()
        {
            return new CustomProviderDefinition
            {
                Id = Id,
                Name = Name,
                ColorHex = ColorHex,
                IconPathData = IconPathData,
                IconSource = IconSource
            };
        }
    }
}
