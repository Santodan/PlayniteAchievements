using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsViewModel
    {
        public IReadOnlyList<NativeSourceOption> NativeSourceOptions => NativeSourceCatalog.Sources;
        private string _nativeSourceKey = "auto", _nativeInstallPath = "", _nativeSavePath = "", _nativeSchemaPath = "", _nativeProfile = "", _nativeInspection = "Select a source to see expected files. Inspect reads the selected files without modifying them.";
        public string NativeSourceKey { get => _nativeSourceKey; set { SetValue(ref _nativeSourceKey, value); OnPropertyChanged(nameof(NativeSourceFileHint)); } }
        public string NativeSourceFileHint => NativeSourceCatalog.Find(NativeSourceKey)?.Files ?? "";
        public string NativeInstallPath { get => _nativeInstallPath; set => SetValue(ref _nativeInstallPath, value); }
        public string NativeSavePath { get => _nativeSavePath; set => SetValue(ref _nativeSavePath, value); }
        public string NativeSchemaPath { get => _nativeSchemaPath; set => SetValue(ref _nativeSchemaPath, value); }
        public string NativeProfile { get => _nativeProfile; set => SetValue(ref _nativeProfile, value); }
        private string _nativeUserId = "";
        public string NativeUserId { get => _nativeUserId; set => SetValue(ref _nativeUserId, value); }
        private string _nativeCollectionGame = "";
        public string NativeCollectionGame { get => _nativeCollectionGame; set => SetValue(ref _nativeCollectionGame, value); }
        public string NativeInspection { get => _nativeInspection; set => SetValue(ref _nativeInspection, value); }
        private NativeSourceConfiguration NativeDraft() => new NativeSourceConfiguration { Source = NativeSourceKey ?? "auto", InstallPath = NativeInstallPath?.Trim(), SavePath = NativeSavePath?.Trim(), SchemaPath = NativeSchemaPath?.Trim(), Profile = NativeProfile?.Trim(), CollectionGame = NativeCollectionGame?.Trim().ToLowerInvariant(), UserId = NativeUserId?.Trim() };
        private void LoadNativeOverride()
        {
            ++_nativeInspectionGeneration;
            var settings = ProviderRegistry.Settings<LocalSettings>();
            NativeSourceConfiguration config = null;
            settings?.NativeSourceOverrides?.TryGetValue(_gameId, out config);
            config = config ?? new NativeSourceConfiguration();
            NativeSourceKey = config.Source; NativeInstallPath = config.InstallPath; NativeSavePath = config.SavePath;
            NativeSchemaPath = config.SchemaPath; NativeProfile = config.Profile;
            NativeCollectionGame = config.CollectionGame;
            NativeUserId = config.UserId;
        }
        public RelayCommand BrowseNativeInstallCommand => new RelayCommand(_ =>
        {
            var path = _playniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(path)) NativeInstallPath = path;
        });
        public RelayCommand BrowseNativeSaveCommand => new RelayCommand(_ => BrowseNativeFile(false));
        public RelayCommand BrowseNativeSchemaCommand => new RelayCommand(_ => BrowseNativeFile(true));
        private void BrowseNativeFile(bool schema)
        {
            var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false, Filter = schema ? "Achievement definitions|*.json;*.xml;*.sfm;*.trp;*.ucp;*.tsv;*.txt;*.lng|All files|*.*" : "All files|*.*" };
            if (dialog.ShowDialog() == true) { if (schema) NativeSchemaPath = dialog.FileName; else NativeSavePath = dialog.FileName; }
        }
        private int _nativeInspectionGeneration;
        public RelayCommand InspectNativeSourceCommand => new RelayCommand(async _ =>
        {
            var provider = _refreshService?.Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            var draft = NativeDraft();
            var generation = ++_nativeInspectionGeneration;
            NativeInspection = "Reading native source files…";
            try
            {
                var message = await Task.Run(() => provider?.InspectNativeSource(game, draft) ?? "Local provider is unavailable.");
                if (generation == _nativeInspectionGeneration) NativeInspection = message;
            }
            catch (Exception ex) { if (generation == _nativeInspectionGeneration) NativeInspection = "Inspection failed: " + ex.Message; }
        });
        public RelayCommand ApplyNativeSourceCommand => new RelayCommand(_ =>
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings == null) return;
            settings.NativeSourceOverrides = new Dictionary<Guid, NativeSourceConfiguration>(settings.NativeSourceOverrides) { [_gameId] = NativeDraft() };
            ProviderRegistry.Write(settings);
            _persistSettingsForUi?.Invoke();
            EnsurePlatformOverrideIsLocal();
            // Keep the existing cache until a complete native read succeeds.
            NativeInspection = "Saved. Inspect to verify files; refresh reads this source. Missing or invalid files preserve cached data.";
            TriggerRefreshForProvider(LocalProviderKey);
            RefreshOverviewOverrides();
        });
        public RelayCommand ClearNativeSourceCommand => new RelayCommand(_ =>
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings == null) return;
            var copy = new Dictionary<Guid, NativeSourceConfiguration>(settings.NativeSourceOverrides);
            copy.Remove(_gameId); settings.NativeSourceOverrides = copy; ProviderRegistry.Write(settings);
            _persistSettingsForUi?.Invoke();
            LoadNativeOverride(); NativeInspection = "Override cleared. Automatic native detection uses the game's install directory.";
            RefreshOverviewOverrides();
        });
    }
}
