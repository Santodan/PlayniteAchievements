using System;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamWebApiTokenResolver : IRefreshAuthContextReceiver
    {
        private readonly ISessionManager _sessionManager;
        private readonly Func<CancellationToken, Task<SteamWebAuthSession>> _resolveSessionAsync;
        private readonly ILogger _logger;
        private RefreshAuthContext _authContext;

        public ISessionManager SessionManager => _sessionManager;

#if !TEST
        public SteamWebApiTokenResolver(
            SteamSessionManager sessionManager,
            ILogger logger)
        {
            if (sessionManager == null) throw new ArgumentNullException(nameof(sessionManager));

            _sessionManager = sessionManager;
            _resolveSessionAsync = sessionManager.ResolveWebAuthSessionAsync;
            _logger = logger;
        }
#endif

        internal SteamWebApiTokenResolver(
            ISessionManager sessionManager,
            Func<CancellationToken, Task<SteamWebAuthSession>> resolveSessionAsync,
            ILogger logger)
        {
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _resolveSessionAsync = resolveSessionAsync ?? throw new ArgumentNullException(nameof(resolveSessionAsync));
            _logger = logger;
        }

        public async Task<SteamWebApiTokenResolution> ResolveAsync(CancellationToken ct)
        {
            var scoped = TryResolveFromScopedAuthContext();
            if (scoped != null)
            {
                return scoped;
            }

            var session = await _resolveSessionAsync(ct).ConfigureAwait(false);
            if (session?.IsComplete == true)
            {
                return SteamWebApiTokenResolution.Success(
                    AuthProbeResult.AlreadyAuthenticated(session.SteamId64),
                    session.WebApiToken);
            }

            if (session?.IsTransientFailure == true)
            {
                return SteamWebApiTokenResolution.Fail(AuthProbeResult.Create(
                    session.TransientFailureOutcome.Value,
                    "LOCPlayAch_Common_NotAuthenticated",
                    userId: session.SteamId64));
            }

            if (!string.IsNullOrWhiteSpace(session?.SteamId64))
            {
                _logger?.Warn("[SteamAch] Steam session has a user ID, but the web API token could not be resolved.");
                return SteamWebApiTokenResolution.Fail(AuthProbeResult.Create(
                    AuthOutcome.ProbeFailed,
                    "LOCPlayAch_Common_NotAuthenticated",
                    userId: session.SteamId64));
            }

            return SteamWebApiTokenResolution.Fail(AuthProbeResult.NotAuthenticated());
        }

        public void BeginRefreshAuthContext(RefreshAuthContext context)
        {
            _authContext = context;
        }

        public void EndRefreshAuthContext(RefreshAuthContext context)
        {
            if (ReferenceEquals(_authContext, context))
            {
                _authContext = null;
            }
        }

        private SteamWebApiTokenResolution TryResolveFromScopedAuthContext()
        {
            var context = _authContext;
            if (context == null ||
                !context.IsProviderAuthenticated("Steam") ||
                !context.TryGetArtifact<SteamWebAuthSession>("Steam", out var session) ||
                session?.IsComplete != true)
            {
                return null;
            }

            return SteamWebApiTokenResolution.Success(
                context.GetProbeResult("Steam") ?? AuthProbeResult.AlreadyAuthenticated(session.SteamId64),
                session.WebApiToken);
        }
    }

    internal sealed class SteamWebApiTokenResolution
    {
        public AuthProbeResult ProbeResult { get; private set; }

        public string Token { get; private set; }

        public string UserId => ProbeResult?.UserId?.Trim();

        public bool IsSuccess =>
            ProbeResult?.IsSuccess == true &&
            !string.IsNullOrWhiteSpace(UserId) &&
            !string.IsNullOrWhiteSpace(Token);

        private SteamWebApiTokenResolution()
        {
        }

        public static SteamWebApiTokenResolution Success(AuthProbeResult probeResult, string token)
        {
            return new SteamWebApiTokenResolution
            {
                ProbeResult = probeResult,
                Token = token?.Trim()
            };
        }

        public static SteamWebApiTokenResolution Fail(AuthProbeResult probeResult)
        {
            return new SteamWebApiTokenResolution
            {
                ProbeResult = probeResult,
                Token = null
            };
        }
    }
}
