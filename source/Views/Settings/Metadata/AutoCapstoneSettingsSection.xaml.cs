using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Settings.Controls;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.Views.Settings.Metadata
{
    /// <summary>
    /// Metadata settings: Auto capstone section. Hosts the auto capstone generation toggle, the
    /// text templates auto capstones are titled and described with, and the global tags each
    /// kind of auto capstone carries. Saving applies the templates and tags to existing capstones.
    /// </summary>
    public partial class AutoCapstoneSettingsSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public AutoCapstoneSettingsSection()
        {
            InitializeComponent();
        }

        internal AutoCapstoneSettingsSection(PlayniteAchievementsSettings settings, PlayniteAchievementsPlugin plugin)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateTagTexts);
            UpdateTagTexts();

            // Grouped under Game and Category headers, so the rows reuse the generic column labels.
            var gameRows = new[]
            {
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.GameName, NameLabelKey, this),
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.GameDescription, DescriptionLabelKey, this)
            };
            var categoryRows = new[]
            {
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.CategoryName, NameLabelKey, this),
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.CategoryDescription, DescriptionLabelKey, this)
            };
            TemplateRows = gameRows.Concat(categoryRows).ToList();
            GameTemplateItems.ItemsSource = gameRows;
            CategoryTemplateItems.ItemsSource = categoryRows;
        }

        private const string NameLabelKey = "LOCPlayAch_Common_Name";
        private const string DescriptionLabelKey = "LOCPlayAch_Common_Description";

        public IReadOnlyList<AutoCapstoneTemplateRow> TemplateRows { get; }

        public static readonly DependencyProperty GameTagsTextProperty =
            DependencyProperty.Register(nameof(GameTagsText), typeof(string),
                typeof(AutoCapstoneSettingsSection), new PropertyMetadata(string.Empty));

        /// <summary>Summary text for the whole-game capstone tag picker.</summary>
        public string GameTagsText
        {
            get => (string)GetValue(GameTagsTextProperty);
            set => SetValue(GameTagsTextProperty, value);
        }

        public static readonly DependencyProperty CategoryTagsTextProperty =
            DependencyProperty.Register(nameof(CategoryTagsText), typeof(string),
                typeof(AutoCapstoneSettingsSection), new PropertyMetadata(string.Empty));

        /// <summary>Summary text for the category capstone tag picker.</summary>
        public string CategoryTagsText
        {
            get => (string)GetValue(CategoryTagsTextProperty);
            set => SetValue(CategoryTagsTextProperty, value);
        }

        private void GameTagsButton_Click(object sender, RoutedEventArgs e) => OpenTagMenu(sender as Button, wholeGame: true);

        private void CategoryTagsButton_Click(object sender, RoutedEventArgs e) => OpenTagMenu(sender as Button, wholeGame: false);

        // The session's own tag list, so a tag added on the Tags page before saving is offered.
        private void OpenTagMenu(Button button, bool wholeGame)
        {
            AutoCapstoneTagSelectorMenu.Open(
                button,
                _settings?.Persisted?.ResolveAchievementTags(),
                () => wholeGame ? _settings?.Persisted?.AutoCapstoneGameTags : _settings?.Persisted?.AutoCapstoneCategoryTags,
                value =>
                {
                    var persisted = _settings?.Persisted;
                    if (persisted == null)
                    {
                        return;
                    }

                    if (wholeGame)
                    {
                        persisted.AutoCapstoneGameTags = value;
                    }
                    else
                    {
                        persisted.AutoCapstoneCategoryTags = value;
                    }
                },
                UpdateTagTexts);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(PersistedSettings.AutoCapstoneGameTags) ||
                e?.PropertyName == nameof(PersistedSettings.AutoCapstoneCategoryTags) ||
                e?.PropertyName == nameof(PersistedSettings.AchievementTags))
            {
                UpdateTagTexts();
            }
        }

        private void UpdateTagTexts()
        {
            var persisted = _settings?.Persisted;
            var definitions = persisted?.ResolveAchievementTags();
            GameTagsText = AutoCapstoneTagSelectorMenu.Format(definitions, persisted?.AutoCapstoneGameTags);
            CategoryTagsText = AutoCapstoneTagSelectorMenu.Format(definitions, persisted?.AutoCapstoneCategoryTags);
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private void TemplateTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as AutoCapstoneTemplateRow)?.Commit();
        }

        private void TemplateTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ((sender as FrameworkElement)?.DataContext as AutoCapstoneTemplateRow)?.Commit();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Stores a committed template, or null when it is the language's default so it keeps
        /// following the language, and remembers it so text written with it stays recognizable.
        /// </summary>
        internal void Store(AutoCapstoneTextField field, string edited)
        {
            var persisted = _settings.Persisted;
            if (persisted == null)
            {
                return;
            }

            var stored = AutoCapstoneText.NormalizeForStore(edited, AutoCapstoneText.GetDefault(field));
            AutoCapstoneText.SetStored(persisted, field, stored);
            if (stored != null)
            {
                _plugin.RecordAutoCapstoneTemplate(stored);
            }
        }

        internal string ReadEffective(AutoCapstoneTextField field)
        {
            var stored = AutoCapstoneText.GetStored(_settings.Persisted, field);
            return string.IsNullOrWhiteSpace(stored) ? AutoCapstoneText.GetDefault(field) : stored;
        }

        /// <summary>One template's text box, pending until committed on focus loss or Enter.</summary>
        public sealed class AutoCapstoneTemplateRow : ObservableObject
        {
            private readonly AutoCapstoneSettingsSection _owner;
            private string _text;
            private bool _isInvalid;

            internal AutoCapstoneTemplateRow(AutoCapstoneTextField field, string labelKey, AutoCapstoneSettingsSection owner)
            {
                Field = field;
                Label = ResourceProvider.GetString(labelKey);
                _owner = owner;
                _text = owner.ReadEffective(field);
                ResetCommand = new RelayCommand(_ => Reset());
            }

            public AutoCapstoneTextField Field { get; }

            public string Label { get; }

            public string Text
            {
                get => _text;
                set => SetValue(ref _text, value);
            }

            public bool IsInvalid
            {
                get => _isInvalid;
                private set => SetValue(ref _isInvalid, value);
            }

            public ICommand ResetCommand { get; }

            /// <summary>
            /// Stores the text when it is blank (back to the default) or formats; otherwise keeps
            /// it pending and flags it.
            /// </summary>
            public void Commit()
            {
                if (!string.IsNullOrWhiteSpace(Text) && !AutoCapstoneText.IsValidTemplate(Text))
                {
                    IsInvalid = true;
                    return;
                }

                IsInvalid = false;
                _owner.Store(Field, Text);
                Text = _owner.ReadEffective(Field);
            }

            private void Reset()
            {
                Text = null;
                Commit();
            }
        }
    }
}
