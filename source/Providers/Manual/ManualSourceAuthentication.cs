using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Manual
{
    internal static class ManualSourceAuthentication
    {
        public static Task EnsureAuthenticatedIfRequiredAsync(
            IManualSource source,
            bool requireExophaseAuthentication,
            CancellationToken ct)
        {
            return EnsureAuthenticatedIfRequiredAsync(source, requireExophaseAuthentication, link: null, ct);
        }

        public static Task EnsureAuthenticatedIfRequiredAsync(
            IManualSource source,
            bool requireExophaseAuthentication,
            ManualAchievementLink link,
            CancellationToken ct)
        {
            if (!ShouldRequireAuthentication(source, requireExophaseAuthentication, link))
            {
                return Task.CompletedTask;
            }

            return EnsureAuthenticatedAsync(source, ct);
        }

        public static bool ShouldRequireAuthentication(
            IManualSource source,
            bool requireExophaseAuthentication,
            ManualAchievementLink link)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (string.Equals(source.SourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                if (link != null && link.AllowUnauthenticatedSchemaFetch != false)
                {
                    return false;
                }

                return requireExophaseAuthentication;
            }

            return true;
        }

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
            return string.IsNullOrWhiteSpace(localized) || string.Equals(localized, exception.MessageKey, StringComparison.Ordinal)
                ? exception.Message
                : localized;
        }

        private static string ResolveMessageKey(IManualSource source, AuthProbeResult probeResult)
        {
            var sourceKey = source?.SourceKey?.Trim();
            if (string.Equals(sourceKey, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return "LOCPlayAch_ManualAchievements_Schema_ApiKeyRequired";
            }

            if (string.Equals(sourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                return probeResult?.Outcome == AuthOutcome.ProbeFailed
                    ? "LOCPlayAch_ManualAchievements_ExophaseAuthCheckFailed"
                    : "LOCPlayAch_ManualAchievements_ExophaseAuthRequired";
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
