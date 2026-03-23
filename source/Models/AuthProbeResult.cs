using System;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Result of probing authentication state from the source of truth.
    /// This is returned by ProbeAuthStateAsync and EnsureAuthAsync.
    /// </summary>
    public sealed class AuthProbeResult
    {
        /// <summary>
        /// The outcome of the authentication probe.
        /// </summary>
        public AuthOutcome Outcome { get; private set; }

        /// <summary>
        /// Localization key for the result message.
        /// </summary>
        public string MessageKey { get; private set; }

        /// <summary>
        /// User ID if authenticated, null otherwise.
        /// </summary>
        public string UserId { get; private set; }

        /// <summary>
        /// Token expiry time if available.
        /// </summary>
        public DateTime? ExpiresUtc { get; private set; }

        /// <summary>
        /// Whether a login window was opened during this probe.
        /// </summary>
        public bool WindowOpened { get; private set; }

        /// <summary>
        /// True if the user is authenticated (AlreadyAuthenticated or Authenticated).
        /// </summary>
        public bool IsSuccess =>
            Outcome == AuthOutcome.AlreadyAuthenticated ||
            Outcome == AuthOutcome.Authenticated;

        private AuthProbeResult() { }

        /// <summary>
        /// Creates a successful probe result indicating the user is already authenticated.
        /// </summary>
        public static AuthProbeResult AlreadyAuthenticated(string userId = null, DateTime? expiresUtc = null)
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.AlreadyAuthenticated,
                MessageKey = "LOCPlayAch_Auth_AlreadyAuthenticated",
                UserId = userId,
                ExpiresUtc = expiresUtc,
                WindowOpened = false
            };
        }

        /// <summary>
        /// Creates a successful probe result indicating authentication was completed.
        /// </summary>
        public static AuthProbeResult Authenticated(string userId = null, DateTime? expiresUtc = null, bool windowOpened = false)
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.Authenticated,
                MessageKey = "LOCPlayAch_Auth_Authenticated",
                UserId = userId,
                ExpiresUtc = expiresUtc,
                WindowOpened = windowOpened
            };
        }

        /// <summary>
        /// Creates a probe result indicating the user is not authenticated.
        /// </summary>
        public static AuthProbeResult NotAuthenticated()
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.NotAuthenticated,
                MessageKey = "LOCPlayAch_Auth_NotAuthenticated",
                WindowOpened = false
            };
        }

        /// <summary>
        /// Creates a probe result indicating the authentication timed out.
        /// </summary>
        public static AuthProbeResult TimedOut(bool windowOpened = false)
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.TimedOut,
                MessageKey = "LOCPlayAch_Auth_TimedOut",
                WindowOpened = windowOpened
            };
        }

        /// <summary>
        /// Creates a probe result indicating the authentication was cancelled.
        /// </summary>
        public static AuthProbeResult Cancelled(bool windowOpened = false)
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.Cancelled,
                MessageKey = "LOCPlayAch_Auth_Cancelled",
                WindowOpened = windowOpened
            };
        }

        /// <summary>
        /// Creates a probe result indicating the authentication failed.
        /// </summary>
        public static AuthProbeResult Failed(bool windowOpened = false)
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.Failed,
                MessageKey = "LOCPlayAch_Auth_Failed",
                WindowOpened = windowOpened
            };
        }

        /// <summary>
        /// Creates a probe result indicating the probe itself failed.
        /// </summary>
        public static AuthProbeResult ProbeFailed()
        {
            return new AuthProbeResult
            {
                Outcome = AuthOutcome.ProbeFailed,
                MessageKey = "LOCPlayAch_Auth_ProbeFailed",
                WindowOpened = false
            };
        }

        /// <summary>
        /// Creates a probe result with a custom outcome and message.
        /// </summary>
        public static AuthProbeResult Create(
            AuthOutcome outcome,
            string messageKey,
            string userId = null,
            DateTime? expiresUtc = null,
            bool windowOpened = false)
        {
            return new AuthProbeResult
            {
                Outcome = outcome,
                MessageKey = messageKey,
                UserId = userId,
                ExpiresUtc = expiresUtc,
                WindowOpened = windowOpened
            };
        }
    }
}
