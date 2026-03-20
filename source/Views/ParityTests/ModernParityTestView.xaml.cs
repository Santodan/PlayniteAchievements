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
    public partial class ModernParityTestView : UserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(ModernParityTestView));

        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly Game _game;

        public string GameName { get; }
        public Guid GameId { get; }
        public PlayniteAchievementsSettings Settings => _plugin.Settings;

        public ModernParityTestView(Game game)
        {
            try
            {
                InitializeComponent();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"InitializeComponent failed: {ex.Message}\n\n{ex.StackTrace}", "ModernParityTestView Error");
                throw;
            }

            try
            {
                _plugin = PlayniteAchievementsPlugin.Instance ?? throw new InvalidOperationException("Plugin instance not available");
                _game = game ?? throw new ArgumentNullException(nameof(game));

                GameName = _game.Name;
                GameId = _game.Id;

                DataContext = this;

                Loaded += ModernParityTestView_Loaded;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Constructor setup failed: {ex.Message}\n\n{ex.StackTrace}", "ModernParityTestView Error");
                throw;
            }
        }

        private void ModernParityTestView_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Ensure theme properties are populated for bindings (async + coalesced).
                _plugin.ThemeUpdateService?.RequestUpdate(_game.Id);

                ApplyGameToHosts(this, _game);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Modern parity test view failed during Loaded.");
                _plugin?.PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Modern parity test view failed to load: {ex.Message}",
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

