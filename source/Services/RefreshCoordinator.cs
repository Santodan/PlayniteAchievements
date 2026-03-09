using System;
using System.Linq;
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

        public Task ExecuteAsync(RefreshRequest request, RefreshExecutionPolicy policy = null)
        {
            policy ??= RefreshExecutionPolicy.Default();
            var normalizedRequest = NormalizeRequest(request);

            if (policy.ValidateAuthentication)
            {
                _providerRegistry.PrimeEnabledProvidersAsync().GetAwaiter().GetResult();

                if (!_achievementService.ValidateCanStartRefresh())
                {
                    return Task.CompletedTask;
                }
            }

            if (policy.UseProgressWindow && _runWithProgressWindow != null)
            {
                var singleGameId = policy.ProgressSingleGameId ?? normalizedRequest.SingleGameId;
                _runWithProgressWindow(() => ExecuteCoreAsync(normalizedRequest, policy), singleGameId);
                return Task.CompletedTask;
            }

            return ExecuteCoreAsync(normalizedRequest, policy);
        }

        private async Task ExecuteCoreAsync(RefreshRequest request, RefreshExecutionPolicy policy)
        {
            try
            {
                await _achievementService.ExecuteRefreshAsync(request).ConfigureAwait(false);
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
