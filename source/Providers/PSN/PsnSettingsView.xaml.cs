using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.Windows.Navigation;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Providers.PSN
{
    public partial class PsnSettingsView : ProviderSettingsViewBase, IAuthRefreshable
    {
        private static readonly ILogger Logger = PluginLogger.GetLogger(nameof(PsnSettingsView));
        private readonly object _authOperationLock = new object();
        private readonly PsnSessionManager _sessionManager;

        private bool _authOperationAllowsBootstrap;
        private CancellationTokenSource _authOperationCancellationSource;
        private string _authOperationNpssoSnapshot;
        private PsnSettings _psnSettings;
        private Task<AuthProbeResult> _authOperationTask;
        private int _authStateVersion;

        public static readonly DependencyProperty AuthBusyProperty =
            DependencyProperty.Register(nameof(AuthBusy), typeof(bool), typeof(PsnSettingsView), new PropertyMetadata(false));
        public bool AuthBusy { get => (bool)GetValue(AuthBusyProperty); set => SetValue(AuthBusyProperty, value); }

        public static readonly DependencyProperty IsAuthenticatedProperty =
            DependencyProperty.Register(nameof(IsAuthenticated), typeof(bool), typeof(PsnSettingsView), new PropertyMetadata(false));
        public bool IsAuthenticated { get => (bool)GetValue(IsAuthenticatedProperty); set => SetValue(IsAuthenticatedProperty, value); }

        public static readonly DependencyProperty AuthStatusProperty =
            DependencyProperty.Register(nameof(AuthStatus), typeof(string), typeof(PsnSettingsView), new PropertyMetadata(string.Empty));
        public string AuthStatus { get => (string)GetValue(AuthStatusProperty); set => SetValue(AuthStatusProperty, value); }

        public new PsnSettings Settings => _psnSettings;

        public PsnSettingsView(PsnSessionManager sessionManager)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _authOperationTask = Task.FromResult(AuthProbeResult.NotAuthenticated());
            InitializeComponent();
            ConnectionLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderConnection"),
                ResourceProvider.GetString("LOCPlayAch_Provider_PSN"));
            AuthLabel.Text = string.Format(
                ResourceProvider.GetString("LOCPlayAch_Settings_ProviderAuth"),
                ResourceProvider.GetString("LOCPlayAch_Provider_PSN"));
        }

        public override void Initialize(IProviderSettings settings)
        {
            _psnSettings = settings as PsnSettings;
            base.Initialize(settings);
            SetAuthenticated(false);
            SetAuthStatusVisualState(pending: true, success: false);
            SetAuthStatusByKey("LOCPlayAch_Auth_NotChecked");
        }

        private void UpdateAuthStatus(AuthProbeResult result)
        {
            var isAuthenticated = result?.IsSuccess ?? false;
            SetAuthenticated(isAuthenticated);
            SetAuthStatusVisualState(pending: false, success: isAuthenticated);

            if (isAuthenticated)
            {
                SetAuthStatusByKey("LOCPlayAch_Auth_Authenticated");
                return;
            }

            if (result?.Outcome == AuthOutcome.TimedOut ||
                result?.Outcome == AuthOutcome.ProbeFailed ||
                result?.Outcome == AuthOutcome.Cancelled ||
                result?.Outcome == AuthOutcome.Failed)
            {
                SetAuthStatusByKey("LOCPlayAch_Auth_TemporaryFailure");
                return;
            }

            if (!string.IsNullOrWhiteSpace(result?.MessageKey))
            {
                SetAuthStatusByKey(result.MessageKey);
                return;
            }

            SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
        }

        public async Task RefreshAuthStatusAsync()
        {
            SetAuthStatusChecking();
            SetAuthStatusByKey("LOCPlayAch_Auth_Checking");

            var authStateVersion = Volatile.Read(ref _authStateVersion);
            try
            {
                var result = await QueueAuthOperationAsync(
                    explicitValidation: false,
                    triggerSource: "Settings.Refresh").ConfigureAwait(false);
                ApplyAuthResultIfCurrent(result, authStateVersion);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "PSN auth probe failed during settings refresh.");
                ApplyAuthResultIfCurrent(AuthProbeResult.ProbeFailed(), authStateVersion);
            }
        }

        private async void PsnNpsso_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                (sender as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                await CheckPsnAuthAsync("Settings.EnterKey").ConfigureAwait(false);
                _ = Dispatcher.BeginInvoke(new Action(() => MoveFocusFrom((TextBox)sender)));
            }
        }

        private async void PsnAuth_Check_Click(object sender, RoutedEventArgs e)
        {
            await CheckPsnAuthAsync("Settings.CheckButton").ConfigureAwait(false);
        }

        private async void PsnAuth_Login_Click(object sender, RoutedEventArgs e)
        {
            var authStateVersion = Interlocked.Increment(ref _authStateVersion);
            try
            {
                SetAuthBusy(true);
                SetAuthStatusByKey("LOCPlayAch_Auth_Checking");
                var result = await _sessionManager.AuthenticateInteractiveAsync(forceInteractive: true, CancellationToken.None);
                if (result.IsSuccess)
                {
                    await RefreshAuthStatusAsync();
                    PlayniteAchievementsPlugin.NotifySettingsSaved();
                }
                else
                {
                    ApplyAuthResultIfCurrent(result, authStateVersion);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN login failed");
                SetAuthStatusVisualState(pending: false, success: false);
                SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void PsnAuth_Clear_Click(object sender, RoutedEventArgs e)
        {
            Interlocked.Increment(ref _authStateVersion);
            try
            {
                SetAuthBusy(true);
                CancelQueuedAuthOperation();
                ClearCanonicalAuthInputs();
                _sessionManager.ClearSession();
                SetAuthenticated(false);
                SetAuthStatusVisualState(pending: false, success: false);
                SetAuthStatusByKey("LOCPlayAch_Common_NotAuthenticated");
                PlayniteAchievementsPlugin.NotifySettingsSaved();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN logout failed");
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private async Task CheckPsnAuthAsync(string triggerSource)
        {
            SetAuthBusy(true);
            SetAuthStatusChecking();
            SetAuthStatusByKey("LOCPlayAch_Auth_Checking");
            var authStateVersion = Volatile.Read(ref _authStateVersion);

            try
            {
                SyncCanonicalAuthInputs();
                var result = await QueueAuthOperationAsync(
                    explicitValidation: true,
                    triggerSource: triggerSource,
                    npssoSnapshot: GetCurrentNpssoSnapshot()).ConfigureAwait(false);
                ApplyAuthResultIfCurrent(result, authStateVersion);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "PSN auth check failed");
                ApplyAuthResultIfCurrent(AuthProbeResult.ProbeFailed(), authStateVersion);
            }
            finally
            {
                SetAuthBusy(false);
            }
        }

        private void SyncCanonicalAuthInputs()
        {
            if (_psnSettings == null)
            {
                return;
            }

            var liveSettings = ProviderRegistry.Settings<PsnSettings>();
            if (liveSettings == null)
            {
                return;
            }

            var npsso = _psnSettings.Npsso ?? string.Empty;
            if (string.Equals(liveSettings.Npsso ?? string.Empty, npsso, StringComparison.Ordinal))
            {
                return;
            }

            liveSettings.Npsso = npsso;
            ProviderRegistry.Write(liveSettings, persistToDisk: true);
        }

        private void ClearCanonicalAuthInputs()
        {
            if (_psnSettings != null)
            {
                _psnSettings.Npsso = string.Empty;
            }

            var liveSettings = ProviderRegistry.Settings<PsnSettings>();
            if (liveSettings == null)
            {
                return;
            }

            liveSettings.Npsso = string.Empty;
            ProviderRegistry.Write(liveSettings, persistToDisk: true);
        }

        private void SetAuthStatusByKey(string key)
        {
            var localized = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(localized, key, StringComparison.Ordinal))
            {
                localized = ResourceProvider.GetString("LOCPlayAch_Common_NotAuthenticated");
            }

            if (Dispatcher.CheckAccess())
            {
                AuthStatus = localized;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => AuthStatus = localized));
            }
        }

        private void SetAuthenticated(bool authenticated)
        {
            if (Dispatcher.CheckAccess())
            {
                IsAuthenticated = authenticated;
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(() => IsAuthenticated = authenticated));
            }
        }

        private void MoveFocusFrom(TextBox textBox)
        {
            var parent = textBox?.Parent as FrameworkElement;
            parent?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void SetAuthBusy(bool busy)
        {
            if (Dispatcher.CheckAccess()) AuthBusy = busy;
            else Dispatcher.BeginInvoke(new Action(() => AuthBusy = busy));
        }

        private void ApplyAuthResultIfCurrent(AuthProbeResult result, int authStateVersion)
        {
            if (authStateVersion != Volatile.Read(ref _authStateVersion))
            {
                return;
            }

            UpdateAuthStatus(result);
        }

        private Task<AuthProbeResult> QueueAuthOperationAsync(
            bool explicitValidation,
            string triggerSource,
            string npssoSnapshot = null)
        {
            lock (_authOperationLock)
            {
                var normalizedSnapshot = explicitValidation ? (npssoSnapshot ?? string.Empty) : null;
                if (_authOperationTask != null && !_authOperationTask.IsCompleted)
                {
                    if (!explicitValidation)
                    {
                        return _authOperationTask;
                    }

                    if (_authOperationAllowsBootstrap &&
                        string.Equals(_authOperationNpssoSnapshot ?? string.Empty, normalizedSnapshot, StringComparison.Ordinal))
                    {
                        return _authOperationTask;
                    }

                    var priorTask = _authOperationTask;
                    var cancellationToken = GetAuthOperationCancellationTokenLocked(reset: false);
                    _authOperationTask = ContinueWithExplicitValidationAsync(
                        priorTask,
                        triggerSource,
                        cancellationToken);
                    _authOperationAllowsBootstrap = true;
                    _authOperationNpssoSnapshot = normalizedSnapshot;
                    return _authOperationTask;
                }

                var operationCancellationToken = GetAuthOperationCancellationTokenLocked(reset: true);
                _authOperationAllowsBootstrap = explicitValidation;
                _authOperationNpssoSnapshot = normalizedSnapshot;
                _authOperationTask = RunAuthOperationCoreAsync(
                    explicitValidation,
                    triggerSource,
                    operationCancellationToken);
                return _authOperationTask;
            }
        }

        private async Task<AuthProbeResult> ContinueWithExplicitValidationAsync(
            Task<AuthProbeResult> priorTask,
            string triggerSource,
            CancellationToken cancellationToken)
        {
            try
            {
                await priorTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "PSN auth task failed before queued explicit validation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await RunAuthOperationCoreAsync(
                explicitValidation: true,
                triggerSource,
                cancellationToken).ConfigureAwait(false);
        }

        private Task<AuthProbeResult> RunAuthOperationCoreAsync(
            bool explicitValidation,
            string triggerSource,
            CancellationToken cancellationToken)
        {
            return explicitValidation
                ? _sessionManager.ValidateNpssoAsync(cancellationToken, triggerSource)
                : _sessionManager.ProbeAuthStateAsync(cancellationToken);
        }

        private void CancelQueuedAuthOperation()
        {
            CancellationTokenSource cancellationSource = null;
            Task authOperationTask = null;

            lock (_authOperationLock)
            {
                cancellationSource = _authOperationCancellationSource;
                authOperationTask = _authOperationTask;
                _authOperationCancellationSource = null;
                _authOperationAllowsBootstrap = false;
                _authOperationNpssoSnapshot = null;
                _authOperationTask = Task.FromResult(AuthProbeResult.NotAuthenticated());
            }

            if (cancellationSource == null)
            {
                return;
            }

            try
            {
                cancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            (authOperationTask ?? Task.CompletedTask).ContinueWith(
                _ => cancellationSource.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private CancellationToken GetAuthOperationCancellationTokenLocked(bool reset)
        {
            if (reset || _authOperationCancellationSource == null)
            {
                _authOperationCancellationSource?.Dispose();
                _authOperationCancellationSource = new CancellationTokenSource();
            }

            return _authOperationCancellationSource.Token;
        }

        private string GetCurrentNpssoSnapshot()
        {
            return (_psnSettings?.Npsso ?? string.Empty).Trim();
        }
    }
}

