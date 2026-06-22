namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Key/label pair used by fullscreen themes for dynamic list ComboBox bindings.
    /// </summary>
    public sealed class DynamicThemeOption : System.IEquatable<DynamicThemeOption>
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

        public bool Equals(DynamicThemeOption other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(Key, other.Key, System.StringComparison.Ordinal) &&
                   string.Equals(Label, other.Label, System.StringComparison.Ordinal) &&
                   Count == other.Count;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as DynamicThemeOption);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + System.StringComparer.Ordinal.GetHashCode(Key ?? string.Empty);
                hash = (hash * 31) + System.StringComparer.Ordinal.GetHashCode(Label ?? string.Empty);
                hash = (hash * 31) + Count.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            return Label;
        }
    }
}
