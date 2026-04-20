using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Diagnostics;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Providers.ImportedGameMetadata;
using PlayniteAchievements.Views.Helpers;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Settings view for the Steam provider.
    /// </summary>
    public partial class SteamSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(SteamSettingsView));

        private readonly IPlayniteAPI _api;
        private readonly SteamSessionManager _sessionManager;
        private readonly SteamOwnedGamesImporter _ownedGamesImporter;
        private SteamSettings _steamSettings;
        private CancellationTokenSource _steamImportCts;

        public ObservableCollection<ImportedGameMetadataSourceOption> AvailableMetadataSources { get; } = new ObservableCollection<ImportedGameMetadataSourceOption>();

        #region DependencyProperties

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(
                nameof(AuthBusy),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool AuthBusy
        {
            get => (bool)GetValue(AuthBusyProperty);
            set => SetValue(AuthBusyProperty, value);
        }

        public static readonly DependencyProperty FullyConfiguredProperty =
            DependencyProperty.Register(
                nameof(FullyConfigured),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool FullyConfigured
        {
            get => (bool)GetValue(FullyConfiguredProperty);
            set => SetValue(FullyConfiguredProperty, value);
        }

        public static readonly DependencyProperty WebAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(WebAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool WebAuthenticated
        {
            get => (bool)GetValue(WebAuthenticatedProperty);
            set => SetValue(WebAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty ApiAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(ApiAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool ApiAuthenticated
        {
            get => (bool)GetValue(ApiAuthenticatedProperty);
            set => SetValue(ApiAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty AnyAuthenticatedProperty =
            DependencyProperty.Register(
                nameof(AnyAuthenticated),
                typeof(bool),
                typeof(SteamSettingsView),
                new PropertyMetadata(false));

        public bool AnyAuthenticated
        {
            get => (bool)GetValue(AnyAuthenticatedProperty);
            set => SetValue(AnyAuthenticatedProperty, value);
        }

        public static readonly DependencyProperty WebAuthStatusProperty =
            DependencyProperty.Register(
                nameof(WebAuthStatus),
                typeof(string),
                typeof(SteamSettingsView),
                new PropertyMetadata(
                    ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked")));

        public string WebAuthStatus
        {
            get => (string)GetValue(WebAuthStatusProperty);
            set => SetValue(WebAuthStatusProperty, value);
        }

        #endregion

        public new SteamSettings Settings => _steamSettings;

        public SteamSettingsView(SteamSessionManager sessionManager, IPlayniteAPI api)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _ownedGamesImporter = new SteamOwnedGamesImporter(_api, Logger, _sessionManager);
            InitializeComponent();
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_Steam"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _steamSettings = settings as SteamSettings;
            base.Initialize(settings);
            ImportedGameMetadataSourceComboBox.ItemsSource = AvailableMetadataSources;
            RefreshAvailableMetadataSources();

            if (_steamSettings is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged -= SteamSettings_PropertyChanged;
                notify.PropertyChanged += SteamSettings_PropertyChanged;
            }

            _ = RefreshAuthStatusAsync();
        }

        private void RefreshAvailableMetadataSources()
        {
            AvailableMetadataSources.Clear();

            foreach (var option in ImportedGameMetadataSourceCatalog.GetAvailableOptions(_api, Logger))
            {
                AvailableMetadataSources.Add(option);
            }
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                var apiResult = await _sessionManager.ProbeApiKeyAuthStateAsync(CancellationToken.None);
                var webResult = await _sessionManager.ProbeWebAuthStateAsync(CancellationToken.None);
                UpdateAuthStatusFromResult(apiResult, webResult);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Steam auth probe failed during settings refresh.");
                UpdateAuthStatusFromResult(AuthProbeResult.ProbeFailed(), AuthProbeResult.ProbeFailed());
            }
        }

        private void UpdateAuthStatusFromResult(AuthProbeResult apiResult, AuthProbeResult webResult)
        {
            var hasWebAuth = webResult?.IsSuccess == true;
            var hasApiAuth = apiResult?.IsSuccess == true;
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);
            var apiSteamUserId = !string.IsNullOrWhiteSpace(apiResult?.UserId)
                ? apiResult.UserId.Trim()
                : null;
            var webSteamUserId = hasWebAuth && !string.IsNullOrWhiteSpace(webResult?.UserId)
                ? webResult.UserId.Trim()
                : null;
            var probedSteamUserId = webSteamUserId ?? apiSteamUserId;

            if (_steamSettings != null && !string.Equals(_steamSettings.SteamUserId, probedSteamUserId, StringComparison.Ordinal))
            {
                _steamSettings.SteamUserId = probedSteamUserId;
            }

            WebAuthenticated = hasWebAuth;
            ApiAuthenticated = hasApiKey && hasApiAuth;
            AnyAuthenticated = hasApiAuth || hasWebAuth;
            FullyConfigured = hasApiKey && hasApiAuth;

            if (hasApiKey && hasApiAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiAuthenticated");
            }
            else if (hasWebAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_WebAuthOnly");
            }
            else if (hasApiKey && string.IsNullOrWhiteSpace(probedSteamUserId))
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiNeedsUserId");
            }
            else
            {
                var messageKey = apiResult?.MessageKey ?? webResult?.MessageKey;
                var localized = ResourceProvider.GetString(messageKey);
                WebAuthStatus = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, messageKey, StringComparison.Ordinal)
                    ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                    : localized;
            }
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, ct: CancellationToken.None);
                if (result.IsSuccess)
                {
                    await RefreshAuthStatusAsync();
                    await ImportOwnedGamesAsync(showDialog: true, ct: CancellationToken.None);
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    UpdateAuthStatusFromResult(result, AuthProbeResult.NotAuthenticated());
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam web login failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void SteamAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                await RefreshAuthStatusAsync();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam auth check failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void Logout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetAuthBusy(true);
                _sessionManager.ClearSession();
                await RefreshAuthStatusAsync();
                PlayniteAchievementsPlugin.NotifySettingsSaved();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async void ImportOwnedGames_Click(object sender, RoutedEventArgs e)
        {
            await ImportOwnedGamesAsync(showDialog: true, ct: CancellationToken.None);
        }

        private void SteamSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(SteamSettings.SteamApiKey))
            {
                UpdateConfiguredState();
            }
        }

        private void UpdateConfiguredState()
        {
            var hasApiKey = !string.IsNullOrWhiteSpace(_steamSettings?.SteamApiKey);
            var apiAuthenticated = hasApiKey && ApiAuthenticated;
            ApiAuthenticated = apiAuthenticated;
            FullyConfigured = hasApiKey && apiAuthenticated;

            if (hasApiKey && apiAuthenticated)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiAuthenticated");
            }
            else if (WebAuthenticated)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_WebAuthOnly");
            }
            else if (hasApiKey && string.IsNullOrWhiteSpace(_steamSettings?.SteamUserId))
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiNeedsUserId");
            }
            else
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }
        }

        private async void SteamApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            await RefreshAuthStatusAsync();
        }

        private async void SteamApiKey_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await RefreshAuthStatusAsync();
                MoveFocusFrom((TextBox)sender);
            }
        }

        private async Task ImportOwnedGamesAsync(bool showDialog, CancellationToken ct)
        {
            if (showDialog)
            {
                StartOwnedGamesImportWithProgressWindow();
                return;
            }

            try
            {
                SetAuthBusy(true);
                await _ownedGamesImporter.ImportOwnedGamesAsync(ct, null, _steamSettings).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Steam owned-games import failed");
                if (showDialog)
                {
                    _api.Dialogs.ShowMessage(
                        string.Format(
                            ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesFailed"),
                            ex.Message),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void StartOwnedGamesImportWithProgressWindow()
        {
            SetAuthBusy(true);
            _steamImportCts?.Dispose();
            _steamImportCts = new CancellationTokenSource();

            var progressControl = new LocalImportProgressControl
            {
                DialogTitle = "Importing Steam Games"
            };

            var window = PlayniteUiProvider.CreateExtensionWindow(
                "Import Steam Games",
                progressControl,
                new WindowOptions
                {
                    Width = 430,
                    Height = 250,
                    CanBeResizable = false,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            var settingsWindow = Window.GetWindow(this);
            if (settingsWindow != null)
            {
                window.Owner = settingsWindow;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            progressControl.RequestClose += (s, e) => window.Close();
            progressControl.CancelRequested += (s, e) => _steamImportCts?.Cancel();
            window.Closed += (s, e) =>
            {
                if (_steamImportCts != null && !_steamImportCts.IsCancellationRequested && progressControl.ShowCancelButton)
                {
                    _steamImportCts.Cancel();
                }
            };

            window.Show();

            var progress = new Progress<SteamOwnedGamesImporter.ImportProgressInfo>(info =>
            {
                if (info == null)
                {
                    return;
                }

                var percent = 0d;
                if (info.Current.HasValue && info.Max.HasValue && info.Max.Value > 0)
                {
                    percent = Math.Max(0d, Math.Min(100d, (info.Current.Value * 100d) / info.Max.Value));
                }

                progressControl.Update(percent, info.Text, info.IsIndeterminate ? "Working..." : string.Empty);
            });

            Task.Run(async () =>
            {
                try
                {
                    var result = await _ownedGamesImporter
                        .ImportOwnedGamesAsync(_steamImportCts.Token, progress, _steamSettings)
                        .ConfigureAwait(false);

                    var summary = BuildOwnedGamesImportSummaryText(result);
                    Dispatcher.Invoke(() =>
                    {
                        if (result?.WasCanceled == true)
                        {
                            progressControl.MarkCancelled(summary);
                        }
                        else
                        {
                            progressControl.MarkCompleted(summary);
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() => progressControl.MarkCancelled("Steam import cancelled."));
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Steam owned-games import failed");
                    Dispatcher.Invoke(() =>
                    {
                        progressControl.MarkFailed(
                            string.Format(
                                ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesFailed"),
                                ex.Message));
                    });
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        _steamImportCts?.Dispose();
                        _steamImportCts = null;
                        SetAuthBusy(false);
                    });
                }
            });
        }

        private static string BuildOwnedGamesImportSummaryText(SteamOwnedGamesImporter.ImportResult result)
        {
            if (result == null || !result.IsAuthenticated)
            {
                return ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesNotAuthenticated");
            }

            if (!result.HasSteamLibraryPlugin)
            {
                return ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesMissingLibraryPlugin");
            }

            if (result.OwnedCount <= 0)
            {
                return ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesNoneFound");
            }

            if (result.ImportedCount <= 0)
            {
                if (result.UpdatedCount > 0)
                {
                    return string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesUpdatedOnlySummary"),
                        result.UpdatedCount,
                        result.FailedCount);
                }

                return string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesAlreadyPresent"),
                    result.OwnedCount);
            }

            return result.UpdatedCount > 0
                ? string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesSummaryWithUpdates"),
                    result.ImportedCount,
                    result.UpdatedCount,
                    result.ExistingCount,
                    result.FailedCount)
                : string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ImportOwnedGamesSummary"),
                    result.ImportedCount,
                    result.ExistingCount,
                    result.FailedCount);
        }

        private static void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess())
            {
                AuthBusy = busy;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}

