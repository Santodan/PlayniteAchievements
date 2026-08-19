using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The standard provider auth card - status glyph, status line, and an action row - as a control
    /// that carries its own state.
    ///
    /// The shared auth styles in ProviderSettingsStyles.xaml read their state from the nearest
    /// UserControl ancestor, which normally is the provider settings page itself. That works while a
    /// page has exactly one session, but a page with two (Battle.net has Blizzard's OAuth session and
    /// the separate Data for Azeroth site check) would have both cards showing whichever state the
    /// page last set. Declaring the same property names here gives every card its own state while
    /// reusing the existing styles verbatim, so a second card is identical to the first by
    /// construction rather than by a copied style.
    /// </summary>
    public partial class AuthStatusCard : UserControl
    {
        public AuthStatusCard()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(AuthStatusCard), new PropertyMetadata(string.Empty));

        /// <summary>The status line shown beside the glyph.</summary>
        public string AuthStatus
        {
            get => (string)GetValue(AuthStatusProperty);
            set => SetValue(AuthStatusProperty, value);
        }

        public static readonly DependencyProperty IsAuthStatusPendingProperty =
            DependencyProperty.Register(nameof(IsAuthStatusPending), typeof(bool), typeof(AuthStatusCard), new PropertyMetadata(false));

        /// <summary>Nothing has been checked yet, so neither success nor failure is claimed.</summary>
        public bool IsAuthStatusPending
        {
            get => (bool)GetValue(IsAuthStatusPendingProperty);
            set => SetValue(IsAuthStatusPendingProperty, value);
        }

        public static readonly DependencyProperty IsAuthStatusSuccessProperty =
            DependencyProperty.Register(nameof(IsAuthStatusSuccess), typeof(bool), typeof(AuthStatusCard), new PropertyMetadata(false));

        public bool IsAuthStatusSuccess
        {
            get => (bool)GetValue(IsAuthStatusSuccessProperty);
            set => SetValue(IsAuthStatusSuccessProperty, value);
        }

        public static readonly DependencyProperty IsAuthStatusCheckingProperty =
            DependencyProperty.Register(nameof(IsAuthStatusChecking), typeof(bool), typeof(AuthStatusCard), new PropertyMetadata(false));

        public bool IsAuthStatusChecking
        {
            get => (bool)GetValue(IsAuthStatusCheckingProperty);
            set => SetValue(IsAuthStatusCheckingProperty, value);
        }

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(AuthStatusCard), new PropertyMetadata(false));

        /// <summary>Disables the action buttons supplied as Content while an operation runs.</summary>
        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }
    }
}
