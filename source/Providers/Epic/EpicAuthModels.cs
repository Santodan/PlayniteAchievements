using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Epic
{
    public enum EpicAuthOutcome
    {
        Authenticated,
        AlreadyAuthenticated,
        NotAuthenticated,
        Cancelled,
        TimedOut,
        Failed,
        ProbeFailed
    }

    public enum EpicAuthProgressStep
    {
        CheckingExistingSession,
        OpeningLoginWindow,
        WaitingForUserLogin,
        VerifyingSession,
        Completed,
        Failed
    }

    public enum EpicAuthFlow
    {
        AutoFallback,
        EmbeddedWindow,
        SystemBrowser
    }

    public sealed class EpicAuthResult
    {
        public EpicAuthOutcome Outcome { get; set; }

        public string MessageKey { get; set; }

        public string AccountId { get; set; }

        public bool WindowOpened { get; set; }

        public bool IsSuccess =>
            Outcome == EpicAuthOutcome.Authenticated ||
            Outcome == EpicAuthOutcome.AlreadyAuthenticated;

        public static EpicAuthResult Create(
            EpicAuthOutcome outcome,
            string messageKey,
            string accountId = null,
            bool windowOpened = false)
        {
            return new EpicAuthResult
            {
                Outcome = outcome,
                MessageKey = messageKey,
                AccountId = accountId,
                WindowOpened = windowOpened
            };
        }
    }

    public class EpicAuthRequiredException : Exception
    {
        public EpicAuthRequiredException(string message) : base(message) { }
    }

    /// <summary>
    /// Pure token-state predicates over the persisted Epic OAuth pair. The session manager reads
    /// the tokens from settings and delegates here so the rules stay testable without a Playnite
    /// host.
    /// </summary>
    public static class EpicSessionState
    {
        /// <summary>
        /// The access token is present and expires no sooner than <paramref name="accessBuffer"/>
        /// from <paramref name="nowUtc"/>.
        /// </summary>
        public static bool HasValidAccessToken(
            string accessToken,
            DateTime accessExpiryUtc,
            DateTime nowUtc,
            TimeSpan accessBuffer)
        {
            return !string.IsNullOrWhiteSpace(accessToken) &&
                   nowUtc < accessExpiryUtc - accessBuffer;
        }

        /// <summary>
        /// The refresh token is present and has not expired.
        /// </summary>
        public static bool HasValidRefreshToken(
            string refreshToken,
            DateTime refreshExpiryUtc,
            DateTime nowUtc)
        {
            return !string.IsNullOrWhiteSpace(refreshToken) &&
                   nowUtc < refreshExpiryUtc;
        }

        /// <summary>
        /// The session can be used without user interaction: either the access token is still
        /// valid or a refresh token can renew it. This is the <c>IsAuthenticated</c> snapshot;
        /// <c>ProbeAuthStateAsync</c> remains the authoritative check.
        /// </summary>
        public static bool IsUsable(
            string accessToken,
            DateTime accessExpiryUtc,
            string refreshToken,
            DateTime refreshExpiryUtc,
            DateTime nowUtc,
            TimeSpan accessBuffer)
        {
            return HasValidAccessToken(accessToken, accessExpiryUtc, nowUtc, accessBuffer) ||
                   HasValidRefreshToken(refreshToken, refreshExpiryUtc, nowUtc);
        }
    }
}
