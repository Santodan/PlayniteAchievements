namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Key/label pair used by fullscreen themes for dynamic list ComboBox bindings.
    /// </summary>
    public sealed class DynamicThemeOption
    {
        public DynamicThemeOption(string key, string label, int count = 0)
        {
            Key = key ?? string.Empty;
            Label = label ?? Key;
            Count = count < 0 ? 0 : count;
        }

        public string Key { get; }

        public string Label { get; }

        public int Count { get; }

        public override string ToString()
        {
            return Label;
        }
    }
}
