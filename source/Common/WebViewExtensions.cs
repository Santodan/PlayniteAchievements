using Playnite.SDK;
using Playnite.SDK.Events; // Required for WebViewLoadingChangedEventArgs
using System;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    public static class WebViewExtensions
    {
        private const int DefaultNavigationTimeoutMs = 20000;

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
