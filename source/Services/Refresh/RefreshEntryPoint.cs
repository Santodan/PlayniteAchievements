using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.Refresh
{
    public sealed class RefreshExecutionPolicy
    {
        public bool ValidateAuthentication { get; set; }
        public bool UseProgressWindow { get; set; }
        public bool SwallowExceptions { get; set; }
        public Guid? ProgressSingleGameId { get; set; }
        public string ErrorLogMessage { get; set; }
        public Action<bool> OnRefreshCompleted { get; set; }

        /// <summary>
        /// External cancellation token to link with internal CTS.
        /// When provided, creates a linked token source so canceling this token
        /// will also cancel the refresh operation.
        /// </summary>
        public CancellationToken ExternalCancellationToken { get; set; } = CancellationToken.None;

        internal RefreshAuthContext AuthContext { get; set; }

        public static RefreshExecutionPolicy Default() => new RefreshExecutionPolicy();

        public static RefreshExecutionPolicy ProgressWindow(Guid? singleGameId = null)
        {
            return new RefreshExecutionPolicy
            {
                ValidateAuthentication = true,
                UseProgressWindow = true,
                SwallowExceptions = true,
                ProgressSingleGameId = singleGameId
            };
        }
    }

    public sealed class RefreshAuthContext
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, AuthProbeResult> _probeResults =
            new Dictionary<string, AuthProbeResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _probeDurationsMs =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _artifacts =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<IDataProvider> _authenticatedProviders = Array.Empty<IDataProvider>();

        public RefreshAuthContext(Guid operationId)
        {
            OperationId = operationId == Guid.Empty ? Guid.NewGuid() : operationId;
            CreatedUtc = DateTime.UtcNow;
        }

        public Guid OperationId { get; }

        public DateTime CreatedUtc { get; }

        /// <summary>
        /// Capability results gathered while filtering the auth-probe candidate set, carried
        /// forward so request resolution and plan building reuse them instead of re-running
        /// per-game capability checks.
        /// </summary>
        internal TargetSelectionCache TargetSelectionCache { get; set; }

        public IReadOnlyList<IDataProvider> AuthenticatedProviders
        {
            get
            {
                lock (_sync)
                {
                    return _authenticatedProviders.ToList();
                }
            }
        }

        public IReadOnlyDictionary<string, AuthProbeResult> ProbeResults
        {
            get
            {
                lock (_sync)
                {
                    return new Dictionary<string, AuthProbeResult>(_probeResults, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public IReadOnlyDictionary<string, long> ProbeDurationsMs
        {
            get
            {
                lock (_sync)
                {
                    return new Dictionary<string, long>(_probeDurationsMs, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public bool HasAuthenticatedProviders => AuthenticatedProviders.Count > 0;

        public void SetProbeResult(
            string providerKey,
            AuthProbeResult result,
            long elapsedMs,
            object artifact = null)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return;
            }

            var key = providerKey.Trim();
            lock (_sync)
            {
                _probeResults[key] = result ?? AuthProbeResult.NotAuthenticated();
                _probeDurationsMs[key] = Math.Max(0, elapsedMs);
                if (artifact != null)
                {
                    _artifacts[key] = artifact;
                }
            }
        }

        public void SetAuthenticatedProviders(IEnumerable<IDataProvider> providers)
        {
            lock (_sync)
            {
                _authenticatedProviders = providers?
                    .Where(provider => provider != null)
                    .ToList() ?? new List<IDataProvider>();
            }
        }

        public AuthProbeResult GetProbeResult(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            lock (_sync)
            {
                _probeResults.TryGetValue(providerKey.Trim(), out var result);
                return result;
            }
        }

        public bool IsProviderAuthenticated(string providerKey)
        {
            return GetProbeResult(providerKey)?.IsSuccess == true;
        }

        public bool TryGetArtifact<T>(string providerKey, out T artifact) where T : class
        {
            artifact = null;
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            lock (_sync)
            {
                if (_artifacts.TryGetValue(providerKey.Trim(), out var value) && value is T typed)
                {
                    artifact = typed;
                    return true;
                }
            }

            return false;
        }

        public static RefreshAuthContext FromAuthenticatedProviders(IEnumerable<IDataProvider> providers)
        {
            var context = new RefreshAuthContext(Guid.NewGuid());
            var materialized = providers?
                .Where(provider => provider != null)
                .ToList() ?? new List<IDataProvider>();

            foreach (var provider in materialized)
            {
                context.SetProbeResult(provider.ProviderKey, AuthProbeResult.AlreadyAuthenticated(), 0);
            }

            context.SetAuthenticatedProviders(materialized);
            return context;
        }
    }

    public interface IRefreshAuthContextReceiver
    {
        void BeginRefreshAuthContext(RefreshAuthContext context);

        void EndRefreshAuthContext(RefreshAuthContext context);
    }

    public interface IRefreshAuthArtifactSource
    {
        object GetRefreshAuthArtifact(AuthProbeResult probeResult);
    }

    /// <summary>
    /// Centralized orchestration for refresh entry points.
    /// </summary>
    public sealed class RefreshEntryPoint
    {
        private readonly RefreshRuntime _refreshService;
        private readonly ILogger _logger;
        private readonly Action<Func<Task>, Guid?> _runWithProgressWindow;

        /// <summary>
        /// Event raised when a refresh completes successfully.
        /// Argument is the list of game IDs that were specifically targeted in the refresh.
        /// For mode-based refreshes (Recent, Full, etc.), this list may be empty.
        /// </summary>
        public event Action<List<Guid>> RefreshCompleted;

        public RefreshEntryPoint(
            RefreshRuntime refreshRuntime,
            ILogger logger,
            Action<Func<Task>, Guid?> runWithProgressWindow = null)
        {
            _refreshService = refreshRuntime ?? throw new ArgumentNullException(nameof(refreshRuntime));
            _logger = logger;
            _runWithProgressWindow = runWithProgressWindow;
        }

        public async Task ExecuteAsync(RefreshRequest request, RefreshExecutionPolicy policy = null)
        {
            policy ??= RefreshExecutionPolicy.Default();
            var normalizedRequest = NormalizeRequest(request);

            if (policy.UseProgressWindow && _runWithProgressWindow != null)
            {
                var singleGameId = policy.ProgressSingleGameId ?? normalizedRequest.SingleGameId;
                _runWithProgressWindow(() => FullPipelineAsync(normalizedRequest, policy), singleGameId);
            }
            else
            {
                await FullPipelineAsync(normalizedRequest, policy).ConfigureAwait(false);
            }
        }

        private async Task FullPipelineAsync(
            RefreshRequest request,
            RefreshExecutionPolicy policy)
        {
            var authContext = policy.AuthContext;

            if (policy.ValidateAuthentication)
            {
                if (authContext == null)
                {
                    authContext = await _refreshService
                        .GetRefreshAuthContextOrShowDialogAsync(request, policy.ExternalCancellationToken)
                        .ConfigureAwait(false);
                }

                if (authContext == null || !authContext.HasAuthenticatedProviders)
                {
                    return;
                }
            }

            await ExecuteWithCallbackAsync(request, policy, authContext).ConfigureAwait(false);
        }

        private async Task ExecuteWithCallbackAsync(
            RefreshRequest request,
            RefreshExecutionPolicy policy,
            RefreshAuthContext authContext = null)
        {
            bool success = false;
            try
            {
                await ExecuteCoreAsync(request, policy, authContext).ConfigureAwait(false);
                success = true;
            }
            finally
            {
                policy?.OnRefreshCompleted?.Invoke(success);

                // Raise RefreshCompleted event for subscribers (e.g., tag syncing)
                if (success)
                {
                    try
                    {
                        // Get the actual refreshed game IDs from RefreshRuntime.
                        var gameIds = _refreshService.LastRefreshedGameIds;
                        RefreshCompleted?.Invoke(gameIds);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "RefreshCompleted event handler failed.");
                    }
                }
            }
        }

        private async Task ExecuteCoreAsync(
            RefreshRequest request,
            RefreshExecutionPolicy policy,
            RefreshAuthContext authContext = null)
        {
            try
            {
                await _refreshService.ExecuteRefreshAsync(
                    request,
                    authContext,
                    policy?.ExternalCancellationToken ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, policy?.ErrorLogMessage ?? "Refresh execution failed.");
                if (!((policy?.SwallowExceptions) ?? false))
                {
                    throw;
                }
            }
        }

        private static RefreshRequest NormalizeRequest(RefreshRequest request)
        {
            request ??= new RefreshRequest();

            var normalizedGameIds = request.GameIds?
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (normalizedGameIds?.Count > 0)
            {
                return new RefreshRequest
                {
                    GameIds = normalizedGameIds,
                    SingleGameId = request.SingleGameId,
                    Options = request.Options?.Clone()
                };
            }

            if (request.Mode.HasValue)
            {
                return new RefreshRequest
                {
                    Mode = request.Mode.Value,
                    SingleGameId = request.SingleGameId,
                    Options = request.Options?.Clone()
                };
            }

            if (!string.IsNullOrWhiteSpace(request.ModeKey))
            {
                return new RefreshRequest
                {
                    ModeKey = request.ModeKey.Trim(),
                    SingleGameId = request.SingleGameId,
                    Options = request.Options?.Clone()
                };
            }

            return new RefreshRequest
            {
                Mode = RefreshModeType.Recent,
                SingleGameId = request.SingleGameId,
                Options = request.Options?.Clone()
            };
        }
    }
}

