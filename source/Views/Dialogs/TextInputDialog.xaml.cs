using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    public partial class TextInputDialog : UserControl
    {
        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(nameof(Hint), typeof(string), typeof(TextInputDialog), new PropertyMetadata(string.Empty));

        public string InputText
        {
            get => (string)GetValue(InputTextProperty);
            set => SetValue(InputTextProperty, value);
        }

        public static readonly DependencyProperty InputTextProperty =
            DependencyProperty.Register(nameof(InputText), typeof(string), typeof(TextInputDialog), new PropertyMetadata(string.Empty));

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public TextInputDialog()
        {
            InitializeComponent();
            DataContext = this;
            Loaded += TextInputDialog_Loaded;
        }

        /// <summary>
        /// Puts the caret in the box, so the dialog can be typed into as soon as it appears.
        /// </summary>
        /// <remarks>
        /// The declared FocusManager.FocusedElement sets logical focus, which is not the same as
        /// keyboard focus: the content is loaded before the window that hosts it is shown, and
        /// showing it takes focus back. Asking again at input priority runs after that, which is
        /// what makes it stick. Any default text is selected, so typing replaces it rather than
        /// appending to it.
        /// </remarks>
        private void TextInputDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    InputTextBox.Focus();
                    Keyboard.Focus(InputTextBox);
                    InputTextBox.SelectAll();
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public TextInputDialog(string hint, string defaultText = "") : this()
        {
            Hint = hint ?? string.Empty;
            InputText = defaultText ?? string.Empty;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                DialogResult = true;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }
    }
}
