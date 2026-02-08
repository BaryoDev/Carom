// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom
{
    /// <summary>
    /// Carom: A physics-based, zero-dependency resilience library.
    /// Take a Shot. If it misses, it caroms off the cushion and tries again.
    /// </summary>
    public static class Carom
    {
        #region Synchronous Methods

        /// <summary>
        /// Executes an action with retry logic and decorrelated jitter.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="baseDelay">Base delay between retries (default: 100ms).</param>
        /// <param name="shouldBounce">Predicate to determine if an exception should trigger a retry.</param>
        /// <param name="disableJitter">If true, uses fixed exponential backoff instead of jitter.</param>
        /// <returns>The result of the action.</returns>
        public static T Shot<T>(
            Func<T> action,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return Shot(action, retries, baseDelay, shouldBounce, shouldRetryResult: null, disableJitter);
        }

        /// <summary>
        /// Executes an action with retry logic and decorrelated jitter, including result-based retry.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="baseDelay">Base delay between retries (default: 100ms).</param>
        /// <param name="shouldBounce">Predicate to determine if an exception should trigger a retry.</param>
        /// <param name="shouldRetryResult">Predicate to determine if a result should trigger a retry (returns true to retry).</param>
        /// <param name="disableJitter">If true, uses fixed exponential backoff instead of jitter.</param>
        /// <returns>The result of the action.</returns>
        public static T Shot<T>(
            Func<T> action,
            int retries,
            TimeSpan? baseDelay,
            Func<Exception, bool>? shouldBounce,
            Func<T, bool>? shouldRetryResult,
            bool disableJitter = false)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);
            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;
            T lastResult = default!;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    var result = action();

                    // Check if we should retry based on result
                    if (shouldRetryResult != null && shouldRetryResult(result))
                    {
                        lastResult = result;

                        // Check if we have retries left
                        if (attempt >= retries)
                        {
                            return result; // Return the last result if no retries left
                        }

                        // Calculate and wait for the next delay
                        var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                        Thread.Sleep(nextDelay);
                        previousDelay = nextDelay;
                        continue;
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    // Check if we should retry this exception
                    if (shouldBounce != null && !shouldBounce(ex))
                    {
                        throw;
                    }

                    // Check if we have retries left
                    if (attempt >= retries)
                    {
                        throw;
                    }

                    // Calculate and wait for the next delay
                    var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                    Thread.Sleep(nextDelay);
                    previousDelay = nextDelay;
                }
            }

            // This should never be reached, but satisfies the compiler
            throw lastException ?? new InvalidOperationException("Retry loop exited unexpectedly");
        }

        public static void Shot(
            Action action,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);
            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (shouldBounce != null && !shouldBounce(ex)) throw;
                    if (attempt >= retries) throw;

                    var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                    Thread.Sleep(nextDelay);
                    previousDelay = nextDelay;
                }
            }

            throw lastException ?? new InvalidOperationException("Retry loop exited unexpectedly");
        }

        /// <summary>
        /// Executes an action with retry logic using a Bounce configuration.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <returns>The result of the action.</returns>
        public static T Shot<T>(Func<T> action, Bounce bounce) =>
            Shot(action, bounce.Retries, bounce.BaseDelay, bounce.ShouldBounce, shouldRetryResult: null, bounce.DisableJitter);

        /// <summary>
        /// Executes an action with retry logic using a typed Bounce configuration with result-based retry.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <param name="bounce">The retry configuration with result predicate.</param>
        /// <returns>The result of the action.</returns>
        public static T Shot<T>(Func<T> action, Bounce<T> bounce) =>
            Shot(action, bounce.Retries, bounce.BaseDelay, bounce.ShouldBounce, bounce.ShouldRetryResult, bounce.DisableJitter);

        /// <summary>
        /// Executes a void action with retry logic using a Bounce configuration.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        /// <param name="bounce">The retry configuration.</param>
        public static void Shot(Action action, Bounce bounce) =>
            Shot(action, bounce.Retries, bounce.BaseDelay, bounce.ShouldBounce, bounce.DisableJitter);

        #endregion

        #region Asynchronous Methods

        /// <summary>
        /// Executes an async action with retry logic and decorrelated jitter.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="baseDelay">Base delay between retries (default: 100ms).</param>
        /// <param name="shouldBounce">Predicate to determine if an exception should trigger a retry.</param>
        /// <param name="disableJitter">If true, uses fixed exponential backoff instead of jitter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            int retries = 3,
            TimeSpan? baseDelay = null,
            TimeSpan? timeout = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return ShotAsync(action, retries, baseDelay, timeout, shouldBounce, shouldRetryResult: null, disableJitter, ct);
        }

        /// <summary>
        /// Executes an async action with retry logic and decorrelated jitter, including result-based retry.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="baseDelay">Base delay between retries (default: 100ms).</param>
        /// <param name="timeout">Timeout for the entire operation.</param>
        /// <param name="shouldBounce">Predicate to determine if an exception should trigger a retry.</param>
        /// <param name="shouldRetryResult">Predicate to determine if a result should trigger a retry (returns true to retry).</param>
        /// <param name="disableJitter">If true, uses fixed exponential backoff instead of jitter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            int retries,
            TimeSpan? baseDelay,
            TimeSpan? timeout,
            Func<Exception, bool>? shouldBounce,
            Func<T, bool>? shouldRetryResult,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);

            // Create linked token source if timeout specified OR if we have a real cancellation token
            // This ensures we can properly clean up the delay task
            using var timeoutCts = (timeout.HasValue || ct.CanBeCanceled)
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;

            if (timeout.HasValue && timeoutCts != null)
            {
                timeoutCts.CancelAfter(timeout.Value);
            }

            var effectiveCt = timeoutCts?.Token ?? ct;

            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;
            T lastResult = default!;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                effectiveCt.ThrowIfCancellationRequested();

                try
                {
                    Task<T> task = action();
                    T result;

                    // Only use WhenAny pattern if we have a cancellation mechanism
                    // This prevents Task.Delay(-1, ct) from leaking when ct is never cancelled
                    if (effectiveCt.CanBeCanceled)
                    {
                        var completedTask = await Task.WhenAny(task, Task.Delay(-1, effectiveCt)).ConfigureAwait(false);

                        if (completedTask == task)
                        {
                            result = await task.ConfigureAwait(false);
                        }
                        else
                        {
                            // Task.Delay completed, which means effectiveCt was cancelled (timeout or manual)
                            throw new OperationCanceledException(effectiveCt);
                        }
                    }
                    else
                    {
                        // No cancellation possible - just await directly (prevents leak)
                        result = await task.ConfigureAwait(false);
                    }

                    // Check if we should retry based on result
                    if (shouldRetryResult != null && shouldRetryResult(result))
                    {
                        lastResult = result;

                        // Check if we have retries left
                        if (attempt >= retries)
                        {
                            return result; // Return the last result if no retries left
                        }

                        // Calculate and wait for the next delay
                        var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                        await Task.Delay(nextDelay, effectiveCt).ConfigureAwait(false);
                        previousDelay = nextDelay;
                        continue;
                    }

                    return result;
                }
                catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    // Check if we should retry this exception
                    if (shouldBounce != null && !shouldBounce(ex))
                    {
                        throw;
                    }

                    // Check if we have retries left
                    if (attempt >= retries)
                    {
                        throw;
                    }

                    // Calculate and wait for the next delay
                    var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                    await Task.Delay(nextDelay, effectiveCt).ConfigureAwait(false);
                    previousDelay = nextDelay;
                }
            }

            // This should never be reached, but satisfies the compiler
            throw lastException ?? new InvalidOperationException("Retry loop exited unexpectedly");
        }

        public static async Task ShotAsync(
            Func<Task> action,
            int retries = 3,
            TimeSpan? baseDelay = null,
            TimeSpan? timeout = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);

            // Create linked token source if timeout specified OR if we have a real cancellation token
            // This ensures we can properly clean up the delay task
            using var timeoutCts = (timeout.HasValue || ct.CanBeCanceled)
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;

            if (timeout.HasValue && timeoutCts != null)
            {
                timeoutCts.CancelAfter(timeout.Value);
            }

            var effectiveCt = timeoutCts?.Token ?? ct;

            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                effectiveCt.ThrowIfCancellationRequested();

                try
                {
                    var task = action();

                    // Only use WhenAny pattern if we have a cancellation mechanism
                    // This prevents Task.Delay(-1, ct) from leaking when ct is never cancelled
                    if (effectiveCt.CanBeCanceled)
                    {
                        var completedTask = await Task.WhenAny(task, Task.Delay(-1, effectiveCt)).ConfigureAwait(false);

                        if (completedTask == task)
                        {
                            await task.ConfigureAwait(false);
                            return;
                        }

                        throw new OperationCanceledException(effectiveCt);
                    }
                    else
                    {
                        // No cancellation possible - just await directly (prevents leak)
                        await task.ConfigureAwait(false);
                        return;
                    }
                }
                catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (shouldBounce != null && !shouldBounce(ex)) throw;
                    if (attempt >= retries) throw;

                    var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                    await Task.Delay(nextDelay, effectiveCt).ConfigureAwait(false);
                    previousDelay = nextDelay;
                }
            }

            throw lastException ?? new InvalidOperationException("Retry loop exited unexpectedly");
        }

        /// <summary>
        /// Executes an async action with retry logic using a Bounce configuration.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public static Task<T> ShotAsync<T>(Func<Task<T>> action, Bounce bounce, CancellationToken ct = default) =>
            ShotAsync(action, bounce.Retries, bounce.BaseDelay, bounce.Timeout, bounce.ShouldBounce, shouldRetryResult: null, bounce.DisableJitter, ct);

        /// <summary>
        /// Executes an async action with retry logic using a typed Bounce configuration with result-based retry.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="bounce">The retry configuration with result predicate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public static Task<T> ShotAsync<T>(Func<Task<T>> action, Bounce<T> bounce, CancellationToken ct = default) =>
            ShotAsync(action, bounce.Retries, bounce.BaseDelay, bounce.Timeout, bounce.ShouldBounce, bounce.ShouldRetryResult, bounce.DisableJitter, ct);

        /// <summary>
        /// Executes an async void action with retry logic using a Bounce configuration.
        /// </summary>
        /// <param name="action">The async action to execute.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        public static Task ShotAsync(Func<Task> action, Bounce bounce, CancellationToken ct = default) =>
            ShotAsync(action, bounce.Retries, bounce.BaseDelay, bounce.Timeout, bounce.ShouldBounce, bounce.DisableJitter, ct);

        #endregion
    }
}
