using System;
using System.Windows.Input;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using Playnite.SDK;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public class RefreshProgressViewModel : ObservableObject
    {
        private readonly RefreshRuntime _refreshService;
        private readonly ILogger _logger;
        private readonly Guid? _singleGameRefreshId;
        private readonly Action<Guid> _openSingleGameAction;

        private double _progressPercent;
        private string _progressMessage;
        private bool _isCompleted;
        private bool _completedSuccessfully;

        public bool IsRefreshing => _refreshService.IsRebuilding;

        public double ProgressPercent
        {
            get => _progressPercent;
            set => SetValue(ref _progressPercent, value);
        }

        public string ProgressMessage
        {
            get => _progressMessage;
            set => SetValue(ref _progressMessage, value);
        }

        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (SetValueAndReturn(ref _isCompleted, value))
                {
                    OnPropertyChanged(nameof(ShowInProgressButtons));
                    OnPropertyChanged(nameof(ShowCompleteButtons));
                    OnPropertyChanged(nameof(ShowOpenSingleGameButton));
                }
            }
        }

        public bool ShowInProgressButtons => !IsCompleted && IsRefreshing;
        public bool ShowCompleteButtons => IsCompleted;
        public bool ShowOpenSingleGameButton => IsCompleted &&
                                                _completedSuccessfully &&
                                                _singleGameRefreshId.HasValue &&
                                                _openSingleGameAction != null;

        public string RefreshRunningNote => ResourceProvider.GetString("LOCPlayAch_Progress_RefreshRunningNote");

        public string WindowTitle => ResourceProvider.GetString("LOCPlayAch_Title_Refresh");

        public ICommand HideCommand { get; }
        public RelayCommand CancelCommand { get; }
        public ICommand ContinueCommand { get; }
        public RelayCommand OpenSingleGameCommand { get; }

        public RefreshProgressViewModel(
            RefreshRuntime refreshRuntime,
            ILogger logger,
            Guid? singleGameRefreshId = null,
            Action<Guid> openSingleGameAction = null)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _logger = logger;
            _singleGameRefreshId = singleGameRefreshId;
            _openSingleGameAction = openSingleGameAction;

            HideCommand = new RelayCommand(_ => HideWindow());
            CancelCommand = new RelayCommand(_ => CancelRefresh(), _ => _refreshService.IsRebuilding);
            ContinueCommand = new RelayCommand(_ => Continue());
            OpenSingleGameCommand = new RelayCommand(_ => OpenSingleGame(), _ => ShowOpenSingleGameButton);

            IsCompleted = false;
            ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot());
        }

        public void OnProgress(ProgressReport report)
        {
            if (report == null) return;

            ApplyRefreshStatus(_refreshService.GetRefreshStatusSnapshot(report));
        }

        private void ApplyRefreshStatus(RefreshStatusSnapshot status)
        {
            if (status == null)
            {
                return;
            }

            ProgressPercent = status.ProgressPercent;
            ProgressMessage = status.Message ?? string.Empty;

            var completedSuccessfully = status.IsFinal && !status.IsCanceled;
            if (_completedSuccessfully != completedSuccessfully)
            {
                _completedSuccessfully = completedSuccessfully;
                OnPropertyChanged(nameof(ShowOpenSingleGameButton));
            }

            if (status.IsFinal || status.IsCanceled)
            {
                IsCompleted = true;
            }
            else if (status.IsRefreshing)
            {
                IsCompleted = false;
            }

            OnPropertyChanged(nameof(IsRefreshing));
            OnPropertyChanged(nameof(ShowInProgressButtons));
            OnPropertyChanged(nameof(ShowCompleteButtons));
            OnPropertyChanged(nameof(ShowOpenSingleGameButton));

            CancelCommand?.RaiseCanExecuteChanged();
            OpenSingleGameCommand?.RaiseCanExecuteChanged();
        }

        private void HideWindow()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelRefresh()
        {
            _logger?.Info($"CancelRefresh called, IsRebuilding={_refreshService.IsRebuilding}");
            _refreshService.CancelCurrentRebuild();
        }

        private void Continue()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void OpenSingleGame()
        {
            if (!ShowOpenSingleGameButton || !_singleGameRefreshId.HasValue)
            {
                return;
            }

            RequestClose?.Invoke(this, EventArgs.Empty);
            _openSingleGameAction?.Invoke(_singleGameRefreshId.Value);
        }

        public event EventHandler RequestClose;
    }
}





