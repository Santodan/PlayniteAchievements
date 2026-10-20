using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Picks one glyph from Playnite's icon font, searchable by its IcoFont name.
    /// </summary>
    public partial class IcoFontPickerDialog : UserControl
    {
        public IcoFontPickerDialog()
        {
            InitializeComponent();
            GlyphList.ItemsSource = IcoFontCatalog.All;
            GlyphList.SelectionChanged += (_, __) => SelectedName.Text = SelectedGlyph?.Name ?? string.Empty;
            Loaded += (_, __) => Dispatcher.BeginInvoke(
                new Action(() => Keyboard.Focus(SearchBox)),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        public bool? DialogResult { get; private set; }

        /// <summary>The chosen codepoint as hex, or null when the user chose no icon.</summary>
        public string SelectedCode { get; private set; }

        public event EventHandler RequestClose;

        private IcoFontGlyph SelectedGlyph => GlyphList.SelectedItem as IcoFontGlyph;

        /// <summary>
        /// Shows the picker over the current window. True when the user confirmed, with
        /// <paramref name="code"/> the chosen codepoint or null for "no icon".
        /// </summary>
        public static bool TryPick(string currentCode, out string code)
        {
            code = currentCode;
            var view = new IcoFontPickerDialog();
            view.Select(currentCode);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_Common_ChooseIcon"),
                view,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 540,
                    Height = 520
                });

            try
            {
                window.Owner = API.Instance?.Dialogs?.GetCurrentAppWindow();
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            catch
            {
            }

            WindowPlacementPersistenceService.Attach(window, "IcoFontPicker");
            view.RequestClose += (_, __) => window.Close();
            window.ShowDialog();

            if (view.DialogResult != true)
            {
                return false;
            }

            code = view.SelectedCode;
            return true;
        }

        private void Select(string code)
        {
            var glyph = IcoFontCatalog.All.FirstOrDefault(item =>
                string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
            if (glyph != null)
            {
                GlyphList.SelectedItem = glyph;
                GlyphList.ScrollIntoView(glyph);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var selected = SelectedGlyph;
            GlyphList.ItemsSource = IcoFontCatalog.Search(SearchBox.Text);
            if (selected != null)
            {
                GlyphList.SelectedItem = selected;
            }
        }

        private void GlyphList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedGlyph != null)
            {
                Close(true, SelectedGlyph.Code);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Close(true, SelectedGlyph?.Code);

        private void NoneButton_Click(object sender, RoutedEventArgs e) => Close(true, null);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false, null);

        private void Close(bool confirmed, string code)
        {
            DialogResult = confirmed;
            SelectedCode = code;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
