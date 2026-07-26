using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Rate limiter with exponential backoff and jitter for handling transient failures.
    /// </summary>
    public sealed class RateLimiter
    {
        private readonly Random _jitter = new Random();
        private readonly int _baseDelayMs;
        private readonly int _maxRetryAttempts;

        /// <summary>
        /// Maximum backoff delay (30 seconds).
        /// </summary>
        private const int MaxBackoffMs = 30000;

        /// <summary>
        /// Creates a new rate limiter.
        /// </summary>
        /// <param name="baseDelayMs">Base delay between requests in milliseconds.</param>
        /// <param name="maxRetryAttempts">Maximum retry attempts on transient failures.</param>
        public RateLimiter(int baseDelayMs, int maxRetryAttempts = 3)
        {
            _baseDelayMs = Math.Max(0, baseDelayMs);
            _maxRetryAttempts = Math.Max(0, maxRetryAttempts);
        }

        /// <summary>
        /// Executes an operation with rate limiting and exponential backoff on transient failures.
        /// </summary>
        /// <typeparam name="T">Return type of the operation.</typeparam>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="isTransientError">Function to determine if an exception is a transient error.</param>
        /// <param name="cancel">Cancellation token.</param>
        /// <returns>The result of the operation.</returns>
        public async Task<T> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            Func<Exception, bool> isTransientError,
            CancellationToken cancel)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            if (isTransientError == null) throw new ArgumentNullException(nameof(isTransientError));

            int attempt = 0;
            int consecutiveErrors = 0;

            while (true)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    // Apply base delay before the attempt (except for first attempt)
                    if (attempt > 0)
                    {
                        await Task.Delay(_baseDelayMs, cancel).ConfigureAwait(false);
                    }

                    var result = await operation().ConfigureAwait(false);

                    // Reset consecutive errors on success
                    consecutiveErrors = 0;
                    return result;
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (isTransientError(ex) ||
                                           (ex is OperationCanceledException && !cancel.IsCancellationRequested))
                {
                    // The second filter arm catches timeout-shaped cancellations (e.g. HttpClient's
                    // TaskCanceledException) that arrive without the caller's token being cancelled;
                    // they retry like any other transient failure instead of aborting as a cancel.
                    consecutiveErrors++;
                    attempt++;

                    if (attempt > _maxRetryAttempts || consecutiveErrors > _maxRetryAttempts)
                    {
                        throw;
                    }

                    // Calculate exponential backoff with jitter
                    var delay = CalculateBackoffWithJitter(consecutiveErrors);
                    cancel.ThrowIfCancellationRequested();
                    await Task.Delay(delay, cancel).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Executes an async operation with base delay between calls (no retry logic).
        /// Use this for the main refresh loop where you want delay between games but handle retries differently.
        /// </summary>
        /// <param name="operation">The operation to execute.</param>
        /// <param name="cancel">Cancellation token.</param>
        public async Task ExecuteWithDelayAsync(
            Func<Task> operation,
            CancellationToken cancel)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            cancel.ThrowIfCancellationRequested();

            if (_baseDelayMs > 0)
            {
                await Task.Delay(_baseDelayMs, cancel).ConfigureAwait(false);
            }

            await operation().ConfigureAwait(false);
        }

        /// <summary>
        /// Calculates exponential backoff delay with jitter.
        /// Formula: min(baseDelay * 2^(attempt-1) + random(0, baseDelay/2), MaxBackoffMs)
        /// </summary>
        private int CalculateBackoffWithJitter(int consecutiveErrors)
        {
            // Exponential backoff: baseDelay * 2^(consecutiveErrors - 1)
            var exponentialDelay = _baseDelayMs * (1 << (consecutiveErrors - 1));

            // Add jitter: random value between 0 and baseDelay/2
            var jitter = _jitter.Next(0, Math.Max(1, _baseDelayMs / 2));

            // Cap at maximum backoff
            var totalDelay = Math.Min(exponentialDelay + jitter, MaxBackoffMs);

            return totalDelay;
        }

        /// <summary>
        /// Applies a simple delay before the next request (for use in loops).
        /// </summary>
        public Task DelayBeforeNextAsync(CancellationToken cancel)
        {
            if (_baseDelayMs <= 0) return Task.CompletedTask;
            return Task.Delay(_baseDelayMs, cancel);
        }

        /// <summary>
        /// Applies exponential backoff delay after an error.
        /// </summary>
        public Task DelayAfterErrorAsync(int consecutiveErrors, CancellationToken cancel)
        {
            var delay = CalculateBackoffWithJitter(consecutiveErrors);
            return Task.Delay(delay, cancel);
        }
    }
}
