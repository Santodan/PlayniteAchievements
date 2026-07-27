using System;

namespace PlayniteAchievements.Services.Search
{
    public readonly struct SearchQuery
    {
        private SearchQuery(string text)
        {
            Text = text;
        }

        public string Text { get; }

        public bool HasValue => !string.IsNullOrEmpty(Text);

        public static SearchQuery From(string value)
        {
            var normalized = value?.Trim();
            return new SearchQuery(string.IsNullOrEmpty(normalized) ? string.Empty : normalized);
        }

        public bool Matches(string value)
        {
            if (!HasValue)
            {
                return true;
            }

            return value?.IndexOf(Text, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
