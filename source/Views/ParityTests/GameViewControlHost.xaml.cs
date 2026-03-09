using System;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Services.Logging;
using Playnite.SDK;

namespace PlayniteAchievements.Views.ParityTests
{
    public partial class GameViewControlHost : UserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(GameViewControlHost));

        public static readonly DependencyProperty ControlNameProperty = DependencyProperty.Register(
            nameof(ControlName),
            typeof(string),
            typeof(GameViewControlHost),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnControlNameChanged));

        public static readonly DependencyProperty GameProperty = DependencyProperty.Register(
            nameof(Game),
            typeof(Game),
            typeof(GameViewControlHost),
            new FrameworkPropertyMetadata(null, OnGameChanged));

        private Control _createdControl;

        public string ControlName
        {
            get => (string)GetValue(ControlNameProperty);
            set => SetValue(ControlNameProperty, value);
        }

        public Game Game
        {
            get => (Game)GetValue(GameProperty);
            set => SetValue(GameProperty, value);
        }

        public GameViewControlHost()
        {
            InitializeComponent();
            Loaded += GameViewControlHost_Loaded;
        }

        private void SetEmpty(string message)
        {
            _createdControl = null;
            PART_Content.Content = null;
            PART_EmptyText.Text = string.IsNullOrWhiteSpace(message)
                ? "Control not available."
                : message;
            PART_Empty.Visibility = Visibility.Visible;
        }

        private void GameViewControlHost_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureControlCreated();
            ApplyGameContextIfPossible();
        }

        private static void OnControlNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GameViewControlHost host)
            {
                host.EnsureControlCreated(forceRecreate: true);
                host.ApplyGameContextIfPossible();
            }
        }

        private static void OnGameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GameViewControlHost host)
            {
                host.ApplyGameContextIfPossible();
            }
        }

        private void EnsureControlCreated(bool forceRecreate = false)
        {
            if (!IsLoaded)
            {
                return;
            }

            if (!forceRecreate && _createdControl != null)
            {
                return;
            }

            _createdControl = null;
            PART_Content.Content = null;

            var name = ControlName;
            if (string.IsNullOrWhiteSpace(name))
            {
                SetEmpty("ControlName is empty.");
                return;
            }

            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin == null)
            {
                SetEmpty("Plugin instance not available.");
                return;
            }

            try
            {
                var control = plugin.GetGameViewControl(new GetGameViewControlArgs { Name = name });
                if (control == null)
                {
                    SetEmpty("Control not available (returned null).");
                    return;
                }

                // Defensive: WPF controls can't have two parents.
                if (control is FrameworkElement fe && fe.Parent != null)
                {
                    SetEmpty($"Control '{name}' instance already has a parent. The factory must return a new instance per request.");
                    return;
                }

                _createdControl = control;
                PART_Empty.Visibility = Visibility.Collapsed;
                PART_Content.Content = control;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create GameView control '{name}'.");
                SetEmpty($"Failed to create control '{name}': {ex.Message}");
            }
        }

        private void ApplyGameContextIfPossible()
        {
            if (_createdControl is not PluginUserControl pluginControl)
            {
                return;
            }

            try
            {
                pluginControl.GameContextChanged(null, Game);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"GameContextChanged failed for '{ControlName}'.");
                SetEmpty($"'{ControlName}' threw during GameContextChanged: {ex.Message}");
            }
        }
    }
}
