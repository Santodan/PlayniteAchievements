using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    public sealed class LocalAchievementEditorViewModel : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Guid _gameId;
        private readonly ICacheManager _cacheManager;
        private readonly LocalSavesProvider _localProvider;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly List<ManualAchievementEditItem> _allAchievements = new List<ManualAchievementEditItem>();

        private string _gameName;
        private string _filePath;
        private string _fileKindText;
        private string _filterText;
        private string _statusMessage;
        private string _errorMessage;
        private bool _isBusy;

        public LocalAchievementEditorViewModel(
            Guid gameId,
            ICacheManager cacheManager,
            LocalSavesProvider localProvider,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _gameId = gameId;
            _cacheManager = cacheManager;
            _localProvider = localProvider;
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;

            FilteredAchievements = new ObservableCollection<ManualAchievementEditItem>();
            UnlockAllCommand = new PlayniteAchievements.Common.RelayCommand(_ => SetAllUnlocked(true), _ => !IsBusy && FilteredAchievements.Count > 0);
            LockAllCommand = new PlayniteAchievements.Common.RelayCommand(_ => SetAllUnlocked(false), _ => !IsBusy && FilteredAchievements.Count > 0);

            Load();
        }

        public string WindowTitle => string.IsNullOrWhiteSpace(GameName)
            ? "Edit Local Achievements"
            : $"{GameName} - Edit Local Achievements";

        public string GameName
        {
            get => _gameName;
            private set => SetValue(ref _gameName, value);
        }

        public string FilePath
        {
            get => _filePath;
            private set => SetValue(ref _filePath, value);
        }

        public string FileKindText
        {
            get => _fileKindText;
            private set => SetValue(ref _fileKindText, value);
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetValueAndReturn(ref _filterText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetValue(ref _statusMessage, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetValueAndReturn(ref _errorMessage, value))
                {
                    OnPropertyChanged(nameof(HasError));
                }
            }
        }

        public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetValueAndReturn(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanSave));
                    (UnlockAllCommand as PlayniteAchievements.Common.RelayCommand)?.RaiseCanExecuteChanged();
                    (LockAllCommand as PlayniteAchievements.Common.RelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        public bool CanSave => !IsBusy && _allAchievements.Count > 0 && !string.IsNullOrWhiteSpace(FilePath);

        public ObservableCollection<ManualAchievementEditItem> FilteredAchievements { get; }

        public ICommand UnlockAllCommand { get; }
        public ICommand LockAllCommand { get; }

        public void Load()
        {
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;
            FilePath = string.Empty;
            FileKindText = string.Empty;
            GameName = string.Empty;
            _allAchievements.Clear();
            FilteredAchievements.Clear();

            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                ErrorMessage = ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame");
                return;
            }

            GameName = game.Name;

            if (_localProvider == null)
            {
                ErrorMessage = "The Local provider is unavailable.";
                return;
            }

            if (!_localProvider.TryResolveWritableAchievementFilePath(game, out var writableFilePath, out var fileKind, out _, out _))
            {
                ErrorMessage = "This game does not use a writable Local achievements.json or achievements.ini file.";
                return;
            }

            FilePath = writableFilePath;
            FileKindText = fileKind == LocalSavesProvider.LocalAchievementFileKind.Json ? "achievements.json" : "achievements.ini";

            var cacheManager = _cacheManager as CacheManager;
            var gameData = cacheManager?.LoadGameData(_gameId.ToString()) ?? _cacheManager?.LoadGameData(_gameId.ToString());
            if (gameData?.Achievements == null || gameData.Achievements.Count == 0)
            {
                ErrorMessage = "No local achievements were found for this game.";
                _logger?.Warn($"Local achievement editor load found no cached achievements for gameId={_gameId}.");
                return;
            }

            foreach (var achievement in gameData.Achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var item = new ManualAchievementEditItem(
                    achievement,
                    achievement.Unlocked,
                    achievement.Unlocked ? achievement.UnlockTimeUtc : null,
                    _gameId)
                {
                    IsRevealed = true
                };

                item.SelectedTimeModeText = "24hr";
                _allAchievements.Add(item);
            }

            ApplyFilter();
            StatusMessage = $"Loaded {_allAchievements.Count} achievements from cache.";
            _logger?.Info($"Local achievement editor loaded gameId={_gameId} provider={gameData.ProviderKey ?? "unknown"} achievements={_allAchievements.Count} file={FilePath}");
            OnPropertyChanged(nameof(CanSave));
        }

        public async Task<bool> SaveAsync(CancellationToken token = default)
        {
            if (IsBusy)
            {
                return false;
            }

            if (_localProvider == null)
            {
                ErrorMessage = "The Local provider is unavailable.";
                return false;
            }

            var invalidTimeItem = _allAchievements.FirstOrDefault(item => item != null && item.IsUnlocked && item.HasUnlockTime && !item.IsValidTime);
            if (invalidTimeItem != null)
            {
                ErrorMessage = $"Invalid unlock time for '{invalidTimeItem.DisplayName}'.";
                return false;
            }

            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            if (game == null)
            {
                ErrorMessage = ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame");
                return false;
            }

            IsBusy = true;
            ErrorMessage = string.Empty;
            StatusMessage = string.Empty;

            try
            {
                var savePayload = BuildSavePayloadFromUiState();
                var unlockedCount = savePayload.Count(a => a != null && a.Unlocked);
                _logger?.Info($"Local achievement editor saving gameId={_gameId} achievements={savePayload.Count} unlocked={unlockedCount} file={FilePath}");
                var result = await _localProvider.SaveLocalAchievementsAsync(
                    game,
                    savePayload,
                    token).ConfigureAwait(false);

                if (!result.Success)
                {
                    ErrorMessage = result.Message ?? "Failed to save Local achievements.";
                    return false;
                }

                StatusMessage = result.Message;
                var cacheRefreshed = await RefreshLocalCacheAfterSaveAsync(game, token).ConfigureAwait(false);
                _cacheManager?.NotifyCacheInvalidated();
                _logger?.Info($"Local achievement editor save completed gameId={_gameId} updated={result.UpdatedCount} cacheRefreshed={cacheRefreshed} file={result.FilePath}");

                try
                {
                    PlayniteAchievementsPlugin.Instance?.ThemeUpdateService?.RequestUpdate(_gameId);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to request theme update after Local achievement save.");
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                ErrorMessage = "Save was canceled.";
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to save Local achievements for gameId={_gameId}");
                ErrorMessage = ex.Message;
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void SetAllUnlocked(bool unlocked)
        {
            foreach (var item in _allAchievements)
            {
                if (item == null)
                {
                    continue;
                }

                item.IsUnlocked = unlocked;
                if (unlocked)
                {
                    item.HasUnlockTime = true;
                }
            }

            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filter = (_filterText ?? string.Empty).Trim();
            var matching = string.IsNullOrWhiteSpace(filter)
                ? _allAchievements
                : _allAchievements.Where(item => MatchesFilter(item, filter)).ToList();

            FilteredAchievements.Clear();
            foreach (var item in matching)
            {
                FilteredAchievements.Add(item);
            }

            (UnlockAllCommand as PlayniteAchievements.Common.RelayCommand)?.RaiseCanExecuteChanged();
            (LockAllCommand as PlayniteAchievements.Common.RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSave));
        }

        private static bool MatchesFilter(ManualAchievementEditItem item, string filter)
        {
            if (item == null || string.IsNullOrWhiteSpace(filter))
            {
                return true;
            }

            return Contains(item.ApiName, filter) ||
                   Contains(item.DisplayName, filter) ||
                   Contains(item.Description, filter);
        }

        private static bool Contains(string value, string filter)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private List<AchievementDetail> BuildSavePayloadFromUiState()
        {
            var payload = new List<AchievementDetail>(_allAchievements.Count);
            foreach (var item in _allAchievements)
            {
                if (item?.Source == null)
                {
                    continue;
                }

                item.Source.Unlocked = item.IsUnlocked;
                item.Source.UnlockTimeUtc = item.IsUnlocked ? item.UnlockTime : null;
                payload.Add(item.Source);
            }

            return payload;
        }

        private async Task<bool> RefreshLocalCacheAfterSaveAsync(Game game, CancellationToken token)
        {
            try
            {
                var refreshedData = await _localProvider.GetAchievementsAsync(game, null).ConfigureAwait(false);
                if (refreshedData == null)
                {
                    _logger?.Warn($"Local achievement editor could not refresh Local data after save for gameId={_gameId}.");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(refreshedData.ProviderKey))
                {
                    refreshedData.ProviderKey = "Local";
                }

                var writeResult = _cacheManager?.SaveGameData(_gameId.ToString(), refreshedData);
                if (writeResult?.Success == true)
                {
                    return true;
                }

                _logger?.Warn($"Local achievement editor failed to persist refreshed cache for gameId={_gameId}: {writeResult?.ErrorMessage ?? "unknown cache write error"}");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Local achievement editor refresh-after-save failed for gameId={_gameId}");
                return false;
            }
        }
    }
}
