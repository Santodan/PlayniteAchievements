using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels.Settings
{
    /// <summary>
    /// One row of the Progress bar details priority grid in Display settings.
    /// </summary>
    /// <remarks>
    /// The grid writes edits straight to the persisted list; this row only mirrors it.
    /// </remarks>
    public sealed class ProgressDetailRowItem : ObservableObject
    {
        private bool _isEnabled;

        public ProgressDetailRowItem(ProgressDetailStyle style, bool isEnabled)
        {
            Style = style;
            _isEnabled = isEnabled;
        }

        public ProgressDetailStyle Style { get; }

        public string DisplayName => ResourceProvider.GetString(GetNameKey(Style));

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetValue(ref _isEnabled, value);
        }

        private static string GetNameKey(ProgressDetailStyle style)
        {
            switch (style)
            {
                case ProgressDetailStyle.Trophy:
                    return "LOCPlayAch_Column_Trophy";
                case ProgressDetailStyle.Points:
                    return "LOCPlayAch_Column_Points";
                default:
                    return "LOCPlayAch_Column_Rarity";
            }
        }
    }
}
