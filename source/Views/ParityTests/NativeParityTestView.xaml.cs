using System;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Views.ParityTests
{
    public partial class NativeParityTestView : UserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(NativeParityTestView));

        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly Game _game;

        public string GameName { get; }
        public Guid GameId { get; }
        public PlayniteAchievementsSettings Settings => _plugin.Settings;

        public NativeParityTestView(Game game)
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"InitializeComponent failed: {ex.Message}\n\n{ex.StackTrace}", "NativeParityTestView Error");
                throw;
            }

            try
            {
                _plugin = PlayniteAchievementsPlugin.Instance ?? throw new InvalidOperationException("Plugin instance not available");
                _game = game ?? throw new ArgumentNullException(nameof(game));

                GameName = _game.Name;
                GameId = _game.Id;

                DataContext = this;

                Loaded += NativeParityTestView_Loaded;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Constructor setup failed: {ex.Message}\n\n{ex.StackTrace}", "NativeParityTestView Error");
                throw;
            }
        }

        private void NativeParityTestView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Ensure theme properties are populated for bindings (async + coalesced).
                _plugin.ThemeUpdateService?.RequestUpdate(_game.Id);

                ApplyGameToHosts(this, _game);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Native parity test view failed during Loaded.");
                _plugin?.PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Native parity test view failed to load: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        private static void ApplyGameToHosts(DependencyObject root, Game game)
        {
            if (root == null)
            {
                return;
            }

            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                if (child is GameViewControlHost host)
                {
                    host.Game = game;
                }

                ApplyGameToHosts(child, game);
            }
        }
    }
}
