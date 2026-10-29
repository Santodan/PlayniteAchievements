using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Puts a search box at the top of every drop-down and right-click menu level the plugin shows
    /// with more than <see cref="MaxUnsearchedOptions"/> options, and filters the list as the user
    /// types.
    /// </summary>
    /// <remarks>
    /// One mechanism for every list. Drop-downs get the box from PlayAch.ComboBox.BaseStyle, whose
    /// popup hosts a TextBox marked with <see cref="IsComboBoxSearchBoxProperty"/>. Menus are found
    /// through class handlers on the opened events, since most are built in code without a plugin
    /// style; only menus opened from the plugin's own elements are touched, and only a level that
    /// is the end of its hierarchy (no item opens a submenu), where the options are what the user
    /// is choosing between.
    ///
    /// A list can register <see cref="CreateActionProperty"/>: when the search matches nothing, one
    /// result offers to create what was typed and runs the action with the text.
    /// </remarks>
    internal static class SelectorSearch
    {
        /// <summary>Lists with this many options or fewer stay as they are.</summary>
        public const int MaxUnsearchedOptions = 5;

        private const string InjectedTag = "PlayAch.SelectorSearch";

        private static bool _registered;

        #region Attached properties

        public static readonly DependencyProperty CreateActionProperty =
            DependencyProperty.RegisterAttached(
                "CreateAction",
                typeof(Action<string>),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        /// <summary>
        /// Run with the typed text when the user picks the create result; set on a ComboBox, a
        /// ContextMenu or the MenuItem whose submenu lists the options.
        /// </summary>
        public static void SetCreateAction(DependencyObject element, Action<string> value) =>
            element.SetValue(CreateActionProperty, value);

        public static Action<string> GetCreateAction(DependencyObject element) =>
            (Action<string>)element.GetValue(CreateActionProperty);

        public static readonly DependencyProperty SearchHandlerProperty =
            DependencyProperty.RegisterAttached(
                "SearchHandler",
                typeof(Func<string, bool>),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        /// <summary>
        /// Lets a ComboBox narrow its own list: called with the query ("" to restore the full list)
        /// in place of the shared filter, and returns whether anything matched. For lists whose
        /// matches need more than themselves shown, as categories need their parents.
        /// </summary>
        public static void SetSearchHandler(DependencyObject element, Func<string, bool> value) =>
            element.SetValue(SearchHandlerProperty, value);

        public static Func<string, bool> GetSearchHandler(DependencyObject element) =>
            (Func<string, bool>)element.GetValue(SearchHandlerProperty);

        public static readonly DependencyProperty MenuMatcherProperty =
            DependencyProperty.RegisterAttached(
                "MenuMatcher",
                typeof(Func<string, Func<MenuItem, bool>>),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        /// <summary>
        /// Lets a menu level decide which options a query shows, in place of matching each option's
        /// own text: given the query, returns whether an option stays.
        /// </summary>
        public static void SetMenuMatcher(DependencyObject element, Func<string, Func<MenuItem, bool>> value) =>
            element.SetValue(MenuMatcherProperty, value);

        public static Func<string, Func<MenuItem, bool>> GetMenuMatcher(DependencyObject element) =>
            (Func<string, Func<MenuItem, bool>>)element.GetValue(MenuMatcherProperty);

        public static readonly DependencyProperty IsDisabledProperty =
            DependencyProperty.RegisterAttached(
                "IsDisabled",
                typeof(bool),
                typeof(SelectorSearch),
                new PropertyMetadata(false));

        /// <summary>
        /// Keeps the search off a ComboBox or a menu (and its submenus) whatever its length: lists
        /// of switches rather than choices, read whole.
        /// </summary>
        public static void SetIsDisabled(DependencyObject element, bool value) =>
            element.SetValue(IsDisabledProperty, value);

        public static bool GetIsDisabled(DependencyObject element) =>
            (bool)element.GetValue(IsDisabledProperty);

        public static readonly DependencyProperty IsComboBoxSearchBoxProperty =
            DependencyProperty.RegisterAttached(
                "IsComboBoxSearchBox",
                typeof(bool),
                typeof(SelectorSearch),
                new PropertyMetadata(false, OnIsComboBoxSearchBoxChanged));

        public static void SetIsComboBoxSearchBox(DependencyObject element, bool value) =>
            element.SetValue(IsComboBoxSearchBoxProperty, value);

        public static bool GetIsComboBoxSearchBox(DependencyObject element) =>
            (bool)element.GetValue(IsComboBoxSearchBoxProperty);

        /// <summary>
        /// The search box of an open menu level whose template pins it above the options: a
        /// presenter named <see cref="SearchHeaderPartName"/> showing this property of the menu
        /// or submenu item. Null while the level has no search.
        /// </summary>
        public static readonly DependencyProperty SearchHeaderProperty =
            DependencyProperty.RegisterAttached(
                "SearchHeader",
                typeof(object),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        public static void SetSearchHeader(DependencyObject element, object value) =>
            element.SetValue(SearchHeaderProperty, value);

        public static object GetSearchHeader(DependencyObject element) =>
            element.GetValue(SearchHeaderProperty);

        /// <summary>The template part that pins a menu level's search box above its scrolling options.</summary>
        public const string SearchHeaderPartName = "PART_SearchHeader";

        private static readonly DependencyProperty MenuStateProperty =
            DependencyProperty.RegisterAttached(
                "MenuState",
                typeof(MenuSearchState),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        #endregion

        /// <summary>Hooks every menu and submenu as it opens. Called once at plugin start.</summary>
        public static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            EventManager.RegisterClassHandler(
                typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnContextMenuOpened));
            EventManager.RegisterClassHandler(
                typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnContextMenuClosed));
            EventManager.RegisterClassHandler(
                typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened));
            EventManager.RegisterClassHandler(
                typeof(MenuItem), MenuItem.SubmenuClosedEvent, new RoutedEventHandler(OnSubmenuClosed));
        }

        #region Text matching

        private static bool Matches(string text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>What a menu option is searched by: its header text and a text tooltip.</summary>
        private static IEnumerable<string> GetMenuItemTexts(MenuItem item)
        {
            yield return GetElementText(item.Header);
            if (item.ToolTip is string toolTip)
            {
                yield return toolTip;
            }
        }

        private static string GetElementText(object content)
        {
            switch (content)
            {
                case null:
                    return null;
                case string text:
                    return text;
                case TextBlock textBlock:
                    return textBlock.Text;
                case ContentControl contentControl:
                    return GetElementText(contentControl.Content);
                case DependencyObject element:
                    return string.Join(" ", LogicalTreeHelper.GetChildren(element)
                        .OfType<object>()
                        .Select(GetElementText)
                        .Where(text => !string.IsNullOrWhiteSpace(text)));
                default:
                    return content.ToString();
            }
        }

        /// <summary>
        /// What a drop-down option is searched by: the text WPF's own type-ahead reads, else the
        /// displayed member, else the option itself.
        /// </summary>
        private static string GetComboBoxItemText(ComboBox combo, object item)
        {
            if (item is ComboBoxItem container)
            {
                var explicitText = TextSearch.GetText(container);
                return !string.IsNullOrEmpty(explicitText) ? explicitText : GetElementText(container.Content);
            }

            var path = TextSearch.GetTextPath(combo);
            if (string.IsNullOrEmpty(path))
            {
                path = combo.DisplayMemberPath;
            }

            if (!string.IsNullOrEmpty(path))
            {
                return ReadPath(item, path)?.ToString();
            }

            return GetElementText(item);
        }

        private static object ReadPath(object source, string path)
        {
            var current = source;
            foreach (var segment in path.Split('.'))
            {
                if (current == null)
                {
                    return null;
                }

                var property = current.GetType().GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
                current = property?.GetValue(current, null);
            }

            return current;
        }

        private static string PlaceholderText(bool canCreate) =>
            canCreate
                ? L("LOCPlayAch_Common_SearchOrAdd", "Search or Add")
                : L("LOCPlayAch_Common_Search", "Search");

        private static string FormatCreateText(string query) =>
            string.Format(L("LOCPlayAch_Common_AddNamed", "Add \"{0}\""), query);

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrEmpty(value) || value.StartsWith("<!", StringComparison.Ordinal) ? fallback : value;
        }

        #endregion

        #region Drop-downs

        private sealed class ComboSearchState
        {
            public ComboBox Combo;
            public TextBox Box;
            public FrameworkElement Host;
            public FrameworkElement Placeholder;
            public Button CreateButton;
            public bool IsActive;
            public string Query = string.Empty;

            // The picked option's row while a search it does not match is on: kept in the list so
            // the selection survives, but not shown.
            public ComboBoxItem HiddenSelectedRow;
        }

        private static readonly DependencyProperty ComboStateProperty =
            DependencyProperty.RegisterAttached(
                "ComboState",
                typeof(ComboSearchState),
                typeof(SelectorSearch),
                new PropertyMetadata(null));

        private static void OnIsComboBoxSearchBoxChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBox box && e.NewValue is bool enabled && enabled)
            {
                // The popup's content loads on the first open, after the template is applied, so the
                // owning ComboBox is looked up once the box is in the tree.
                box.Loaded += ComboSearchBox_Loaded;
            }
        }

        private static void ComboSearchBox_Loaded(object sender, RoutedEventArgs e)
        {
            var box = (TextBox)sender;
            if (!(box.TemplatedParent is ComboBox combo) || combo.GetValue(ComboStateProperty) != null)
            {
                return;
            }

            var placeholder = combo.Template?.FindName("PART_SearchPlaceholder", combo) as FrameworkElement;
            if (placeholder != null)
            {
                AlignPlaceholderToCaret(box, placeholder);
            }

            var state = new ComboSearchState
            {
                Combo = combo,
                Box = box,
                Host = combo.Template?.FindName("PART_SearchHost", combo) as FrameworkElement ?? box,
                Placeholder = placeholder,
                CreateButton = combo.Template?.FindName("PART_SearchCreate", combo) as Button
            };
            combo.SetValue(ComboStateProperty, state);

            box.TextChanged += (_, __) => ApplyComboFilter(state);
            box.PreviewKeyDown += (_, args) => ComboSearchBox_PreviewKeyDown(state, args);
            if (state.CreateButton != null)
            {
                state.CreateButton.Click += (_, __) => RunComboCreate(state);
            }

            // Rows are realized as they scroll into view, the picked one among them.
            combo.ItemContainerGenerator.StatusChanged += (_, __) => UpdateSelectedRow(state);
            combo.DropDownOpened += (_, __) => OnComboOpened(state);
            combo.DropDownClosed += (_, __) => OnComboClosed(state);
            if (combo.IsDropDownOpen)
            {
                OnComboOpened(state);
            }
        }

        /// <summary>
        /// Puts <paramref name="placeholder"/> (laid over <paramref name="box"/> in the same cell)
        /// where the box's caret starts, in the box's font, so typing replaces it in place. The
        /// offset depends on the box's template and padding, which differ between the plugin's
        /// style and a theme's, so it is measured rather than assumed.
        /// </summary>
        private static void AlignPlaceholderToCaret(TextBox box, FrameworkElement placeholder)
        {
            if (placeholder is TextBlock text)
            {
                text.SetBinding(TextBlock.FontSizeProperty, new Binding(nameof(TextBox.FontSize)) { Source = box });
                text.SetBinding(TextBlock.FontFamilyProperty, new Binding(nameof(TextBox.FontFamily)) { Source = box });
            }

            void Align()
            {
                if (!box.IsLoaded)
                {
                    return;
                }

                var caret = box.GetRectFromCharacterIndex(0);
                if (caret.IsEmpty || double.IsNaN(caret.X) || double.IsInfinity(caret.X))
                {
                    return;
                }

                placeholder.HorizontalAlignment = HorizontalAlignment.Left;
                placeholder.VerticalAlignment = VerticalAlignment.Top;
                placeholder.Margin = new Thickness(box.Margin.Left + caret.X, box.Margin.Top + caret.Y, 0, 0);
            }

            box.Loaded += (_, __) => box.Dispatcher.BeginInvoke(new Action(Align), DispatcherPriority.Loaded);
            box.SizeChanged += (_, __) => Align();
            if (box.IsLoaded)
            {
                box.Dispatcher.BeginInvoke(new Action(Align), DispatcherPriority.Loaded);
            }
        }

        private static void OnComboOpened(ComboSearchState state)
        {
            var combo = state.Combo;
            if (state.Placeholder is TextBlock placeholderText)
            {
                placeholderText.Text = PlaceholderText(GetCreateAction(combo) != null);
            }

            // A list that can create always searches: the search is the only way to name the new
            // entry, however few there are.
            state.IsActive = !GetIsDisabled(combo) &&
                             (CountOptions(combo.Items) > MaxUnsearchedOptions || GetCreateAction(combo) != null);
            state.Host.Visibility = state.IsActive ? Visibility.Visible : Visibility.Collapsed;
            UpdateComboCreate(state, matches: true);
            if (!state.IsActive)
            {
                return;
            }

            // After the ComboBox has moved focus to the selected option, which it does once the
            // popup is up.
            combo.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (combo.IsDropDownOpen)
                    {
                        Keyboard.Focus(state.Box);
                    }
                }),
                DispatcherPriority.Input);
        }

        private static int CountOptions(IEnumerable items) =>
            items.Cast<object>().Count(item => !(item is Separator));

        private static void OnComboClosed(ComboSearchState state)
        {
            if (!string.IsNullOrEmpty(state.Box.Text))
            {
                state.Box.Text = string.Empty;
            }

            ClearComboFilter(state);
            UpdateComboCreate(state, matches: true);
        }

        private static void ApplyComboFilter(ComboSearchState state)
        {
            var combo = state.Combo;
            var query = state.Box.Text?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                ClearComboFilter(state);
                UpdateComboCreate(state, matches: true);
                return;
            }

            var handler = GetSearchHandler(combo);
            if (handler != null)
            {
                state.Query = query;
                UpdateComboCreate(state, handler(query));
                return;
            }

            // The picked option stays in the list, hidden when it does not match: a Selector whose
            // SelectedItem leaves its items writes null back through a two-way binding, which would
            // erase the stored value.
            var selected = combo.SelectedItem;
            try
            {
                combo.Items.Filter = item =>
                    ReferenceEquals(item, selected) || Matches(GetComboBoxItemText(combo, item), query);
            }
            catch (InvalidOperationException)
            {
                // A collection that cannot filter (an editing view) is left unfiltered.
                return;
            }

            state.Query = query;
            UpdateSelectedRow(state);
            var matches = combo.Items.Cast<object>().Any(item => Matches(GetComboBoxItemText(combo, item), query));
            UpdateComboCreate(state, matches);
        }

        private static void ClearComboFilter(ComboSearchState state)
        {
            var combo = state.Combo;
            var hadQuery = state.Query.Length > 0;
            state.Query = string.Empty;
            var handler = GetSearchHandler(combo);
            if (handler != null)
            {
                if (hadQuery)
                {
                    handler(string.Empty);
                }

                return;
            }

            UpdateSelectedRow(state);
            try
            {
                if (combo.Items.Filter != null)
                {
                    combo.Items.Filter = null;
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static void UpdateSelectedRow(ComboSearchState state)
        {
            var combo = state.Combo;
            var selected = combo.SelectedItem;
            var row = selected == null ? null : combo.ItemContainerGenerator.ContainerFromItem(selected) as ComboBoxItem;
            var hide = row != null && state.Query.Length > 0 &&
                       !Matches(GetComboBoxItemText(combo, selected), state.Query);

            if (state.HiddenSelectedRow != null && (!hide || !ReferenceEquals(state.HiddenSelectedRow, row)))
            {
                state.HiddenSelectedRow.ClearValue(UIElement.VisibilityProperty);
                state.HiddenSelectedRow = null;
            }

            if (hide && state.HiddenSelectedRow == null)
            {
                row.Visibility = Visibility.Collapsed;
                state.HiddenSelectedRow = row;
            }
        }

        private static void UpdateComboCreate(ComboSearchState state, bool matches)
        {
            if (state.CreateButton == null)
            {
                return;
            }

            var query = state.Box.Text?.Trim() ?? string.Empty;
            var show = state.IsActive && !matches && query.Length > 0 && GetCreateAction(state.Combo) != null;
            state.CreateButton.Content = show ? FormatCreateText(query) : null;
            state.CreateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void RunComboCreate(ComboSearchState state)
        {
            var action = GetCreateAction(state.Combo);
            var query = state.Box.Text?.Trim();
            if (action == null || string.IsNullOrEmpty(query))
            {
                return;
            }

            state.Combo.IsDropDownOpen = false;
            action(query);
        }

        private static void ComboSearchBox_PreviewKeyDown(ComboSearchState state, KeyEventArgs e)
        {
            var combo = state.Combo;
            switch (e.Key)
            {
                case Key.Escape when !string.IsNullOrEmpty(state.Box.Text):
                    state.Box.Text = string.Empty;
                    e.Handled = true;
                    break;

                case Key.Enter:
                {
                    var query = state.Box.Text?.Trim() ?? string.Empty;
                    // The first match, not the first row: the picked option stays in the list,
                    // hidden, whether it matches or not.
                    var firstMatch = query.Length == 0
                        ? null
                        : combo.Items.Cast<object>()
                            .FirstOrDefault(item => !(item is Separator) && Matches(GetComboBoxItemText(combo, item), query));
                    if (firstMatch != null)
                    {
                        combo.SelectedItem = firstMatch;
                        combo.IsDropDownOpen = false;
                    }
                    else if (state.CreateButton?.Visibility == Visibility.Visible)
                    {
                        RunComboCreate(state);
                    }

                    e.Handled = true;
                    break;
                }

                case Key.Down:
                {
                    // Into the list, at the first option the search shows.
                    var first = combo.Items.Cast<object>()
                        .Select(item => combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem)
                        .FirstOrDefault(container => container != null && container.Visibility == Visibility.Visible);
                    if (first != null)
                    {
                        first.Focus();
                        e.Handled = true;
                    }

                    break;
                }
            }
        }

        #endregion

        #region Menus

        private sealed class MenuSearchState
        {
            public ItemsControl Level;
            public MenuItem SearchItem;
            public TextBox Box;
            public MenuItem CreateItem;
            public readonly Dictionary<UIElement, Visibility> OriginalVisibility = new Dictionary<UIElement, Visibility>();
        }

        private static void OnContextMenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu && ReferenceEquals(e.OriginalSource, menu) &&
                !GetIsDisabled(menu) && IsPluginMenu(menu))
            {
                AttachMenuLevel(menu);
            }
        }

        private static void OnContextMenuClosed(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu && ReferenceEquals(e.OriginalSource, menu))
            {
                // Every level, not just the root: a submenu still open when the menu closes may
                // not report its own close, and a menu declared once is opened again.
                DetachMenuTree(menu);
            }
        }

        private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && ReferenceEquals(e.OriginalSource, item) &&
                FindRootContextMenu(item) is ContextMenu root && !GetIsDisabled(root) && !GetIsDisabled(item) &&
                IsPluginMenu(root))
            {
                AttachMenuLevel(item);
            }
        }

        private static void OnSubmenuClosed(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item && ReferenceEquals(e.OriginalSource, item))
            {
                DetachMenuLevel(item);
            }
        }

        private static ContextMenu FindRootContextMenu(DependencyObject element)
        {
            var current = element;
            while (current != null)
            {
                if (current is ContextMenu menu)
                {
                    return menu;
                }

                current = LogicalTreeHelper.GetParent(current) ?? ItemsControl.ItemsControlFromItemContainer(current);
            }

            return null;
        }

        /// <summary>
        /// True for a menu opened from the plugin's own UI: its target, or something it sits in, is
        /// one of the plugin's types. Playnite's menus and other extensions' are left alone, even
        /// when they carry items the plugin added.
        /// </summary>
        private static bool IsPluginMenu(ContextMenu menu)
        {
            var assembly = typeof(SelectorSearch).Assembly;
            if (menu.GetType().Assembly == assembly)
            {
                return true;
            }

            DependencyObject current = menu.PlacementTarget;
            while (current != null)
            {
                if (current.GetType().Assembly == assembly)
                {
                    return true;
                }

                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }

            return false;
        }

        private static void AttachMenuLevel(ItemsControl level)
        {
            if (level.GetValue(MenuStateProperty) != null || level.ItemsSource != null)
            {
                return;
            }

            var options = level.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).ToList();

            // Only where the hierarchy ends: a level that opens further submenus is navigation,
            // and its own search would hide the way to them.
            if (options.Any(item => item.HasItems) ||
                (options.Count <= MaxUnsearchedOptions && GetCreateAction(level) == null))
            {
                return;
            }

            var state = new MenuSearchState { Level = level };
            state.Box = new TextBox
            {
                MinWidth = 160,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var placeholder = new TextBlock
            {
                Text = PlaceholderText(GetCreateAction(level) != null),
                Margin = new Thickness(10, 0, 0, 2),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            placeholder.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            placeholder.SetResourceReference(UIElement.OpacityProperty, "PlayAch.Opacity.Placeholder");
            placeholder.SetBinding(
                UIElement.VisibilityProperty,
                new Binding(nameof(TextBox.Text))
                {
                    Source = state.Box,
                    Converter = EmptyToVisibleConverter.Instance
                });

            placeholder.VerticalAlignment = VerticalAlignment.Top;
            AlignPlaceholderToCaret(state.Box, placeholder);

            var host = new Grid();
            host.Children.Add(state.Box);
            host.Children.Add(placeholder);

            // The plugin's menu templates pin the box above the scrolling options, so it stays in
            // view however far the list scrolls. Any other menu gets it as its first row.
            if (level.Template?.FindName(SearchHeaderPartName, level) is FrameworkElement)
            {
                host.Margin = new Thickness(2, 0, 2, 4);
                SetSearchHeader(level, host);
            }
            else
            {
                // A bare presenter: the header is the box, without the highlight, check and
                // arrow columns of an option.
                state.SearchItem = new MenuItem
                {
                    Header = host,
                    Tag = InjectedTag,
                    StaysOpenOnClick = true,
                    Template = CreateBareTemplate()
                };
                state.SearchItem.GotKeyboardFocus += (_, args) =>
                {
                    if (ReferenceEquals(args.NewFocus, state.SearchItem))
                    {
                        Keyboard.Focus(state.Box);
                    }
                };
            }

            state.CreateItem = new MenuItem
            {
                Tag = InjectedTag,
                Visibility = Visibility.Collapsed
            };
            state.CreateItem.Click += (_, __) => RunMenuCreate(state);

            foreach (var option in level.Items.OfType<UIElement>())
            {
                state.OriginalVisibility[option] = option.Visibility;
            }

            level.SetValue(MenuStateProperty, state);
            if (state.SearchItem != null)
            {
                level.Items.Insert(0, state.SearchItem);
            }

            level.Items.Insert(state.SearchItem != null ? 1 : 0, state.CreateItem);

            state.Box.TextChanged += (_, __) =>
            {
                ApplyMenuFilter(state);
                ScrollToSearchBox(state);
            };
            state.Box.PreviewKeyDown += (_, args) => MenuSearchBox_PreviewKeyDown(state, args);

            // A menu built once and opened again keeps its scroll offset, which would open it
            // with the search box scrolled out of view.
            level.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    ScrollToSearchBox(state);
                    // After the scroll is laid out: a virtualizing menu realizes the box only once
                    // its row is in view.
                    level.Dispatcher.BeginInvoke(
                        new Action(() => Keyboard.Focus(state.Box)),
                        DispatcherPriority.Input);
                }),
                DispatcherPriority.Input);
        }

        // Scrolls the options back to the first one. A box in the options is their first row; a
        // pinned box, or a row a virtualizing menu has not realized, leaves the scroller to be
        // found from the menu.
        private static void ScrollToSearchBox(MenuSearchState state)
        {
            DependencyObject current = state.SearchItem;
            while (current != null && !(current is ScrollViewer))
            {
                current = VisualTreeHelper.GetParent(current);
            }

            var scroller = current as ScrollViewer ?? FindDescendantScrollViewer(state.Level);
            scroller?.ScrollToTop();
        }

        private static ScrollViewer FindDescendantScrollViewer(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                // The search box scrolls its own text; the options' scroller is the other one.
                if (child is TextBoxBase)
                {
                    continue;
                }

                if (child is ScrollViewer scroller)
                {
                    return scroller;
                }

                var nested = FindDescendantScrollViewer(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private static ControlTemplate CreateBareTemplate()
        {
            // The header itself, not through ContentSource: that also takes HeaderTemplate, and a menu
            // style whose template renders a header as text printed the box's type name.
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(HeaderedItemsControl.HeaderProperty));
            presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(6, 4, 6, 4));
            return new ControlTemplate(typeof(MenuItem)) { VisualTree = presenter };
        }

        private static void DetachMenuTree(ItemsControl level)
        {
            DetachMenuLevel(level);
            if (level.ItemsSource != null)
            {
                return;
            }

            foreach (var child in level.Items.OfType<MenuItem>().Where(item => item.HasItems).ToList())
            {
                DetachMenuTree(child);
            }
        }

        private static void DetachMenuLevel(ItemsControl level)
        {
            if (!(level.GetValue(MenuStateProperty) is MenuSearchState state))
            {
                return;
            }

            level.ClearValue(MenuStateProperty);
            level.ClearValue(SearchHeaderProperty);
            if (state.SearchItem != null)
            {
                level.Items.Remove(state.SearchItem);
            }

            level.Items.Remove(state.CreateItem);
            foreach (var pair in state.OriginalVisibility)
            {
                pair.Key.Visibility = pair.Value;
            }
        }

        private static void ApplyMenuFilter(MenuSearchState state)
        {
            var query = state.Box.Text?.Trim() ?? string.Empty;
            var anyMatch = false;
            var matcher = query.Length == 0 ? null : GetMenuMatcher(state.Level)?.Invoke(query);
            foreach (var pair in state.OriginalVisibility)
            {
                var element = pair.Key;
                if (query.Length == 0)
                {
                    element.Visibility = pair.Value;
                    continue;
                }

                // Separators divide groups of a full list; a filtered one has no groups left.
                var match = pair.Value == Visibility.Visible &&
                            element is MenuItem item &&
                            (matcher != null
                                ? matcher(item)
                                : GetMenuItemTexts(item).Any(text => Matches(text, query)));
                element.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                anyMatch |= match;
            }

            var create = GetCreateAction(state.Level);
            var showCreate = create != null && query.Length > 0 && !anyMatch;
            state.CreateItem.Header = showCreate ? FormatCreateText(query) : null;
            state.CreateItem.Visibility = showCreate ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void RunMenuCreate(MenuSearchState state)
        {
            var action = GetCreateAction(state.Level);
            var query = state.Box.Text?.Trim();
            if (action == null || string.IsNullOrEmpty(query))
            {
                return;
            }

            CloseMenu(state.Level);
            action(query);
        }

        private static void CloseMenu(ItemsControl level)
        {
            if (FindRootContextMenu(level) is ContextMenu root)
            {
                root.IsOpen = false;
            }
        }

        private static void MenuSearchBox_PreviewKeyDown(MenuSearchState state, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape when !string.IsNullOrEmpty(state.Box.Text):
                    state.Box.Text = string.Empty;
                    e.Handled = true;
                    break;

                case Key.Enter:
                {
                    if (state.CreateItem.Visibility == Visibility.Visible)
                    {
                        RunMenuCreate(state);
                    }
                    else if (VisibleOptions(state).FirstOrDefault() is MenuItem first &&
                             !string.IsNullOrWhiteSpace(state.Box.Text))
                    {
                        Invoke(first);
                    }

                    e.Handled = true;
                    break;
                }

                case Key.Down:
                {
                    var first = state.CreateItem.Visibility == Visibility.Visible
                        ? state.CreateItem
                        : VisibleOptions(state).FirstOrDefault();
                    if (first != null)
                    {
                        first.Focus();
                        e.Handled = true;
                    }

                    break;
                }
            }
        }

        private static IEnumerable<MenuItem> VisibleOptions(MenuSearchState state) =>
            state.Level.Items.OfType<MenuItem>()
                .Where(item => !Equals(item.Tag, InjectedTag) && item.Visibility == Visibility.Visible && item.IsEnabled);

        /// <summary>Picks an option as a click would, toggling a checkable one first.</summary>
        private static void Invoke(MenuItem item)
        {
            if (item.IsCheckable)
            {
                item.IsChecked = !item.IsChecked;
            }

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            if (item.Command != null && item.Command.CanExecute(item.CommandParameter))
            {
                item.Command.Execute(item.CommandParameter);
            }

            if (!item.StaysOpenOnClick)
            {
                CloseMenu(item);
            }
        }

        private sealed class EmptyToVisibleConverter : IValueConverter
        {
            public static readonly EmptyToVisibleConverter Instance = new EmptyToVisibleConverter();

            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
                Binding.DoNothing;
        }

        #endregion
    }
}
