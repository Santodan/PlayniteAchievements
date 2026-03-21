using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    public partial class RefreshProgressControl : UserControl
    {
        private readonly RefreshProgressViewModel _viewModel;
        private readonly RefreshRuntime _refreshService;
        private readonly ILogger _logger;

        public RefreshProgressControl(
            RefreshRuntime refreshRuntime,
            ILogger logger,
            Guid? singleGameRefreshId = null,
            Action<Guid> openSingleGameAction = null)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _logger = logger;

            _viewModel = new RefreshProgressViewModel(
                refreshRuntime,
                logger,
                singleGameRefreshId,
                openSingleGameAction);
            DataContext = _viewModel;

            InitializeComponent();

            _viewModel.RequestClose += (s, e) => RequestClose?.Invoke(this, EventArgs.Empty);

            _refreshService.RebuildProgress += OnRebuildProgress;
            Unloaded += (s, e) => _refreshService.RebuildProgress -= OnRebuildProgress;
        }

        public string WindowTitle => _viewModel.WindowTitle;

        public event EventHandler RequestClose;

        private void OnRebuildProgress(object sender, ProgressReport report)
        {
            Dispatcher?.InvokeIfNeeded(() =>
            {
                try
                {
                    _viewModel.OnProgress(report);
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Progress UI update error: {ex.Message}");
                }
            });
        }
    }
}




