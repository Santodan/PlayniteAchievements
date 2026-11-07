using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services
{
    internal static class GameplayRefreshDeferral
    {
        // No persistent timer. Only the scheduler waits; shutdown cancels the wait.
        internal static async Task WaitAsync(Func<bool> shouldDefer,
            Func<CancellationToken, Task> delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            while (shouldDefer())
            {
                await delay(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }
    }
}
