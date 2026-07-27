using Playnite.SDK;
using Playnite.SDK.Events; // Required for WebViewLoadingChangedEventArgs
using System;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    public static class WebViewExtensions
    {
        private const int DefaultNavigationTimeoutMs = 20000;

        /// <summary>
        /// Runs work against a temporary offscreen web view, marshalled onto the UI
        /// dispatcher. Awaits inside the callback resume on the dispatcher so the view
        /// is only touched from the UI thread; the view is disposed when work completes.
        /// </summary>
        public static async Task<T> WithOffscreenViewAsync<T>(this IPlayniteAPI api, Func<IWebView, Task<T>> work)
        {
            var operation = api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    return await work(view);
                }
            });

            var workTask = await operation.Task.ConfigureAwait(false);
            return await workTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Navigates a temporary offscreen view to a URL and returns the page text.
        /// </summary>
        public static Task<string> GetPageTextViaOffscreenViewAsync(
            this IPlayniteAPI api,
            string url,
            int timeoutMs = DefaultNavigationTimeoutMs)
        {
            return api.WithOffscreenViewAsync(async view =>
            {
                await view.NavigateAndWaitAsync(url, timeoutMs);
                return await view.GetPageTextAsync();
            });
        }

        /// <summary>
        /// Deletes cookies for the given domains via a temporary offscreen view,
        /// marshalled onto the UI dispatcher. Failures are logged at debug level.
        /// </summary>
        public static void DeleteDomainCookies(this IPlayniteAPI api, ILogger logger, string logPrefix, params string[] domains)
        {
            try
            {
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = api.WebViews.CreateOffscreenView())
                    {
                        foreach (var domain in domains)
                        {
                            view.DeleteDomainCookies(domain);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"{logPrefix} Failed to clear cookies.");
            }
        }

        public static async Task NavigateAndWaitAsync(this IWebView webView, string url, int timeoutMs = DefaultNavigationTimeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnLoadingChanged(object sender, WebViewLoadingChangedEventArgs e)
            {
                if (!e.IsLoading)
                {
                    webView.LoadingChanged -= OnLoadingChanged;
                    tcs.TrySetResult(true);
                }
            }

            webView.LoadingChanged += OnLoadingChanged;

            try
            {
                webView.Navigate(url);
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));

                if (completedTask != tcs.Task)
                {
                    throw new TimeoutException($"WebView navigation to '{url}' timed out after {timeoutMs}ms");
                }

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                webView.LoadingChanged -= OnLoadingChanged;
            }
        }
    }
}
