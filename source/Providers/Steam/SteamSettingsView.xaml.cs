using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Providers.ImportedGameMetadata;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;

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
        private bool _loadingSteamAccountState;

        public ObservableCollection<ImportedGameMetadataSourceOption> AvailableMetadataSources { get; } = new ObservableCollection<ImportedGameMetadataSourceOption>();
        public ObservableCollection<SteamAccountSettings> SteamAccounts { get; } = new ObservableCollection<SteamAccountSettings>();

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

        public static readonly DependencyProperty DefaultSteamAccountIdProperty =
            DependencyProperty.Register(
                nameof(DefaultSteamAccountId),
                typeof(string),
                typeof(SteamSettingsView),
                new PropertyMetadata(string.Empty, OnDefaultSteamAccountIdChanged));

        public string DefaultSteamAccountId
        {
            get => (string)GetValue(DefaultSteamAccountIdProperty);
            set => SetValue(DefaultSteamAccountIdProperty, value ?? string.Empty);
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
            SetAuthStatusVisualState(pending: true, success: false);
            WebAuthenticated = false;
            FullyConfigured = false;
            WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_NotChecked");
            ImportedGameMetadataSourceComboBox.ItemsSource = AvailableMetadataSources;
            DefaultSteamAccountComboBox.ItemsSource = SteamAccounts;
            RefreshAvailableMetadataSources();
            RefreshSteamAccountsState();
            _ = RefreshAuthStatusAsync();
        }

        private static void OnDefaultSteamAccountIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = d as SteamSettingsView;
            view?.ApplyDefaultSteamAccountSelection(e.NewValue as string);
        }

        private void ApplyDefaultSteamAccountSelection(string selectedAccountId)
        {
            if (_loadingSteamAccountState || _steamSettings == null)
            {
                return;
            }

            _steamSettings.DefaultSteamAccountId = selectedAccountId?.Trim() ?? string.Empty;
        }

        private void RefreshSteamAccountsState()
        {
            if (_steamSettings == null)
            {
                return;
            }

            _loadingSteamAccountState = true;
            try
            {
                SteamAccounts.Clear();
                foreach (var account in _steamSettings.SteamAccounts ?? new List<SteamAccountSettings>())
                {
                    if (account == null)
                    {
                        continue;
                    }

                    account.PropertyChanged -= SteamAccount_PropertyChanged;
                    account.PropertyChanged += SteamAccount_PropertyChanged;
                    SteamAccounts.Add(account);
                }

                DefaultSteamAccountId = _steamSettings.DefaultSteamAccountId;
            }
            finally
            {
                _loadingSteamAccountState = false;
            }
        }

        private void SteamAccount_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_loadingSteamAccountState)
            {
                return;
            }

            SyncSteamAccountsToSettings();
        }

        private void SyncSteamAccountsToSettings()
        {
            if (_steamSettings == null)
            {
                return;
            }

            _steamSettings.SteamAccounts = SteamAccounts.Select(account => account?.Clone()).Where(account => account != null).ToList();
            _steamSettings.DefaultSteamAccountId = DefaultSteamAccountId;
        }

        private async void AddSteamAccount_Click(object sender, RoutedEventArgs e)
        {
            if (_steamSettings == null)
            {
                return;
            }

            var selectedDefault = _steamSettings.GetDefaultAccount();
            var newAccount = selectedDefault != null
                ? selectedDefault.Clone()
                : new SteamAccountSettings();

            newAccount.AccountId = Guid.NewGuid().ToString("N");
            newAccount.DisplayName = BuildUniqueAccountDisplayName(newAccount.DisplayName);
            if (selectedDefault != null)
            {
                newAccount.SteamUserId = string.Empty;
                newAccount.SteamWebApiKey = string.Empty;
            }

            SteamAccounts.Add(newAccount);
            DefaultSteamAccountId = newAccount.AccountId;
            SyncSteamAccountsToSettings();

            // Immediately prompt login so the newly added account can be authenticated.
            // Pass the new account's ID so the probed userId is written only to that account.
            await AuthenticateSelectedSteamAccountAsync(newAccount.AccountId);
        }

        private void AddSteamApiKey_Click(object sender, RoutedEventArgs e)
        {
            if (_steamSettings == null)
            {
                return;
            }

            var accountBaseName = ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiKeyAccountDefaultName");
            var newAccount = new SteamAccountSettings
            {
                AccountId = Guid.NewGuid().ToString("N"),
                DisplayName = BuildUniqueAccountDisplayName(accountBaseName),
                SteamWebApiKey = string.Empty,
                SteamUserId = string.Empty
            };

            SteamAccounts.Add(newAccount);
            if (string.IsNullOrWhiteSpace(DefaultSteamAccountId) || SteamAccounts.Count == 1)
            {
                DefaultSteamAccountId = newAccount.AccountId;
            }
            SyncSteamAccountsToSettings();
        }

        private async Task<string> ResolveSteamUserIdInputAsync(string apiKey, string steamUserInput)
        {
            var normalizedInput = (steamUserInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedInput))
            {
                return string.Empty;
            }

            if (TryParseSteamId64(normalizedInput, out var steamId64))
            {
                return steamId64;
            }

            var vanity = ExtractVanityFromInput(normalizedInput);
            if (string.IsNullOrWhiteSpace(vanity))
            {
                _api.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Settings_Steam_AddApiKey_UserIdInvalid"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }

            var resolved = await ResolveSteamIdFromVanityAsync(apiKey, vanity).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            _api.Dialogs.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Settings_Steam_AddApiKey_UserIdResolveFailed"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        private static bool TryParseSteamId64(string input, out string steamId64)
        {
            steamId64 = null;
            var normalized = (input ?? string.Empty).Trim();
            if (normalized.Length == 17 && normalized.All(char.IsDigit))
            {
                steamId64 = normalized;
                return true;
            }

            var profileMatch = Regex.Match(normalized, @"steamcommunity\.com/profiles/(?<id>\d{17})", RegexOptions.IgnoreCase);
            if (profileMatch.Success)
            {
                steamId64 = profileMatch.Groups["id"].Value;
                return true;
            }

            return false;
        }

        private static string ExtractVanityFromInput(string input)
        {
            var normalized = (input ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var vanityUrlMatch = Regex.Match(normalized, @"steamcommunity\.com/id/(?<vanity>[A-Za-z0-9_\-\.]+)", RegexOptions.IgnoreCase);
            if (vanityUrlMatch.Success)
            {
                return vanityUrlMatch.Groups["vanity"].Value;
            }

            if (normalized.IndexOf("/", StringComparison.Ordinal) >= 0 || normalized.IndexOf("\\", StringComparison.Ordinal) >= 0)
            {
                return string.Empty;
            }

            return normalized;
        }

        private async Task<string> ResolveSteamIdFromVanityAsync(string apiKey, string vanity)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(vanity))
            {
                return string.Empty;
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var url = "https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key="
                        + Uri.EscapeDataString(apiKey.Trim())
                        + "&vanityurl=" + Uri.EscapeDataString(vanity.Trim());

                    using (var response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return string.Empty;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return string.Empty;
                        }

                        var steamIdMatch = Regex.Match(json, "\\\"steamid\\\"\\s*:\\s*\\\"(?<id>\\d{17})\\\"", RegexOptions.IgnoreCase);
                        return steamIdMatch.Success ? steamIdMatch.Groups["id"].Value : string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed resolving Steam ID from vanity URL during Add API key flow.");
                return string.Empty;
            }
        }

        private async Task<bool?> ValidateSteamApiKeyAsync(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    var url = "https://api.steampowered.com/ISteamWebAPIUtil/GetSupportedAPIList/v1/?key=" + Uri.EscapeDataString(apiKey.Trim());
                    using (var response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return false;
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return false;
                        }

                        return json.IndexOf("\"apilist\"", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
            }
            catch
            {
                // Network or transient failures should not block account creation.
                return null;
            }
        }

        private string PromptForSingleLineInput(string title, string hint, string defaultText)
        {
            var inputDialog = new TextInputDialog(hint ?? string.Empty, defaultText ?? string.Empty);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title ?? ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 520,
                    Height = 210
                });

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = _api?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
            }

            inputDialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return null;
            }

            return inputDialog.InputText;
        }

        private string BuildUniqueAccountDisplayName(string baseName)
        {
            var normalizedBaseName = string.IsNullOrWhiteSpace(baseName)
                ? ResourceProvider.GetString("LOCPlayAch_Settings_Steam_AccountDefaultName")
                : baseName.Trim();

            if (!SteamAccounts.Any(account => string.Equals(account?.DisplayName, normalizedBaseName, StringComparison.OrdinalIgnoreCase)))
            {
                return normalizedBaseName;
            }

            for (var index = 2; index < 200; index++)
            {
                var candidate = $"{normalizedBaseName} {index}";
                if (!SteamAccounts.Any(account => string.Equals(account?.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }

            return $"{normalizedBaseName} {Guid.NewGuid():N}";
        }

        private void RemoveSteamAccount_Click(object sender, RoutedEventArgs e)
        {
            if (_steamSettings == null || SteamAccounts.Count == 0)
            {
                return;
            }

            var accountId = (sender as FrameworkElement)?.Tag as string;
            var account = SteamAccounts.FirstOrDefault(item =>
                item != null &&
                string.Equals(item.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
            if (account == null)
            {
                return;
            }

            SteamAccounts.Remove(account);
            if (string.Equals(DefaultSteamAccountId, account.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                DefaultSteamAccountId = SteamAccounts.FirstOrDefault()?.AccountId ?? string.Empty;
            }

            SyncSteamAccountsToSettings();
            _ = RefreshAuthStatusAsync();
        }

        private void SteamAccountField_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loadingSteamAccountState)
            {
                return;
            }

            SyncSteamAccountsToSettings();
        }

        private void RefreshAvailableMetadataSources()
        {
            AvailableMetadataSources.Clear();

            foreach (var option in ImportedGameMetadataSourceCatalog.GetAvailableOptions(_api, Logger))
            {
                AvailableMetadataSources.Add(option);
            }

            if (_steamSettings == null)
            {
                return;
            }

            var normalizedSelectedId = ImportedGameMetadataSourceCatalog.NormalizeMetadataSourceId(
                _api,
                Logger,
                _steamSettings.ImportedGameMetadataSourceId);
            if (!string.Equals(_steamSettings.ImportedGameMetadataSourceId, normalizedSelectedId, StringComparison.OrdinalIgnoreCase))
            {
                _steamSettings.ImportedGameMetadataSourceId = normalizedSelectedId;
            }

            if (!AvailableMetadataSources.Any(option => string.Equals(option.Id, _steamSettings.ImportedGameMetadataSourceId, StringComparison.OrdinalIgnoreCase)))
            {
                _steamSettings.ImportedGameMetadataSourceId = string.Empty;
            }
        }

        public async Task RefreshAuthStatusAsync()
        {
            try
            {
                var result = await _sessionManager.ProbeAuthStateAsync(CancellationToken.None);
                UpdateAuthStatusFromResult(result);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Steam auth probe failed during settings refresh.");
                UpdateAuthStatusFromResult(AuthProbeResult.ProbeFailed());
            }
        }

        private void UpdateAuthStatusFromResult(AuthProbeResult result)
        {
            var authenticatedAccount = ResolveSessionAuthenticatedAccount(result);
            var hasWebAuth = authenticatedAccount != null;
            var hasManualApiConfiguration = HasManualApiConfiguration();

            if (_steamSettings != null &&
                !string.IsNullOrWhiteSpace(result?.UserId) &&
                !string.Equals(_steamSettings.SteamUserId, result.UserId.Trim(), StringComparison.Ordinal))
            {
                _steamSettings.SteamUserId = result.UserId.Trim();
            }

            WebAuthenticated = hasWebAuth;
            FullyConfigured = hasWebAuth;
            SetAuthStatusVisualState(pending: false, success: hasWebAuth);

            if (hasWebAuth)
            {
                WebAuthStatus = ResourceProvider.GetString("LOCPlayAch_Auth_Authenticated");
            }
            else
            {
                var localized = ResourceProvider.GetString(result.MessageKey);
                var notAuthenticatedText = string.IsNullOrWhiteSpace(localized) || string.Equals(localized, result.MessageKey, StringComparison.Ordinal)
                    ? ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated")
                    : localized;

                WebAuthStatus = hasManualApiConfiguration
                    ? $"{notAuthenticatedText} ({ResourceProvider.GetString("LOCPlayAch_Settings_Steam_ApiAuthenticated")})"
                    : notAuthenticatedText;
            }
        }

        private bool HasManualApiConfiguration()
        {
            var account = _steamSettings?.GetDefaultAccount();
            return account != null &&
                   !string.IsNullOrWhiteSpace(account.SteamUserId) &&
                   !string.IsNullOrWhiteSpace(account.SteamWebApiKey);
        }

        private SteamAccountSettings ResolveSessionAuthenticatedAccount(AuthProbeResult result)
        {
            if (result?.IsSuccess != true)
            {
                return null;
            }

            var probedUserId = result.UserId?.Trim();
            if (string.IsNullOrWhiteSpace(probedUserId))
            {
                return null;
            }

            return SteamAccounts.FirstOrDefault(account =>
                account != null &&
                string.Equals(account.SteamUserId?.Trim(), probedUserId, StringComparison.OrdinalIgnoreCase));
        }

        private async void LoginWeb_Click(object sender, RoutedEventArgs e)
        {
            await AuthenticateSelectedSteamAccountAsync(DefaultSteamAccountId);
        }

        private async Task AuthenticateSelectedSteamAccountAsync(string targetAccountId)
        {
            try
            {
                SetAuthBusy(true);
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, ct: CancellationToken.None);
                if (result.IsSuccess)
                {
                    var normalizedUserId = result.UserId?.Trim();
                    if (!string.IsNullOrWhiteSpace(normalizedUserId))
                    {
                        var existingAuthenticatedAccount = SteamAccounts.FirstOrDefault(a =>
                            string.Equals(a?.SteamUserId?.Trim(), normalizedUserId, StringComparison.OrdinalIgnoreCase));

                        if (existingAuthenticatedAccount != null)
                        {
                            // Keep authenticated account visible and prioritized.
                            var existingIndex = SteamAccounts.IndexOf(existingAuthenticatedAccount);
                            if (existingIndex > 0)
                            {
                                SteamAccounts.RemoveAt(existingIndex);
                                SteamAccounts.Insert(0, existingAuthenticatedAccount);
                            }

                            DefaultSteamAccountId = existingAuthenticatedAccount.AccountId;
                        }
                        else
                        {
                            var targetAccount = SteamAccounts.FirstOrDefault(a =>
                                string.Equals(a?.AccountId, targetAccountId, StringComparison.OrdinalIgnoreCase));

                            // If the selected target is blank or no longer present, create a dedicated
                            // authenticated session account and place it at the top of the list.
                            var canHydrateTarget = targetAccount != null && string.IsNullOrWhiteSpace(targetAccount.SteamUserId);
                            if (canHydrateTarget)
                            {
                                targetAccount.SteamUserId = normalizedUserId;

                                var targetIndex = SteamAccounts.IndexOf(targetAccount);
                                if (targetIndex > 0)
                                {
                                    SteamAccounts.RemoveAt(targetIndex);
                                    SteamAccounts.Insert(0, targetAccount);
                                }

                                DefaultSteamAccountId = targetAccount.AccountId;
                            }
                            else
                            {
                                var authenticatedAccount = new SteamAccountSettings
                                {
                                    AccountId = Guid.NewGuid().ToString("N"),
                                    DisplayName = BuildUniqueAccountDisplayName(ResourceProvider.GetString("LOCPlayAch_Settings_Steam_AccountDefaultName")),
                                    SteamUserId = normalizedUserId,
                                    SteamWebApiKey = string.Empty
                                };

                                SteamAccounts.Insert(0, authenticatedAccount);
                                DefaultSteamAccountId = authenticatedAccount.AccountId;
                            }
                        }

                        SyncSteamAccountsToSettings();
                    }

                    await RefreshAuthStatusAsync();
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    UpdateAuthStatusFromResult(result);
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
    }
}

