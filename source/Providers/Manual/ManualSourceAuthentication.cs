using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Providers.Manual
{
    internal static class ManualSourceAuthentication
    {
        /// <summary>
        /// Throws <see cref="ManualSourceAuthenticationException"/> unless the source is signed in.
        /// A source that needs no sign-in reports itself authenticated with no session.
        /// </summary>
        public static async Task EnsureAuthenticatedAsync(IManualSource source, CancellationToken ct)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.AuthSession != null)
            {
                var probeResult = await source.AuthSession.ProbeAuthStateAsync(ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested && probeResult?.Outcome == AuthOutcome.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (probeResult?.IsSuccess == true)
                {
                    return;
                }

                throw CreateException(source, probeResult);
            }

            if (source.IsAuthenticated)
            {
                return;
            }

            throw CreateException(source, probeResult: null);
        }

        public static ManualSourceAuthenticationException CreateException(
            IManualSource source,
            AuthProbeResult probeResult)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var messageKey = ResolveMessageKey(source, probeResult);
            return new ManualSourceAuthenticationException(
                source.SourceKey,
                messageKey,
                probeResult);
        }

        public static string ResolveLocalizedMessage(ManualSourceAuthenticationException exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var localized = ResourceProvider.GetString(exception.MessageKey);
            if (string.IsNullOrWhiteSpace(localized) || string.Equals(localized, exception.MessageKey, StringComparison.Ordinal))
            {
                return exception.Message;
            }

            return string.Equals(exception.MessageKey, SignInRequiredKey, StringComparison.Ordinal)
                ? string.Format(localized, ResourceProvider.GetString($"LOCPlayAch_Provider_{exception.SourceKey}"))
                : localized;
        }

        /// <summary>"Sign in to {0} in plugin settings first.", formatted with the source's platform name.</summary>
        internal const string SignInRequiredKey = "LOCPlayAch_ManualAchievements_SignInRequired";

        private static string ResolveMessageKey(IManualSource source, AuthProbeResult probeResult)
        {
            var sourceKey = source?.SourceKey?.Trim();
            if (string.Equals(sourceKey, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return SignInRequiredKey;
            }

            if (string.Equals(sourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                return probeResult?.Outcome == AuthOutcome.ProbeFailed
                    ? "LOCPlayAch_ManualAchievements_ExophaseAuthCheckFailed"
                    : SignInRequiredKey;
            }

            return "LOCPlayAch_Status_AuthRequired";
        }
    }

    internal sealed class ManualSourceAuthenticationException : Exception
    {
        public string SourceKey { get; }

        public string MessageKey { get; }

        public AuthProbeResult ProbeResult { get; }

        public ManualSourceAuthenticationException(
            string sourceKey,
            string messageKey,
            AuthProbeResult probeResult = null)
            : base(messageKey ?? "Manual source authentication required.")
        {
            SourceKey = sourceKey ?? string.Empty;
            MessageKey = string.IsNullOrWhiteSpace(messageKey)
                ? "LOCPlayAch_Status_AuthRequired"
                : messageKey;
            ProbeResult = probeResult;
        }
    }
}
