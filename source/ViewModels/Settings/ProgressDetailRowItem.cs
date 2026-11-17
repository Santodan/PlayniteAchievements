using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.ViewModels.Settings
{
    /// <summary>
    /// One row of the Achievement styles priority grid in General settings.
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

        /// <summary>Rarity is always on; only Trophy and Points can be turned off.</summary>
        public bool CanToggle => Style != ProgressDetailStyle.Rarity;

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
