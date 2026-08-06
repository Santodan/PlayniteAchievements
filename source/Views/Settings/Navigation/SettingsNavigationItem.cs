using System;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Settings.Navigation
{
    /// <summary>
    /// A left-navigation entry in a settings master-detail tab. Holds display metadata and
    /// creates its detail view lazily on first selection.
    /// </summary>
    public class SettingsNavigationItem : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Func<UserControl> _viewFactory;
        private UserControl _view;
        private string _providerColorHex;

        public SettingsNavigationItem(
            string key,
            string displayName,
            string groupName = null,
            Func<UserControl> viewFactory = null,
            string redirectKey = null,
            string subtitle = null,
            string iconGlyph = null,
            string providerIconKey = null,
            string providerColorHex = null)
        {
            Key = key;
            DisplayName = displayName;
            GroupName = groupName;
            _viewFactory = viewFactory;
            RedirectKey = redirectKey;
            Subtitle = subtitle;
            IconGlyph = iconGlyph;
            ProviderIconKey = providerIconKey;
            _providerColorHex = providerColorHex;
        }

        public string Key { get; }
        public string DisplayName { get; }
        public string GroupName { get; }
        public string Subtitle { get; }
        public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

        /// <summary>Segoe MDL2 glyph shown when the item has no provider icon.</summary>
        public string IconGlyph { get; }
        public bool HasIconGlyph => !string.IsNullOrWhiteSpace(IconGlyph);

        /// <summary>Resource key for a provider icon, rendered via ProviderIconConverter.</summary>
        public string ProviderIconKey { get; }
        public string ProviderColorHex
        {
            get => _providerColorHex;
            private set => SetValue(ref _providerColorHex, value);
        }
        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);

        public void RefreshProviderAppearance()
        {
            if (HasProviderIcon)
            {
                ProviderColorHex = Providers.ProviderRegistry.GetProviderColorHex(Key, _providerColorHex);
            }
        }

        /// <summary>Key of another item this entry forwards selection to instead of showing a view.</summary>
        public string RedirectKey { get; }
        public bool IsRedirect => !string.IsNullOrWhiteSpace(RedirectKey);

        public virtual bool IsEnabled => true;

        public UserControl View => _view;

        /// <summary>
        /// Creates the detail view on first selection. A factory failure yields null (an empty
        /// detail pane) rather than propagating: this runs inside a DependencyProperty change
        /// callback on the dispatcher, where an escaping exception takes Playnite down and loses
        /// access to every other settings page too.
        /// </summary>
        public UserControl EnsureView()
        {
            if (IsRedirect || _view != null)
            {
                return _view;
            }

            UserControl view;
            try
            {
                view = _viewFactory?.Invoke();
            }
            catch (Exception ex)
            {
                Playnite.SDK.LogManager.GetLogger()?.Error(
                    ex, $"Failed creating the settings view for '{Key}'.");
                return null;
            }

            if (view != null)
            {
                _view = view;
                OnPropertyChanged(nameof(View));
            }

            return _view;
        }
    }
}
