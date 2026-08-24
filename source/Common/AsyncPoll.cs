using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Polls an asynchronous value until it satisfies a predicate. Used where a browser view has to
    /// be watched for a state change the page reaches on its own schedule (a completed login, a
    /// cleared site check), which no event reliably announces.
    /// </summary>
    internal static class AsyncPoll
    {
        /// <summary>
        /// Evaluates <paramref name="valueFactory"/> until <paramref name="readyPredicate"/> accepts
        /// the result, waiting <paramref name="delayMs"/> between attempts. Returns one final
        /// evaluation when the attempts run out, so the caller always gets the freshest value rather
        /// than a stale one.
        /// </summary>
        public static async Task<T> UntilAsync<T>(
            Func<CancellationToken, Task<T>> valueFactory,
            Func<T, bool> readyPredicate,
            int maxAttempts,
            int delayMs,
            CancellationToken ct)
        {
            if (valueFactory == null) throw new ArgumentNullException(nameof(valueFactory));
            if (readyPredicate == null) throw new ArgumentNullException(nameof(readyPredicate));

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var value = await valueFactory(ct).ConfigureAwait(false);
                if (readyPredicate(value))
                {
                    return value;
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return await valueFactory(ct).ConfigureAwait(false);
        }
    }
}
