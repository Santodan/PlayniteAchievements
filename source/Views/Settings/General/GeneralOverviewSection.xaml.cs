using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: overview section. Hosts the settings header, quick links to other
    /// settings tabs, and the language selection.
    /// </summary>
    public partial class GeneralOverviewSection : UserControl
    {
        private readonly Action<string> _jumpToTab;

        public GeneralOverviewSection()
        {
            InitializeComponent();
        }

        internal GeneralOverviewSection(Action<string> jumpToTab)
            : this()
        {
            _jumpToTab = jumpToTab ?? throw new ArgumentNullException(nameof(jumpToTab));
        }

        private void JumpToTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: string tabKey })
            {
                _jumpToTab?.Invoke(tabKey);
            }
        }
    }
}
