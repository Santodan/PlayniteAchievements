using Playnite.SDK;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>The status and body an in-page <c>fetch()</c> returned.</summary>
    internal sealed class InPageFetchResult
    {
        public InPageFetchResult(int status, string url, string body)
        {
            Status = status;
            Url = url ?? string.Empty;
            Body = body ?? string.Empty;
        }

        public int Status { get; }

        /// <summary>The address the response came from, after any redirects.</summary>
        public string Url { get; }

        public string Body { get; }

        public bool IsSuccess => Status >= 200 && Status < 300;
    }

    /// <summary>
    /// Reads a same-origin URL with <c>fetch()</c> from inside the page an offscreen view has
    /// loaded. The response comes back as a string: none of its scripts run and none of its
    /// images, frames or media load, so nothing is rendered. The view must already be on the
    /// URL's origin; the browser sends its own cookies and TLS fingerprint, which is what lets
    /// these reads pass a WAF that rejects the .NET HTTP stack. Cookies the response sets are
    /// kept in the browser's store, as after a navigation.
    /// </summary>
    internal static class InPageFetch
    {
        private const int PollMs = 250;
        private const string Pending = "__PENDING__";
        private const string Failed = "__ERR__";

        private static int _nextSlot;

        /// <summary>
        /// The response's status and body, or null when the script failed, the request errored
        /// or no answer arrived within <paramref name="timeoutMs"/>. Each call stores its result
        /// in its own window slot, since <c>EvaluateScriptAsync</c> does not await promises.
        /// </summary>
        public static async Task<InPageFetchResult> FetchAsync(
            IWebView view,
            string url,
            CancellationToken ct,
            int timeoutMs = 10000,
            string accept = null)
        {
            if (view == null || string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var slot = "__paFetch" + Interlocked.Increment(ref _nextSlot).ToString(CultureInfo.InvariantCulture);
            var headers = string.IsNullOrEmpty(accept) ? string.Empty : ",headers:{'Accept':" + JsString(accept) + "}";
            var kickoff =
                "(function(){try{window." + slot + "=undefined;" +
                "fetch(" + JsString(url) + ",{credentials:'include'" + headers + "})" +
                ".then(function(r){return r.text().then(function(t){window." + slot + "=r.status+'|'+encodeURIComponent(r.url||'')+'|'+t;});})" +
                ".catch(function(){window." + slot + "='" + Failed + "';});return true;}" +
                "catch(e){window." + slot + "='" + Failed + "';return false;}})()";

            try
            {
                await view.EvaluateScriptAsync(kickoff).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }

            var read = "(function(){var v=window." + slot + ";if(typeof v==='undefined'){return '" + Pending + "';}" +
                "window." + slot + "=undefined;return v;})()";
            var attempts = Math.Max(1, timeoutMs / PollMs);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(PollMs, ct).ConfigureAwait(false);

                string value;
                try
                {
                    var eval = await view.EvaluateScriptAsync(read).ConfigureAwait(false);
                    if (eval?.Success != true || eval.Result == null)
                    {
                        continue;
                    }

                    value = Convert.ToString(eval.Result, CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                    continue;
                }

                if (value == Pending)
                {
                    continue;
                }

                return Parse(value);
            }

            return null;
        }

        /// <summary>
        /// A stored "status|url|body" value as a result, the address percent-encoded so it holds no
        /// bar; null for a failed request.
        /// </summary>
        internal static InPageFetchResult Parse(string value)
        {
            if (string.IsNullOrEmpty(value) || value == Failed)
            {
                return null;
            }

            var bar = value.IndexOf('|');
            var urlBar = bar < 0 ? -1 : value.IndexOf('|', bar + 1);
            if (bar <= 0 || urlBar < 0 ||
                !int.TryParse(value.Substring(0, bar), NumberStyles.Integer, CultureInfo.InvariantCulture, out var status))
            {
                return null;
            }

            var url = Uri.UnescapeDataString(value.Substring(bar + 1, urlBar - bar - 1));
            return new InPageFetchResult(status, url, value.Substring(urlBar + 1));
        }

        /// <summary>A single-quoted JavaScript string literal for <paramref name="value"/>.</summary>
        internal static string JsString(string value)
        {
            var escaped = (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\u2028", "\\u2028")
                .Replace("\u2029", "\\u2029");
            return "'" + escaped + "'";
        }
    }
}
