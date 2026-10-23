using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Views.Helpers;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Picks one glyph from Playnite's icon font, searchable by its IcoFont name.
    /// </summary>
    public partial class IcoFontPickerDialog : UserControl
    {
        // A glyph cell's width (GlyphCellStyle), which sets how many fit in a row.
        private const double CellWidth = 60;

        // The glyph panel's border and padding on both sides, which the rows do not get.
        private const double PanelInset = 2 * (1 + 4);

        // The All entry's icon: IcoFont's "layers" glyph.
        private const string AllIcon = "EF5C";

        // Searching every glyph per keystroke is what made typing lag; the search runs once the
        // typing pauses.
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(200);

        private readonly DispatcherTimer _searchTimer;
        private readonly Dictionary<string, IcoFontPickerCell> _cellsByCode;
        private IcoFontPickerCell _selected;
        private List<IcoFontPickerCell> _shown = new List<IcoFontPickerCell>();
        private int _glyphsPerRow = 9;

        // The chosen category's name; null for All.
        private string _category;

        public IcoFontPickerDialog()
        {
            InitializeComponent();
            _cellsByCode = IcoFontCatalog.All
                .GroupBy(glyph => glyph.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => new IcoFontPickerCell(group.First()), StringComparer.OrdinalIgnoreCase);
            _searchTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = SearchDelay };
            _searchTimer.Tick += (_, __) =>
            {
                _searchTimer.Stop();
                ShowGlyphs(IcoFontCatalog.Search(SearchBox.Text, _category));
            };

            CategoryList.ItemsSource = new[] { new IcoFontPickerCategory(null, AllIcon) }
                .Concat(IcoFontCatalog.Categories.Select(category => new IcoFontPickerCategory(category.Name, category.Icon)))
                .ToList();
            CategoryList.SelectedIndex = 0;

            GlyphRows.SizeChanged += GlyphRows_SizeChanged;
            Loaded += (_, __) =>
            {
                Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(SearchBox)), DispatcherPriority.Input);
                Dispatcher.BeginInvoke(new Action(ScrollSelectedIntoView), DispatcherPriority.Background);
            };
            Unloaded += (_, __) => _searchTimer.Stop();
        }

        public bool? DialogResult { get; private set; }

        /// <summary>The chosen codepoint as hex, or null when the user chose no icon.</summary>
        public string SelectedCode { get; private set; }

        public event EventHandler RequestClose;

        /// <summary>
        /// Shows the picker over the current window. True when the user confirmed, with
        /// <paramref name="code"/> the chosen codepoint or null for "no icon".
        /// </summary>
        public static bool TryPick(string currentCode, out string code)
        {
            code = currentCode;
            var view = new IcoFontPickerDialog();
            view.Select(currentCode);
            view.ShowCategoryOf(currentCode);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_Common_ChooseIcon"),
                view,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 760,
                    Height = 600
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

        private void ShowGlyphs(IReadOnlyList<IcoFontGlyph> glyphs)
        {
            _shown = glyphs
                .Select(glyph => _cellsByCode.TryGetValue(glyph.Code, out var cell) ? cell : null)
                .Where(cell => cell != null)
                .ToList();
            GlyphRows.ItemsSource = BuildRows();
        }

        /// <summary>The shown glyphs in rows as wide as the list; the list builds only the rows on screen.</summary>
        private List<IReadOnlyList<IcoFontPickerCell>> BuildRows()
        {
            var rows = new List<IReadOnlyList<IcoFontPickerCell>>();
            for (var i = 0; i < _shown.Count; i += _glyphsPerRow)
            {
                rows.Add(_shown.GetRange(i, Math.Min(_glyphsPerRow, _shown.Count - i)));
            }

            return rows;
        }

        /// <summary>
        /// Re-rows the glyphs when the window's width fits a different number per row, keeping the
        /// first glyph on screen in view.
        /// </summary>
        private void GlyphRows_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged)
            {
                return;
            }

            var available = GlyphRows.ActualWidth - PanelInset - SystemParameters.VerticalScrollBarWidth;
            var perRow = Math.Max(1, (int)Math.Floor(available / CellWidth));
            if (perRow == _glyphsPerRow)
            {
                return;
            }

            var scroller = GlyphRows.Template?.FindName("GlyphScroller", GlyphRows) as ScrollViewer;
            var firstGlyph = (int)((scroller?.VerticalOffset ?? 0) * _glyphsPerRow);
            _glyphsPerRow = perRow;
            GlyphRows.ItemsSource = BuildRows();
            scroller?.ScrollToVerticalOffset(firstGlyph / perRow);
        }

        private void Select(string code)
        {
            if (_selected != null)
            {
                _selected.IsSelected = false;
            }

            _selected = code != null && _cellsByCode.TryGetValue(code, out var cell) ? cell : null;
            if (_selected != null)
            {
                _selected.IsSelected = true;
            }

            SelectedName.Text = _selected?.Name ?? string.Empty;
        }

        /// <summary>Opens on the category of the glyph at <paramref name="code"/>, when it has one.</summary>
        private void ShowCategoryOf(string code)
        {
            var category = code == null ? null : IcoFontCatalog.CategoryOf(code);
            var item = CategoryList.Items.OfType<IcoFontPickerCategory>().FirstOrDefault(entry => entry.Name == category);
            if (item != null)
            {
                CategoryList.SelectedItem = item;
            }
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _category = (CategoryList.SelectedItem as IcoFontPickerCategory)?.Name;
            _searchTimer.Stop();
            ShowGlyphs(IcoFontCatalog.Search(SearchBox.Text, _category));
            CategoryList.ScrollIntoView(CategoryList.SelectedItem);
        }

        /// <summary>Scrolls the list to the chosen glyph's row.</summary>
        private void ScrollSelectedIntoView()
        {
            var index = _selected == null ? -1 : _shown.IndexOf(_selected);
            var scroller = GlyphRows.Template?.FindName("GlyphScroller", GlyphRows) as ScrollViewer;
            if (index >= 0 && scroller != null)
            {
                scroller.ScrollToVerticalOffset(index / _glyphsPerRow);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void GlyphCell_Click(object sender, RoutedEventArgs e)
        {
            Select(((sender as FrameworkElement)?.DataContext as IcoFontPickerCell)?.Code);
        }

        private void GlyphCell_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var cell = (sender as FrameworkElement)?.DataContext as IcoFontPickerCell;
            if (cell != null)
            {
                Close(true, cell.Code);
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e) => Close(true, _selected?.Code);

        private void NoneButton_Click(object sender, RoutedEventArgs e) => Close(true, null);

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close(false, null);

        private void Close(bool confirmed, string code)
        {
            _searchTimer.Stop();
            DialogResult = confirmed;
            SelectedCode = code;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// One entry of the picker's category strip, shown as its icon alone: a category, or All when
    /// its name is null.
    /// </summary>
    public sealed class IcoFontPickerCategory
    {
        internal IcoFontPickerCategory(string name, string icon)
        {
            Name = name;
            Glyph = Services.Achievements.AchievementTagHelper.IconGlyph(icon);
        }

        public string Name { get; }

        public string Glyph { get; }
    }

    /// <summary>One glyph in the picker, marked while it is the chosen one.</summary>
    public sealed class IcoFontPickerCell : ObservableObject
    {
        private bool _isSelected;

        internal IcoFontPickerCell(IcoFontGlyph glyph)
        {
            Name = glyph.Name;
            Code = glyph.Code;
            Glyph = glyph.Glyph;
        }

        public string Name { get; }

        public string Code { get; }

        public string Glyph { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }
    }
}
