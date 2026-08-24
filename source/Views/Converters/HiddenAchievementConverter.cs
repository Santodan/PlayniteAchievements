using System;
using System.Globalization;
using System.Windows.Data;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Multi-value converter that returns placeholder content for hidden achievements
    /// when the corresponding show setting is false.
    /// Used for icon, title, and description bindings in theme controls.
    /// </summary>
    public class HiddenAchievementConverter : IMultiValueConverter
    {
        /// <summary>
        /// The type of content being converted.
        /// </summary>
        public enum Mode
        {
            /// <summary>
            /// Icon mode - returns hidden icon pack URI or real icon path.
            /// </summary>
            Icon,
            /// <summary>
            /// Title mode - returns "Hidden Achievement" or real title.
            /// </summary>
            Title,
            /// <summary>
            /// Description mode - returns "Click to reveal" or real description.
            /// </summary>
            Description
        }

        /// <summary>
        /// The conversion mode determining what placeholder to return.
        /// </summary>
        public Mode ConversionMode { get; set; }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // values[0] = actual value (icon path, title, description)
            // values[1] = Hidden (bool)
            // values[2] = Unlocked (bool)
            // values[3] = ShowHidden setting (bool) from control
            // values[4] = IsRevealed (bool) from attached property (optional)

            if (values.Length >= 4 &&
                values[1] is bool hidden &&
                values[2] is bool unlocked &&
                values[3] is bool showSetting)
            {
                // Check if achievement has been locally revealed via click
                bool isRevealed = values.Length >= 5 && values[4] is bool revealed && revealed;

                // Only show placeholder when: hidden AND locked AND setting is false AND not locally revealed
                if (hidden && !unlocked && !showSetting && !isRevealed)
                {
                    return ConversionMode switch
                    {
                        Mode.Icon => AchievementIconResolver.GetHiddenFallbackIcon(),
                        Mode.Title => ResourceProvider.GetString("LOCPlayAch_Achievements_HiddenTitle"),
                        Mode.Description => ResourceProvider.GetString("LOCPlayAch_Achievements_ClickToReveal"),
                        _ => values[0]
                    };
                }
            }

            // Return original value when conditions not met
            return values[0] ?? Binding.DoNothing;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
