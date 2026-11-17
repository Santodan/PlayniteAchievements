using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// The Link File window. Apply stores the link, reads the file and closes; Cancel and the
    /// window's close button discard. The host reloads when
    /// <see cref="LinkedFileMappingViewModel.ChangedLink"/> is set.
    /// </summary>
    public partial class LinkedFileMappingDialog : UserControl
    {
        private readonly LinkedFileMappingViewModel _viewModel;

        public LinkedFileMappingDialog(LinkedFileMappingViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;
            _viewModel.ColumnsRebuilt += (s, e) => RebuildPreviewColumns();
            _viewModel.RequestClose += (s, e) => RequestClose?.Invoke(this, EventArgs.Empty);
            RebuildPreviewColumns();
        }

        public event EventHandler RequestClose;

        /// <summary>Shows the window modally and reports whether the link changed.</summary>
        public static bool Show(Window owner, LinkedFileMappingViewModel viewModel)
        {
            var view = new LinkedFileMappingDialog(viewModel);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString(viewModel.HasStoredLink ? "LOCPlayAch_LinkedFile_EditButton" : "LOCPlayAch_LinkedFile_LinkButton"),
                view,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 980,
                    Height = 760
                });

            WindowPlacementPersistenceService.Attach(window, "LinkedFileMapping");

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = owner ?? API.Instance?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
                // Ownerless windows still work; they just do not center on the parent.
            }

            view.RequestClose += (s, e) => window.Close();
            window.ShowDialog();
            return viewModel.ChangedLink;
        }

        private void RebuildPreviewColumns()
        {
            PreviewGrid.Columns.Clear();

            // The first column shows the achievement each row pairs with, as a dropdown that pairs
            // it by hand, and stays in view while the file's columns scroll.
            PreviewGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Column_Achievement"),
                CellTemplate = (DataTemplate)FindResource("MatchCellTemplate"),
                // A template of its own: the grid's header style hosts the field picker, which
                // belongs on the file's columns only.
                HeaderTemplate = (DataTemplate)FindResource("MatchColumnHeaderTemplate"),
                HeaderStyle = (Style)FindResource("MatchColumnHeaderStyle"),
                CellStyle = (Style)FindResource("MatchCellStyle"),
                Width = 240,
                MaxWidth = 300
            });

            var cellTextStyle = (Style)FindResource("PreviewCellTextStyle");
            var fieldHeaderTemplate = (DataTemplate)FindResource("FieldHeaderTemplate");
            foreach (var column in _viewModel.Columns)
            {
                PreviewGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = column,
                    HeaderTemplate = fieldHeaderTemplate,
                    Binding = new Binding("[" + column.Index + "]"),
                    ElementStyle = BuildCellTextStyle(cellTextStyle, column.Index),
                    MaxWidth = 420
                });
            }
        }

        /// <summary>
        /// The text style of one file column: a cell whose value cannot be read for its field
        /// shows in the error color, with the reason as its tooltip.
        /// </summary>
        private Style BuildCellTextStyle(Style baseStyle, int columnIndex)
        {
            var style = new Style(typeof(TextBlock), baseStyle);
            style.Setters.Add(new Setter(ToolTipProperty, new Binding("CellErrors[" + columnIndex + "]") { FallbackValue = null }));
            var failed = new DataTrigger
            {
                Binding = new Binding("FailedCells[" + columnIndex + "]") { FallbackValue = false },
                Value = true
            };
            failed.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("PlayAch.Brush.AuthError")));
            failed.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            style.Triggers.Add(failed);
            return style;
        }

        private void PresetActionsButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu
            {
                PlacementTarget = PresetActionsButton,
                Placement = PlacementMode.Bottom
            };
            menu.Items.Add(new MenuItem { Header = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_Presets_SaveAs"), Command = _viewModel.SaveAsCommand });
            menu.Items.Add(new MenuItem { Header = ResourceProvider.GetString("LOCPlayAch_Common_Rename"), Command = _viewModel.RenameSavedCommand });
            menu.Items.Add(new MenuItem { Header = ResourceProvider.GetString("LOCPlayAch_Common_Delete"), Command = _viewModel.DeleteSavedCommand });
            menu.IsOpen = true;
        }

        private void CommitOnEnter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (sender is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
            else if (sender is ComboBox comboBox)
            {
                comboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Applies an item picked from an editable combo at once. The combo sets its text after
        /// the selection event, so the binding updates on the next dispatcher turn.
        /// </summary>
        private void CommitOnSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox comboBox) || comboBox.SelectedItem == null || !comboBox.IsKeyboardFocusWithin && !comboBox.IsDropDownOpen)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
                comboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource()));
        }

        // The row whose dropdown a press outside it just closed, and when: a press on the row's
        // own cell closes the dropdown, and its release must not open it again.
        private LinkedFilePreviewRow _closedRow;
        private int _closedAt;

        /// <summary>Keeps the press from toggling the button; the dropdown opens on release.</summary>
        private void PickerButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
        }

        /// <summary>
        /// Opens the pairing dropdown under the cell. Opened on release rather than on press: the
        /// grid's cell handles every press, handled or not, by taking focus, and a dropdown opened
        /// during the press closes as soon as the cell does.
        /// </summary>
        private void PickerButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement cell) || !(cell.DataContext is LinkedFilePreviewRow row) || !row.CanPick)
            {
                return;
            }

            var closedByThisClick = ReferenceEquals(row, _closedRow) && unchecked(Environment.TickCount - _closedAt) < 1000;
            _closedRow = null;
            if (closedByThisClick)
            {
                return;
            }

            // Queued past the release, so nothing still handling this click sees the dropdown open.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PairingPopup.PlacementTarget = cell;
                PairingPopup.MinWidth = Math.Max(320, cell.ActualWidth);
                PairingPopup.DataContext = row;
                row.IsPickerOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void PairingPopup_Closed(object sender, EventArgs e)
        {
            // Only a press outside the dropdown closes it with the button down; a pick happens on release.
            _closedRow = Mouse.LeftButton == MouseButtonState.Pressed ? PairingPopup.DataContext as LinkedFilePreviewRow : null;
            _closedAt = Environment.TickCount;
        }

        private void PickerPopup_Opened(object sender, EventArgs e) => MatchPickerHandlers.PopupOpened(sender);

        private void PickerSearch_PreviewKeyDown(object sender, KeyEventArgs e) => MatchPickerHandlers.SearchPreviewKeyDown(sender, e);

        private void PickerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
            MatchPickerHandlers.ListPreviewMouseLeftButtonUp(sender, e);

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // Nothing in the window is stored until Apply, so closing discards it.
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
