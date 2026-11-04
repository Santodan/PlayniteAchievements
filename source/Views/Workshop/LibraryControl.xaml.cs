using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Workshop;
using PlayniteAchievements.ViewModels.Library;
using PlayniteAchievements.Views.Helpers;
using DialogResult = System.Windows.Forms.DialogResult;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;

namespace PlayniteAchievements.Views.Workshop
{
    /// <summary>
    /// The Library page, hosted by Settings > Workshop and by the Workshop window: every saved
    /// look, every item added from the Workshop and the Workshop game data on games, where each
    /// is used, and the actions on it. Questions go through Playnite's dialogs.
    /// </summary>
    public partial class LibraryControl : UserControl
    {
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;

        public LibraryControl()
        {
            InitializeComponent();
        }

        internal LibraryControl(PlayniteAchievementsPlugin plugin, ILogger logger, LibraryItemKind? focusKind = null, Guid? focusGameId = null)
            : this()
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            DataContext = new LibraryViewModel(plugin, logger, focusKind, focusGameId)
            {
                Confirm = Confirm,
                ChooseMergeOrReplace = ChooseMergeOrReplace,
                PickGameDataTargets = row => GameDataTargetPicker.Pick(
                    plugin,
                    Window.GetWindow(this),
                    row.IndexItem?.Id,
                    row.Name,
                    row.IndexItem?.Game?.Keys),
                AskName = AskName,
                OpenShare = share => plugin.OpenWorkshopShare(
                    share.Kind,
                    Window.GetWindow(this),
                    packagePath: share.PackagePath,
                    defaultName: share.DefaultName,
                    libraryItemId: share.LibraryItemId,
                    publishedItemId: share.PublishedItemId),
                OpenEdit = item => plugin.OpenWorkshopEdit(item, Window.GetWindow(this)),
                OpenSettings = OpenSettings
            };
        }

        private LibraryViewModel ViewModel => DataContext as LibraryViewModel;

        public void Cleanup()
        {
            ViewModel?.Dispose();
        }

        private bool Confirm(string message)
        {
            return _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                       message,
                       ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                       MessageBoxButton.YesNo,
                       MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private LibraryApplyMode? ChooseMergeOrReplace(string message)
        {
            var dialogs = _plugin.PlayniteApi?.Dialogs;
            if (dialogs == null)
            {
                return null;
            }

            var merge = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Common_Merge"), isDefault: true);
            var replace = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Button_Replace"));
            var cancel = new MessageBoxOption(ResourceProvider.GetString("LOCPlayAch_Common_Cancel"), isCancel: true);
            var picked = dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxImage.Question,
                new List<MessageBoxOption> { merge, replace, cancel });
            if (picked == merge)
            {
                return LibraryApplyMode.Merge;
            }

            return picked == replace ? LibraryApplyMode.Replace : (LibraryApplyMode?)null;
        }

        private string AskName(string current)
        {
            return PresetNamePrompt.TryAsk(
                _plugin,
                current,
                PackagePresetStore.SanitizeName,
                PackagePresetStore.MaxNameLength,
                out var name,
                ResourceProvider.GetString("LOCPlayAch_Common_Rename"))
                ? name
                : null;
        }

        /// <summary>
        /// Shows a settings place. Hosted by the settings, the page switches its own window. In
        /// the Workshop window, that window closes first; the settings window under it switches,
        /// or the settings open on the place when none is open.
        /// </summary>
        private void OpenSettings(SettingsNavigationRequest request)
        {
            var hostingSettings = FindAncestor<SettingsControl>(this);
            if (hostingSettings != null)
            {
                hostingSettings.NavigateTo(request);
                return;
            }

            // Queued so the settings switch or open after this window has closed and the click
            // has returned.
            var dispatcher = Dispatcher;
            Window.GetWindow(this)?.Close();
            dispatcher.BeginInvoke(new Action(() => _plugin.OpenSettingsAt(request)));
        }

        private static T FindAncestor<T>(DependencyObject element)
            where T : DependencyObject
        {
            while (element != null && !(element is T))
            {
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }

            return element as T;
        }

        /// <summary>Export is the same two-way menu as everywhere else: to a file, or shared to the Workshop.</summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            var row = viewModel?.SelectedRow;
            if (row == null || !row.HasFile)
            {
                return;
            }

            WorkshopMenus.OpenExport(
                sender as Button,
                () => ExportFile(row),
                () => viewModel.ShareCommand.Execute(null));
        }

        /// <summary>Copies the item's package file wherever the user chooses; it is already a valid package.</summary>
        private void ExportFile(LibraryItemRow row)
        {
            if (row == null || !row.HasFile || !File.Exists(row.FilePath))
            {
                return;
            }

            try
            {
                var extension = LibraryStore.ExtensionOf(row.Kind);
                var dialog = new SaveFileDialog
                {
                    Filter = "*" + extension + "|*" + extension,
                    AddExtension = true,
                    DefaultExt = extension.TrimStart('.'),
                    FileName = Path.GetFileName(row.FilePath)
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var destination = dialog.FileName;
                if (!destination.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    destination += extension;
                }

                File.Copy(row.FilePath, destination, overwrite: true);
                ShowMessage(ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting a library item.");
                ShowMessage(string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        private void ShowMessage(string message, MessageBoxImage image)
        {
            _plugin?.PlayniteApi?.Dialogs?.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }
    }
}
