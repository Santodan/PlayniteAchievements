using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Modal editor for one custom provider (name, color, SVG icon). Nothing is persisted here;
    /// the caller reads the result and the view model's definition after the window closes.
    /// </summary>
    public partial class CustomProviderEditorDialog : UserControl
    {
        public CustomProviderEditorDialog(CustomProviderEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        public bool? DialogResult { get; private set; }

        public bool RequestDelete { get; private set; }

        public event EventHandler RequestClose;

        /// <summary>
        /// Shows the editor as a modal extension window and reports how it was closed.
        /// </summary>
        public static CustomProviderEditorResult Show(Window owner, CustomProviderEditorViewModel viewModel)
        {
            var view = new CustomProviderEditorDialog(viewModel);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_ProviderEditorTitle"),
                view,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 600,
                    Height = 360
                });

            WindowPlacementPersistenceService.Attach(window, "CustomProviderEditor");

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

            if (view.RequestDelete)
            {
                return CustomProviderEditorResult.Deleted;
            }

            return view.DialogResult == true
                ? CustomProviderEditorResult.Saved
                : CustomProviderEditorResult.Cancelled;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            // Commit a pending icon source edit before reading the definition.
            IconSourceTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (DataContext is CustomProviderEditorViewModel viewModel && !viewModel.CanConfirm)
            {
                return;
            }

            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            RequestDelete = true;
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Enter commits the icon source (starting the import) instead of triggering OK.
        /// </summary>
        private void IconSourceTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
