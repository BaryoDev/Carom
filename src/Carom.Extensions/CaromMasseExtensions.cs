// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for hedging (Masse) pattern execution.
    /// </summary>
    public static class CaromMasseExtensions
    {
        /// <summary>
        /// Executes an async action with hedging pattern.
        /// Launches parallel backup requests after a delay to improve latency.
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute (receives a CancellationToken).</param>
        /// <param name="config">The hedging configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the first successful attempt.</returns>
        public static async Task<T> ShotWithHedgingAsync<T>(
            Func<CancellationToken, Task<T>> action,
            Masse config,
            CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new List<Task<T>>(config.MaxHedgedAttempts);
            var exceptions = new List<Exception>();
            T lastResult = default!;
            var haveResult = false;

            // Launch first attempt immediately
            tasks.Add(LaunchAttempt(action, linkedCts.Token));

            // Launch hedged attempts after delays
            for (int i = 1; i < config.MaxHedgedAttempts; i++)
            {
                // The delay gates the next launch even when an attempt comes back
                // unsatisfactory early: draining a completed task must not shortcut
                // the wait, or every remaining attempt fires as fast as the loop
                // can go and the hedge becomes request amplification.
                var delayTask = Task.Delay(config.HedgeDelay, linkedCts.Token);

                while (true)
                {
                    if (tasks.Count == 0)
                    {
                        // Nothing in flight; wait out the remaining delay.
                        // WhenAny never throws, so a cancelled delay is observed
                        // safely and the cancellation check below handles it.
                        await Task.WhenAny(delayTask).ConfigureAwait(false);
                        break;
                    }

                    var completedTask = await Task.WhenAny(Task.WhenAny(tasks), delayTask).ConfigureAwait(false);
                    if (completedTask == delayTask)
                    {
                        break;
                    }

                    var finishedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.Remove(finishedTask);

                    try
                    {
                        var result = await finishedTask.ConfigureAwait(false);

                        // Check if we should continue hedging
                        if (config.ShouldHedge == null || !config.ShouldHedge(result))
                        {
                            if (config.CancelPendingOnSuccess)
                            {
                                linkedCts.Cancel();
                                ObserveRemainingTasks(tasks);
                            }
                            return result;
                        }

                        // Result wasn't satisfactory; keep it and wait out the delay
                        lastResult = result;
                        haveResult = true;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Task failed, continue with hedging
                        lock (exceptions)
                        {
                            exceptions.Add(ex);
                        }
                    }
                }

                // Launch next hedged attempt
                if (!linkedCts.Token.IsCancellationRequested)
                {
                    tasks.Add(LaunchAttempt(action, linkedCts.Token));
                }
            }

            // Wait for any remaining task to complete successfully
            while (tasks.Count > 0)
            {
                var completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completedTask);

                try
                {
                    var result = await completedTask.ConfigureAwait(false);

                    // Check if we should accept this result
                    if (config.ShouldHedge == null || !config.ShouldHedge(result))
                    {
                        if (config.CancelPendingOnSuccess)
                        {
                            linkedCts.Cancel();
                            ObserveRemainingTasks(tasks);
                        }
                        return result;
                    }

                    // Result wasn't satisfactory, keep it and try the next
                    lastResult = result;
                    haveResult = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Internal cancellation, ignore
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }

            // All attempts either failed or returned an unsatisfactory result.
            // Exceptions surface first; when every attempt succeeded but none
            // satisfied ShouldHedge, return the last result rather than an
            // AggregateException with no inner exceptions. This matches
            // Carom.ShotAsync's shouldRetryResult behaviour, which returns the
            // last result when retries run out.
            if (exceptions.Count == 1)
            {
                throw exceptions[0];
            }
            if (exceptions.Count > 1)
            {
                throw new AggregateException("All hedged attempts failed", exceptions);
            }
            if (haveResult)
            {
                return lastResult;
            }
            throw new InvalidOperationException("Hedging completed with no attempts");
        }

        /// <summary>
        /// Executes an async action with hedging pattern (action without CancellationToken).
        /// </summary>
        /// <typeparam name="T">The return type of the action.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="config">The hedging configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the first successful attempt.</returns>
        public static Task<T> ShotWithHedgingAsync<T>(
            Func<Task<T>> action,
            Masse config,
            CancellationToken ct = default)
        {
            return ShotWithHedgingAsync(_ => action(), config, ct);
        }

        private static void ObserveRemainingTasks<T>(List<Task<T>> tasks, Task<T>? excludeTask = null)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                if (t != excludeTask)
                {
                    t.ContinueWith(
                        static task => { var _ = task.Exception; },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

        private static async Task<T> LaunchAttempt<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken ct)
        {
            return await action(ct).ConfigureAwait(false);
        }
    }
}
