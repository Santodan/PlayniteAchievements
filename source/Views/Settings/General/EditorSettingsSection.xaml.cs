using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Editor section. Hosts the auto capstone generation toggle and the text
    /// templates auto capstones are titled and described with, plus the Apply action that brings
    /// existing capstones in line.
    /// </summary>
    public partial class EditorSettingsSection : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;

        public EditorSettingsSection()
        {
            InitializeComponent();
        }

        internal EditorSettingsSection(PlayniteAchievementsSettings settings, PlayniteAchievementsPlugin plugin)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            TemplateRows = new List<AutoCapstoneTemplateRow>
            {
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.GameName, "LOCPlayAch_Settings_AutoCapstoneText_GameTitle", this),
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.GameDescription, "LOCPlayAch_Settings_AutoCapstoneText_GameDescription", this),
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.CategoryName, "LOCPlayAch_Settings_AutoCapstoneText_CategoryTitle", this),
                new AutoCapstoneTemplateRow(AutoCapstoneTextField.CategoryDescription, "LOCPlayAch_Settings_AutoCapstoneText_CategoryDescription", this)
            };
            TemplateItems.ItemsSource = TemplateRows;
        }

        public IReadOnlyList<AutoCapstoneTemplateRow> TemplateRows { get; }

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

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            // A template still being typed is committed first, so Apply uses what the box shows.
            foreach (var row in TemplateRows)
            {
                row.Commit();
            }

            _plugin.ApplyAutoCapstoneTextWithProgress();
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
            private readonly EditorSettingsSection _owner;
            private string _text;
            private bool _isInvalid;

            internal AutoCapstoneTemplateRow(AutoCapstoneTextField field, string labelKey, EditorSettingsSection owner)
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
