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
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);
            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                try
                {
                    return action();
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
            Shot(action, bounce.Retries, bounce.BaseDelay, bounce.ShouldBounce, bounce.DisableJitter);

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
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            int retries = 3,
            TimeSpan? baseDelay = null,
            TimeSpan? timeout = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            retries = Math.Max(0, retries);

            // Create linked token source ONLY if timeout specified
            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;

            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(timeout!.Value);
                ct = timeoutCts.Token;
            }

            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var task = action();
                    var completedTask = await Task.WhenAny(task, Task.Delay(-1, ct)).ConfigureAwait(false);

                    if (completedTask == task)
                    {
                        return await task.ConfigureAwait(false);
                    }
                    
                    // Task.Delay completed, which means ct was cancelled (timeout or manual)
                    throw new OperationCanceledException(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
                    await Task.Delay(nextDelay, ct).ConfigureAwait(false);
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

            // Create linked token source ONLY if timeout specified
            using var timeoutCts = timeout.HasValue
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;

            if (timeoutCts != null)
            {
                timeoutCts.CancelAfter(timeout!.Value);
                ct = timeoutCts.Token;
            }

            var delay = baseDelay ?? JitterStrategy.DefaultBaseDelay;
            var previousDelay = delay;
            Exception? lastException = null;

            for (int attempt = 0; attempt <= retries; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var task = action();
                    var completedTask = await Task.WhenAny(task, Task.Delay(-1, ct)).ConfigureAwait(false);

                    if (completedTask == task)
                    {
                        await task.ConfigureAwait(false);
                        return;
                    }

                    throw new OperationCanceledException(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastException = ex;

                    if (shouldBounce != null && !shouldBounce(ex)) throw;
                    if (attempt >= retries) throw;

                    var nextDelay = JitterStrategy.CalculateDelay(delay, previousDelay, disableJitter, attempt + 1);
                    await Task.Delay(nextDelay, ct).ConfigureAwait(false);
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
            ShotAsync(action, bounce.Retries, bounce.BaseDelay, bounce.Timeout, bounce.ShouldBounce, bounce.DisableJitter, ct);

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
