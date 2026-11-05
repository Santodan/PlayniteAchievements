using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// The Link File window. Settings are stored as they are entered, so closing is never a
    /// discard; the host reloads when <see cref="LinkedFileMappingViewModel.ChangedLink"/> is set.
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
                ResourceProvider.GetString("LOCPlayAch_LinkedFile_LinkButton"),
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
            foreach (var column in _viewModel.Columns)
            {
                PreviewGrid.Columns.Add(new DataGridTextColumn
                {
                    Header = column,
                    Binding = new Binding("[" + column.Index + "]"),
                    MaxWidth = 420
                });
            }
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
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // A text box still holding focus commits what was typed before the window goes.
            if (Keyboard.FocusedElement is TextBox focused)
            {
                focused.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
