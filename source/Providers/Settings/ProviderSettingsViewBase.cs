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

        public static readonly DependencyProperty IsAuthStatusCheckingProperty =
            DependencyProperty.Register(
                nameof(IsAuthStatusChecking),
                typeof(bool),
                typeof(ProviderSettingsViewBase),
                new PropertyMetadata(false));

        public bool IsAuthStatusChecking
        {
            get => (bool)GetValue(IsAuthStatusCheckingProperty);
            set => SetValue(IsAuthStatusCheckingProperty, value);
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
                IsAuthStatusChecking = false;
                return;
            }

            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                IsAuthStatusPending = pending;
                IsAuthStatusSuccess = success;
                IsAuthStatusChecking = false;
            }));
        }

        /// <summary>
        /// Marks an auth check as in progress. Any subsequent
        /// <see cref="SetAuthStatusVisualState"/> call clears the checking state.
        /// </summary>
        protected void SetAuthStatusChecking()
        {
            if (Dispatcher.CheckAccess())
            {
                IsAuthStatusChecking = true;
                return;
            }

            Dispatcher.BeginInvoke(new System.Action(() => IsAuthStatusChecking = true));
        }
    }
}
