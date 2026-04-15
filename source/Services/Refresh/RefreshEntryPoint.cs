using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Providers;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services
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
            IReadOnlyList<IDataProvider> authenticatedProviders = null;

            if (policy.ValidateAuthentication)
            {
                authenticatedProviders = await _refreshService
                    .GetAuthenticatedProvidersOrShowDialogAsync(policy.ExternalCancellationToken)
                    .ConfigureAwait(false);
                if (authenticatedProviders.Count == 0)
                {
                    return;
                }
            }

            await ExecuteWithCallbackAsync(request, policy, authenticatedProviders).ConfigureAwait(false);
        }

        private async Task ExecuteWithCallbackAsync(
            RefreshRequest request,
            RefreshExecutionPolicy policy,
            IReadOnlyList<IDataProvider> authenticatedProviders = null)
        {
            bool success = false;
            try
            {
                await ExecuteCoreAsync(request, policy, authenticatedProviders).ConfigureAwait(false);
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
                    catch { }
                }
            }
        }

        private async Task ExecuteCoreAsync(
            RefreshRequest request,
            RefreshExecutionPolicy policy,
            IReadOnlyList<IDataProvider> authenticatedProviders = null)
        {
            try
            {
                await _refreshService.ExecuteRefreshAsync(
                    request,
                    authenticatedProviders,
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
                    ForceIconRefresh = request.ForceIconRefresh,
                    CustomOptions = request.CustomOptions
                };
            }

            if (request.Mode.HasValue)
            {
                return new RefreshRequest
                {
                    Mode = request.Mode.Value,
                    SingleGameId = request.SingleGameId,
                    ForceIconRefresh = request.ForceIconRefresh,
                    CustomOptions = request.CustomOptions
                };
            }

            if (!string.IsNullOrWhiteSpace(request.ModeKey))
            {
                return new RefreshRequest
                {
                    ModeKey = request.ModeKey.Trim(),
                    SingleGameId = request.SingleGameId,
                    ForceIconRefresh = request.ForceIconRefresh,
                    CustomOptions = request.CustomOptions
                };
            }

            return new RefreshRequest
            {
                Mode = RefreshModeType.Recent,
                SingleGameId = request.SingleGameId,
                ForceIconRefresh = request.ForceIconRefresh,
                CustomOptions = request.CustomOptions
            };
        }
    }
}

