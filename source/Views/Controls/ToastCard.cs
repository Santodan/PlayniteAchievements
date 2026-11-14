using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A notification card around its content: the rays around the card, its glow or shadow, the
    /// surface that casts it, and the user's background image or live blur, marked with
    /// <see cref="Helpers.ToastLayer"/> roles so the notification service can rest the card as
    /// separate layers. Its template is in Themes/Generic.xaml and binds to the
    /// <see cref="ViewModels.AchievementToastViewModel"/> it inherits as DataContext.
    ///
    /// A notification template writes only <c>&lt;controls:ToastCard&gt;content&lt;/controls:ToastCard&gt;</c>.
    /// Background, BorderBrush and BorderThickness shape the surface shown where no background
    /// image or blur covers it.
    /// </summary>
    public class ToastCard : ContentControl
    {
        static ToastCard()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(ToastCard),
                new FrameworkPropertyMetadata(typeof(ToastCard)));
        }

        /// <summary>The radius of the card's corners: its surface, background and content clip.</summary>
        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CornerRadius), typeof(CornerRadius), typeof(ToastCard),
                new FrameworkPropertyMetadata(new CornerRadius(8)));

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }
    }
}
