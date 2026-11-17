using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: overview section. Hosts the settings header, quick links to other
    /// settings tabs, the Achievement styles priority, the language selection, and the
    /// GitHub, Discord, and Ko-fi links.
    /// </summary>
    public partial class GeneralOverviewSection : UserControl, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly Action<string> _jumpToTab;
        private readonly ProgressDetailPriorityEditor _priorityEditor;

        public GeneralOverviewSection()
        {
            InitializeComponent();
        }

        internal GeneralOverviewSection(PlayniteAchievementsSettings settings, Action<string> jumpToTab)
            : this()
        {
            _jumpToTab = jumpToTab ?? throw new ArgumentNullException(nameof(jumpToTab));
            _priorityEditor = new ProgressDetailPriorityEditor(settings);
            PriorityEditorHost.Content = _priorityEditor;
        }

        public void Dispose()
        {
            _priorityEditor?.Dispose();
        }

        private void JumpToTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: string tabKey })
            {
                _jumpToTab?.Invoke(tabKey);
            }
        }

        private void OpenLink_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button { Tag: string url }))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, $"Failed to open link: {url}");
            }
        }
    }
}
