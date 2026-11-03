using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Views.Settings.General
{
    public partial class ForkGeneralSettingsSection : UserControl
    {
        private readonly PlayniteAchievementsPlugin _plugin;

        internal ForkGeneralSettingsSection(PlayniteAchievementsSettings settings, PlayniteAchievementsPlugin plugin)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            InitializeComponent();
            DataContext = settings ?? throw new ArgumentNullException(nameof(settings));
            DebugModeCheckBox.DataContext = Providers.ProviderRegistry.Instance?.GetSettingsForEdit("Local");
        }

        private void DebugMode_Changed(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox checkBox) || !(checkBox.DataContext is Providers.Local.LocalSettings)) return;
            try
            {
                Services.Logging.PollingDebugLog.Configure(_plugin.GetPluginUserDataPath(), checkBox.IsChecked == true,
                    (checkBox.DataContext as Providers.Local.LocalSettings)?.SampleProcessCpuThreads == true);
            }
            catch (Exception ex)
            {
                _plugin.PlayniteApi.Dialogs.ShowErrorMessage(ex.Message, "Debug mode");
            }
        }

        private List<string> GetLogPaths()
        {
            var root = _plugin.PlayniteApi.Paths.ApplicationPath;
            return new List<string>
            {
                Path.Combine(root, "playnite.log"),
                Path.Combine(root, "extensions.log"),
                Path.Combine(_plugin.GetPluginUserDataPath(), "playniteachievements.log"),
                Path.Combine(_plugin.GetPluginUserDataPath(), "PollingDebug.log")
            };
        }

        private List<string> SelectLogs(string title)
        {
            var window = _plugin.PlayniteApi.Dialogs.CreateWindow(new WindowCreationOptions
            {
                ShowMinimizeButton = false,
                ShowMaximizeButton = false
            });
            window.Title = title;
            window.Owner = Window.GetWindow(this);
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new StackPanel { Margin = new Thickness(20), MinWidth = 320 };
            var description = new TextBlock
            {
                Text = title == "Delete Logs"
                    ? "Select logs to delete. Active logs may be recreated by Playnite."
                    : title == "Copy Logs" ? "Select logs to copy." : "Select logs to open.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
                Margin = new Thickness(0, 0, 0, 12)
            };
            description.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            panel.Children.Add(description);
            var choices = new List<CheckBox>();
            foreach (var path in GetLogPaths())
            {
                var choice = new CheckBox
                {
                    Content = Path.GetFileName(path), Tag = path,
                    ToolTip = path, Margin = new Thickness(0, 0, 0, 8)
                };
                choices.Add(choice);
                panel.Children.Add(choice);
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var accept = new Button { Content = title, IsDefault = true, IsEnabled = false, MinWidth = 100, Margin = new Thickness(0, 8, 8, 0) };
            var cancel = new Button { Content = L("LOCPlayAch_Button_Cancel"), IsCancel = true, MinWidth = 100, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var choice in choices)
            {
                RoutedEventHandler update = (s, args) => accept.IsEnabled = choices.Exists(c => c.IsChecked == true);
                choice.Checked += update;
                choice.Unchecked += update;
            }
            accept.Click += (s, args) => window.DialogResult = true;
            cancel.Click += (s, args) => window.DialogResult = false;
            buttons.Children.Add(accept);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            window.Content = panel;
            if (window.ShowDialog() != true) return null;
            return choices.FindAll(c => c.IsChecked == true).ConvertAll(c => (string)c.Tag);
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            var paths = SelectLogs("Open logs");
            if (paths == null) return;
            RunLogAction(paths, "Open logs", path => Process.Start(new ProcessStartInfo
            {
                FileName = path, UseShellExecute = true, Verb = "open"
            }), showSuccess: false);
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            var paths = SelectLogs("Copy Logs");
            if (paths == null) return;
            var folder = _plugin.PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrWhiteSpace(folder)) return;
            RunLogAction(paths, "Copy Logs", path =>
            {
                var destination = Path.Combine(folder, Path.GetFileName(path));
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The destination is the original log file. Select another folder.");
                if (File.Exists(destination) && _plugin.PlayniteApi.Dialogs.ShowMessage(
                    "Replace the existing file?\n" + destination, "Copy Logs",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    throw new OperationCanceledException("Existing file was kept.");
                // Allow copying logs while their writers are active.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                    input.CopyTo(output);
            });
        }

        private void DeleteLogs_Click(object sender, RoutedEventArgs e)
        {
            var paths = SelectLogs("Delete Logs");
            if (paths == null) return;
            if (_plugin.PlayniteApi.Dialogs.ShowMessage("Delete these log files?\n" +
                string.Join("\n", paths.ConvertAll(Path.GetFileName)), "Delete Logs",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            RunLogAction(paths, "Delete Logs", File.Delete);
        }

        private void RunLogAction(List<string> paths, string title, Action<string> action, bool showSuccess = true)
        {
            var results = new List<string>();
            var failed = false;
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path)) throw new FileNotFoundException("Log file was not found.", path);
                    action(path);
                    if (showSuccess) results.Add(Path.GetFileName(path) + ": Completed.");
                }
                catch (OperationCanceledException ex)
                {
                    results.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
                catch (Exception ex)
                {
                    failed = true;
                    results.Add(Path.GetFileName(path) + ": " + ex.Message);
                }
            }
            if (!showSuccess && !failed) return;
            _plugin.PlayniteApi.Dialogs.ShowMessage(string.Join("\n", results), title,
                MessageBoxButton.OK, failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        private static string L(string key) => ResourceProvider.GetString(key);
    }
}
