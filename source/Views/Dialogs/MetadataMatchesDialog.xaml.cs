using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Pairs a game's achievements with one metadata source's rows, one searchable dropdown per
    /// achievement. Hosted in a Playnite extension window by <see cref="Helpers.MetadataMatchesWindow"/>;
    /// nothing is written until Save.
    /// </summary>
    public partial class MetadataMatchesDialog : UserControl
    {
        public MetadataMatchesDialog()
        {
            InitializeComponent();
        }

        internal MetadataMatchesDialog(MetadataMatchesViewModel viewModel)
            : this()
        {
            DataContext = viewModel;
        }

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        /// <summary>Types straight into the search box when a dropdown opens.</summary>
        private void PickerPopup_Opened(object sender, EventArgs e)
        {
            var search = FindDescendant<TextBox>((sender as Popup)?.Child);
            if (search != null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    search.Focus();
                    Keyboard.Focus(search);
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        /// <summary>Enter picks the first row the search left; Escape closes the dropdown.</summary>
        private void PickerSearch_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MatchTargetItem target))
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                var first = target.Options.FirstOrDefault(option => !option.IsSpecial);
                if (first != null)
                {
                    target.Pick(first);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                target.IsPickerOpen = false;
                e.Handled = true;
            }
        }

        /// <summary>
        /// A click on a row picks it. Taken on mouse-up from the row the pointer is over, so a
        /// click on the scrollbar or between rows picks nothing.
        /// </summary>
        private void PickerList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var list = sender as ListBox;
            var item = list == null ? null : ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is MatchOption option && list.DataContext is MatchTargetItem target)
            {
                target.Pick(option);
                e.Handled = true;
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
