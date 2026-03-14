using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    public sealed class RefreshCoordinator
    {
        private readonly AchievementService _achievementService;
        private readonly ILogger _logger;
        private readonly Action<Func<Task>, Guid?> _runWithProgressWindow;
        private readonly ProviderRegistry _providerRegistry;

        /// <summary>
        /// Event raised when a refresh completes successfully.
        /// Argument is the list of game IDs that were specifically targeted in the refresh.
        /// For mode-based refreshes (Recent, Full, etc.), this list may be empty.
        /// </summary>
        public event Action<List<Guid>> RefreshCompleted;

        public RefreshCoordinator(
            AchievementService achievementService,
            ILogger logger,
            ProviderRegistry providerRegistry,
            Action<Func<Task>, Guid?> runWithProgressWindow = null)
        {
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _logger = logger;
            _providerRegistry = providerRegistry;
            _runWithProgressWindow = runWithProgressWindow;
        }

        public async Task ExecuteAsync(RefreshRequest request, RefreshExecutionPolicy policy = null)
        {
            policy ??= RefreshExecutionPolicy.Default();
            var normalizedRequest = NormalizeRequest(request);

            if (policy.UseProgressWindow && _runWithProgressWindow != null)
            {
                var singleGameId = policy.ProgressSingleGameId ?? normalizedRequest.SingleGameId;
                _runWithProgressWindow(() => ExecuteWithPrimingAndCallbackAsync(normalizedRequest, policy), singleGameId);
            }
            else
            {
                await ExecuteWithPrimingAndCallbackAsync(normalizedRequest, policy);
            }
        }

        private async Task ExecuteWithPrimingAndCallbackAsync(RefreshRequest request, RefreshExecutionPolicy policy)
        {
            bool success = false;
            try
            {
                await ExecuteWithPrimingAsync(request, policy);
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
                        // Get the actual refreshed game IDs from the achievement service
                        var gameIds = _achievementService.LastRefreshedGameIds;
                        RefreshCompleted?.Invoke(gameIds);
                    }
                    catch { }
                }
            }
        }

        private async Task ExecuteWithPrimingAsync(RefreshRequest request, RefreshExecutionPolicy policy)
        {
            if (policy.ValidateAuthentication)
            {
                await _providerRegistry.PrimeEnabledProvidersAsync();

                if (!_achievementService.ValidateCanStartRefresh())
                {
                    return;
                }
            }

            await ExecuteCoreAsync(request, policy);
        }

        private async Task ExecuteCoreAsync(RefreshRequest request, RefreshExecutionPolicy policy)
        {
            try
            {
                await _achievementService.ExecuteRefreshAsync(
                    request,
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
                    CustomOptions = request.CustomOptions
                };
            }

            if (request.Mode.HasValue)
            {
                return new RefreshRequest
                {
                    Mode = request.Mode.Value,
                    SingleGameId = request.SingleGameId,
                    CustomOptions = request.CustomOptions
                };
            }

            if (!string.IsNullOrWhiteSpace(request.ModeKey))
            {
                return new RefreshRequest
                {
                    ModeKey = request.ModeKey.Trim(),
                    SingleGameId = request.SingleGameId,
                    CustomOptions = request.CustomOptions
                };
            }

            return new RefreshRequest
            {
                Mode = RefreshModeType.Recent,
                SingleGameId = request.SingleGameId,
                CustomOptions = request.CustomOptions
            };
        }
    }
}
