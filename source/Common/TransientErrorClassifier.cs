using System;
using System.Net;
using System.Net.Http;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Canonical transient-error classification shared by all network providers.
    /// Providers layer their own exception types on top via the providerRules hook.
    /// </summary>
    public static class TransientErrorClassifier
    {
        /// <summary>
        /// 408 Request Timeout, 429 Too Many Requests, and all 5xx responses are retryable.
        /// </summary>
        public static bool IsTransientStatusCode(int statusCode)
        {
            return statusCode == 408 || statusCode == 429 || statusCode >= 500;
        }

        public static bool IsTransient(Exception ex)
        {
            return IsTransient(ex, null);
        }

        /// <summary>
        /// Walks the exception chain and classifies it as transient (retryable) or not.
        /// providerRules runs first on each exception in the chain; a non-null result wins,
        /// so provider-specific exception types and overrides take precedence over the
        /// canonical rules below.
        /// </summary>
        public static bool IsTransient(Exception ex, Func<Exception, bool?> providerRules)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                var providerVerdict = providerRules?.Invoke(current);
                if (providerVerdict.HasValue)
                {
                    return providerVerdict.Value;
                }

                if (current is OperationCanceledException)
                {
                    return false;
                }

                if (current is TimeoutException || current is HttpRequestException)
                {
                    return true;
                }

                if (current is WebException webEx)
                {
                    if (webEx.Response is HttpWebResponse response &&
                        IsTransientStatusCode((int)response.StatusCode))
                    {
                        return true;
                    }

                    if (webEx.Status == WebExceptionStatus.Timeout ||
                        webEx.Status == WebExceptionStatus.ConnectFailure ||
                        webEx.Status == WebExceptionStatus.ConnectionClosed ||
                        webEx.Status == WebExceptionStatus.KeepAliveFailure ||
                        webEx.Status == WebExceptionStatus.NameResolutionFailure)
                    {
                        return true;
                    }
                }

                if (MessageLooksTransient(current.Message))
                {
                    return true;
                }

                if (ReferenceEquals(current.InnerException, current))
                {
                    break;
                }
            }

            return false;
        }

        private static bool MessageLooksTransient(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (message.IndexOf("connection", StringComparison.OrdinalIgnoreCase) >= 0 &&
                message.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (message.IndexOf("temporarily", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (message.IndexOf("429", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }
    }
}
