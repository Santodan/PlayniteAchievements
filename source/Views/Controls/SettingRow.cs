using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// One settings row: a description on the left, the control that edits the setting on the
    /// right, and an optional hint carried by a hover/focus (i) glyph beside the description
    /// instead of a second line of muted text under the control.
    /// </summary>
    /// <remarks>
    /// The right column participates in a shared size group, so every row inside one
    /// <c>Grid.IsSharedSizeScope</c> card agrees on where the control column starts. The template
    /// lives in CommonResources.xaml, which both the settings and provider settings scoped
    /// dictionaries merge.
    /// </remarks>
    public class SettingRow : ContentControl
    {
        static SettingRow()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(SettingRow),
                new FrameworkPropertyMetadata(typeof(SettingRow)));
        }

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(
                nameof(Label),
                typeof(string),
                typeof(SettingRow),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// The setting's description, shown in the left column.
        /// </summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(
                nameof(Hint),
                typeof(string),
                typeof(SettingRow),
                new PropertyMetadata(string.Empty));

        /// <summary>
        /// Explanatory text for the setting. The (i) glyph appears only when this is non-blank.
        /// </summary>
        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }
    }
}
