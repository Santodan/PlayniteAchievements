using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Tagging section. Hosts tag enable toggles, tag name overrides, and
    /// the apply-and-sync / remove-all actions backed by the tag sync service.
    /// </summary>
    public partial class TaggingSettingsSection : UserControl
    {
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private ObservableCollection<CompletionStatusOption> _completionStatuses;

        /// <summary>
        /// Represents a selectable completion status in the picker. Guid.Empty stands for
        /// the default behavior (status named "Completed").
        /// </summary>
        public class CompletionStatusOption
        {
            public Guid Id { get; set; }
            public string Name { get; set; }
        }

        /// <summary>
        /// Completion statuses from the Playnite database, preceded by a default option.
        /// Bound as the completion status ComboBox ItemsSource.
        /// </summary>
        public ObservableCollection<CompletionStatusOption> CompletionStatuses
        {
            get
            {
                if (_completionStatuses == null)
                {
                    _completionStatuses = BuildCompletionStatusOptions();
                }

                return _completionStatuses;
            }
        }

        private ObservableCollection<CompletionStatusOption> BuildCompletionStatusOptions()
        {
            var options = new ObservableCollection<CompletionStatusOption>
            {
                new CompletionStatusOption
                {
                    Id = Guid.Empty,
                    Name = L("LOCPlayAch_Common_Default")
                }
            };

            var statuses = _plugin?.PlayniteApi?.Database?.CompletionStatuses;
            if (statuses != null)
            {
                foreach (var status in statuses.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    options.Add(new CompletionStatusOption { Id = status.Id, Name = status.Name });
                }
            }

            return options;
        }

        public TaggingSettingsSection()
        {
            InitializeComponent();
        }

        internal TaggingSettingsSection(PlayniteAchievementsPlugin plugin, ILogger logger)
            : this()
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
        }

        /// <summary>
        /// Commits pending tag name changes from text boxes to the source. Walks the section's
        /// tree so the templated per-area rows are covered along with the named boxes; every
        /// TextBox in this section holds a tag name.
        /// </summary>
        private void CommitTagNameBindings()
        {
            foreach (var textBox in FindTextBoxes(this))
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
        }

        private static IEnumerable<TextBox> FindTextBoxes(DependencyObject parent)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox textBox)
                {
                    yield return textBox;
                    continue;
                }

                foreach (var nested in FindTextBoxes(child))
                {
                    yield return nested;
                }
            }
        }

        private void ApplyAndSync_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Commit any pending text box changes
                CommitTagNameBindings();

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    _logger?.Warn("TagSyncService not available");
                    return;
                }

                // Sync all tags (handles orphan cleanup and re-tagging with new names)
                tagSyncService.SyncAllTags();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to apply and sync tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void RemoveAllTags_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Tagging_RemoveAllConfirm"),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                var tagSyncService = _plugin.TagSyncService;
                if (tagSyncService == null)
                {
                    _logger?.Warn("TagSyncService not available");
                    return;
                }

                tagSyncService.RemoveAllTags();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to remove tags.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
