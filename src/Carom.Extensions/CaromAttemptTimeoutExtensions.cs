// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// A timeout for each single try. <c>Bounce.WithTimeout</c> bounds the whole call including
    /// retries; these overloads also give every try its own timeout. A try that runs out is
    /// cancelled (the token passed to the action is cancelled) and fails with a
    /// <see cref="TimeoutRejectedException"/>, which Carom retries like any other failure that
    /// passes the Bounce predicate. The overall timeout and caller cancellation still end the call.
    /// </summary>
    public static class CaromAttemptTimeoutExtensions
    {
        /// <summary>
        /// Executes a cancellable async action with retry logic and a timeout for each try.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action. Receives a token cancelled on a try timeout, the overall timeout or caller cancellation.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <param name="attemptTimeout">The timeout for each try. Must be positive.</param>
        /// <param name="ct">Cancellation token.</param>
        public static Task<T> ShotAsync<T>(
            Func<CancellationToken, Task<T>> action,
            Bounce bounce,
            TimeSpan attemptTimeout,
            CancellationToken ct = default)
        {
            Validate(action, attemptTimeout);
            return Carom.ShotAsync(Limit(action, attemptTimeout), bounce, ct);
        }

        /// <summary>
        /// Executes a cancellable async action with result-based retry and a timeout for each try.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The action. Receives a token cancelled on a try timeout, the overall timeout or caller cancellation.</param>
        /// <param name="bounce">The retry configuration with result predicate.</param>
        /// <param name="attemptTimeout">The timeout for each try. Must be positive.</param>
        /// <param name="ct">Cancellation token.</param>
        public static Task<T> ShotAsync<T>(
            Func<CancellationToken, Task<T>> action,
            Bounce<T> bounce,
            TimeSpan attemptTimeout,
            CancellationToken ct = default)
        {
            Validate(action, attemptTimeout);
            return Carom.ShotAsync(Limit(action, attemptTimeout), bounce, ct);
        }

        /// <summary>
        /// Executes a cancellable async void action with retry logic and a timeout for each try.
        /// </summary>
        /// <param name="action">The action. Receives a token cancelled on a try timeout, the overall timeout or caller cancellation.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <param name="attemptTimeout">The timeout for each try. Must be positive.</param>
        /// <param name="ct">Cancellation token.</param>
        public static Task ShotAsync(
            Func<CancellationToken, Task> action,
            Bounce bounce,
            TimeSpan attemptTimeout,
            CancellationToken ct = default)
        {
            Validate(action, attemptTimeout);
            return Carom.ShotAsync(Limit(action, attemptTimeout), bounce, ct);
        }

        private static void Validate(Delegate action, TimeSpan attemptTimeout)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (attemptTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(attemptTimeout), "Attempt timeout must be positive");
        }

        private static Func<CancellationToken, Task<T>> Limit<T>(Func<CancellationToken, Task<T>> action, TimeSpan attemptTimeout)
        {
            return async ct =>
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(attemptTimeout);
                var token = attemptCts.Token;
                try
                {
                    var task = action(token);
                    var completed = await Task.WhenAny(task, Task.Delay(-1, token)).ConfigureAwait(false);
                    if (completed == task) return await task.ConfigureAwait(false);

                    Observe(task);
                    throw new OperationCanceledException(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // The per-try timer fired, not the overall timeout or the caller.
                    throw new TimeoutRejectedException(attemptTimeout);
                }
            };
        }

        private static Func<CancellationToken, Task> Limit(Func<CancellationToken, Task> action, TimeSpan attemptTimeout)
        {
            var limited = Limit<bool>(async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            }, attemptTimeout);
            return ct => limited(ct);
        }

        // An abandoned try keeps running; observe its fault so it never surfaces as unobserved.
        private static void Observe(Task task)
        {
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
