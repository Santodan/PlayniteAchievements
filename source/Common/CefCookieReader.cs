using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Reads a site's cookies out of Playnite's shared CEF cookie store and renders them as an HTTP
    /// Cookie header. For sites whose credential is a cookie the user obtained in a browser context
    /// but whose data is fetched over <see cref="System.Net.Http.HttpClient"/>.
    ///
    /// Cookies are selected by domain, never by name, so a site that renames or adds cookies keeps
    /// working. Reads go through an offscreen view (no UI-thread affinity) and are marshalled with
    /// InvokeAsync rather than a blocking Invoke, so a busy UI thread cannot deadlock a caller.
    ///
    /// Callers log cookie names only. A cookie value is a working credential and must never reach a
    /// log file that ends up attached to a bug report.
    /// </summary>
    internal static class CefCookieReader
    {
        /// <summary>
        /// True when a CEF cookie domain belongs to the given site, ignoring the leading dot CEF
        /// stores inconsistently and matching subdomains.
        /// </summary>
        public static bool DomainMatches(string cookieDomain, string siteDomain)
        {
            if (string.IsNullOrWhiteSpace(cookieDomain) || string.IsNullOrWhiteSpace(siteDomain))
            {
                return false;
            }

            var domain = cookieDomain.Trim().TrimStart('.');
            var site = siteDomain.Trim().TrimStart('.');
            return domain.Equals(site, StringComparison.OrdinalIgnoreCase) ||
                   domain.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Every cookie belonging to the site, ordered by name so logs and headers are stable.
        /// </summary>
        public static List<HttpCookie> Filter(IEnumerable<HttpCookie> cookies, string siteDomain)
        {
            return (cookies ?? Enumerable.Empty<HttpCookie>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                .Where(c => DomainMatches(c.Domain, siteDomain))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Renders cookies as a Cookie header value. Returns null when nothing usable is present, so
        /// callers can tell "no cookies" from "an empty header". Values are stripped of the
        /// characters that would split or terminate the header; cookies with no value are dropped,
        /// since a valueless cookie carries no credential and would only mask the real state.
        /// </summary>
        public static string BuildCookieHeader(IEnumerable<HttpCookie> cookies)
        {
            var builder = new StringBuilder();
            foreach (var cookie in cookies ?? Enumerable.Empty<HttpCookie>())
            {
                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                {
                    continue;
                }

                var value = SanitizeValue(cookie.Value);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }

                builder.Append(cookie.Name.Trim()).Append('=').Append(value);
            }

            return builder.Length > 0 ? builder.ToString() : null;
        }

        /// <summary>
        /// Comma-separated cookie names, for diagnostics. Never includes values.
        /// </summary>
        public static string DescribeNames(IEnumerable<HttpCookie> cookies)
        {
            var names = (cookies ?? Enumerable.Empty<HttpCookie>())
                .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => c.Name.Trim())
                .ToList();

            return names.Count > 0 ? string.Join(",", names) : "<none>";
        }

        /// <summary>
        /// Reads the site's cookies from the shared CEF store through an offscreen view. Returns an
        /// empty list when the store holds none or the read fails; a caller cancellation propagates.
        /// </summary>
        public static async Task<List<HttpCookie>> ReadAsync(
            IPlayniteAPI api,
            OffscreenViewLeaseSource views,
            string siteDomain,
            ILogger logger,
            string logPrefix,
            CancellationToken ct)
        {
            if (api == null || views == null)
            {
                return new List<HttpCookie>();
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                var dispatcher = api.MainView?.UIDispatcher;
                if (dispatcher == null)
                {
                    return ReadThroughView(views, siteDomain);
                }

                var operation = dispatcher.InvokeAsync(() => ReadThroughView(views, siteDomain));
                return await operation.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"{logPrefix} Failed to read site cookies from the browser store.");
                return new List<HttpCookie>();
            }
        }

        private static List<HttpCookie> ReadThroughView(OffscreenViewLeaseSource views, string siteDomain)
        {
            var (view, owned) = views.AcquireView();
            try
            {
                return Filter(view.GetCookies(), siteDomain);
            }
            finally
            {
                views.ReleaseView(view, owned, faulted: false);
            }
        }

        private static string SanitizeValue(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (c == '\r' || c == '\n' || c == ';')
                {
                    continue;
                }

                builder.Append(c);
            }

            return builder.ToString().Trim();
        }
    }
}
