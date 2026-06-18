using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Providers.Settings
{
    /// <summary>
    /// Base UserControl for provider settings views.
    /// </summary>
    public abstract class ProviderSettingsViewBase : UserControl
    {
        public static readonly DependencyProperty IsAuthStatusPendingProperty =
            DependencyProperty.Register(
                nameof(IsAuthStatusPending),
                typeof(bool),
                typeof(ProviderSettingsViewBase),
                new PropertyMetadata(false));

        public bool IsAuthStatusPending
        {
            get => (bool)GetValue(IsAuthStatusPendingProperty);
            set => SetValue(IsAuthStatusPendingProperty, value);
        }

        public static readonly DependencyProperty IsAuthStatusSuccessProperty =
            DependencyProperty.Register(
                nameof(IsAuthStatusSuccess),
                typeof(bool),
                typeof(ProviderSettingsViewBase),
                new PropertyMetadata(false));

        public bool IsAuthStatusSuccess
        {
            get => (bool)GetValue(IsAuthStatusSuccessProperty);
            set => SetValue(IsAuthStatusSuccessProperty, value);
        }

        private IProviderSettings _settings;

        /// <summary>
        /// Gets the settings bound to this view.
        /// </summary>
        public IProviderSettings Settings => _settings;

        /// <inheritdoc />
        public virtual void Initialize(IProviderSettings settings)
        {
            _settings = settings;
            DataContext = this;
        }

        protected void SetAuthStatusVisualState(bool pending, bool success)
        {
            if (Dispatcher.CheckAccess())
            {
                IsAuthStatusPending = pending;
                IsAuthStatusSuccess = success;
                return;
            }

            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                IsAuthStatusPending = pending;
                IsAuthStatusSuccess = success;
            }));
        }
    }
}
