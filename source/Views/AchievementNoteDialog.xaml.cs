using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class AchievementNoteDialog : UserControl
    {
        public static readonly DependencyProperty NoteTextProperty =
            DependencyProperty.Register(
                nameof(NoteText),
                typeof(string),
                typeof(AchievementNoteDialog),
                new PropertyMetadata(string.Empty, OnNoteTextChanged));

        private readonly bool _isReadOnly;

        public AchievementNoteDialog()
            : this(string.Empty, string.Empty, string.Empty, isReadOnly: false, achievementIconSource: null)
        {
        }

        public AchievementNoteDialog(
            string achievementTitle,
            string achievementApiName,
            string noteText,
            bool isReadOnly,
            string achievementIconSource = null)
        {
            _isReadOnly = isReadOnly;
            InitializeComponent();
            DataContext = this;

            AchievementTitleTextBlock.Text = achievementTitle ?? string.Empty;
            AchievementApiNameTextBlock.Text = achievementApiName ?? string.Empty;
            ApplyAchievementIcon(achievementIconSource);
            NoteTextBox.MaxLength = AchievementNoteHelper.MaxNoteLength;
            NoteText = noteText ?? string.Empty;

            Loaded += AchievementNoteDialog_Loaded;
            UpdateModeUi();
            UpdatePreview();
        }

        public event EventHandler RequestClose;

        public string NoteText
        {
            get => (string)GetValue(NoteTextProperty);
            set => SetValue(NoteTextProperty, value ?? string.Empty);
        }

        public string SavedNote => AchievementNoteHelper.NormalizeNote(NoteText);

        public bool? DialogResult { get; private set; }

        private void ApplyAchievementIcon(string achievementIconSource)
        {
            if (AchievementIconBorder == null || AchievementIconImage == null)
            {
                return;
            }

            AchievementIconBorder.Visibility = string.IsNullOrWhiteSpace(achievementIconSource)
                ? Visibility.Collapsed
                : Visibility.Visible;
            AsyncImage.SetUri(AchievementIconImage, achievementIconSource);
        }

        private void AchievementNoteDialog_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isReadOnly)
            {
                NoteTextBox?.Focus();
                Keyboard.Focus(NoteTextBox);
                NoteTextBox.CaretIndex = NoteTextBox.Text?.Length ?? 0;
            }
        }

        private static void OnNoteTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementNoteDialog dialog)
            {
                dialog.UpdatePreview();
            }
        }

        private void UpdateModeUi()
        {
            if (EditorPanel == null)
            {
                return;
            }

            EditorPanel.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            ViewScrollViewer.Visibility = _isReadOnly ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            ClearButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            CancelButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
            CloseButton.Visibility = _isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdatePreview()
        {
            if (CharacterCountTextBlock != null)
            {
                var format = L(
                    "LOCPlayAch_NotesDialog_CharacterCountFormat",
                    "{0} / {1} characters");
                CharacterCountTextBlock.Text = string.Format(
                    format,
                    (NoteText ?? string.Empty).Length,
                    AchievementNoteHelper.MaxNoteLength);
            }

            if (ClearButton != null)
            {
                ClearButton.IsEnabled = !string.IsNullOrWhiteSpace(NoteText);
            }

            AchievementNoteInlineFormatter.ApplyFormattedText(PreviewTextBlock, NoteText);
            AchievementNoteInlineFormatter.ApplyFormattedText(ViewTextBlock, NoteText);
        }

        private void BoldButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyFormattingMarker("**");
            e.Handled = true;
        }

        private void ItalicButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyFormattingMarker("*");
            e.Handled = true;
        }

        private void UnderlineButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyFormattingMarker("__");
            e.Handled = true;
        }

        private void ApplyFormattingMarker(string marker)
        {
            if (_isReadOnly || NoteTextBox == null || string.IsNullOrEmpty(marker))
            {
                return;
            }

            var selectionStart = NoteTextBox.SelectionStart;
            var selectionLength = NoteTextBox.SelectionLength;
            var selectedText = NoteTextBox.SelectedText ?? string.Empty;
            var replacement = marker + selectedText + marker;
            var currentText = NoteTextBox.Text ?? string.Empty;
            var nextLength = currentText.Length - selectionLength + replacement.Length;
            if (nextLength > AchievementNoteHelper.MaxNoteLength)
            {
                return;
            }

            NoteTextBox.SelectedText = replacement;
            NoteTextBox.SelectionStart = selectionStart + marker.Length;
            NoteTextBox.SelectionLength = selectedText.Length;
            NoteTextBox.Focus();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            NoteText = string.Empty;
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void NoteTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.B)
                {
                    ApplyFormattingMarker("**");
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.I)
                {
                    ApplyFormattingMarker("*");
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.U)
                {
                    ApplyFormattingMarker("__");
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
