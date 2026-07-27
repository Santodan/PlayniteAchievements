using System;
using System.Net.Http;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Central construction point for HttpClient instances so timeouts are consistent
    /// across providers, plus a shared client for infrequent calls that carry all of
    /// their state on the request message instead of allocating a client per call.
    /// </summary>
    internal static class HttpClientFactory
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Shared client for calls whose headers live on the HttpRequestMessage.
        /// Never mutate DefaultRequestHeaders on this instance.
        /// </summary>
        public static HttpClient Shared { get; } = Create();

        public static HttpClient Create(TimeSpan? timeout = null)
        {
            return new HttpClient { Timeout = timeout ?? DefaultTimeout };
        }

        public static HttpClient Create(HttpMessageHandler handler, TimeSpan? timeout = null)
        {
            return new HttpClient(handler) { Timeout = timeout ?? DefaultTimeout };
        }
    }
}
