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

            // Launch first attempt immediately
            tasks.Add(LaunchAttempt(action, linkedCts.Token));

            // Launch hedged attempts after delays
            for (int i = 1; i < config.MaxHedgedAttempts; i++)
            {
                // Wait for hedge delay or until an existing task completes
                var delayTask = Task.Delay(config.HedgeDelay, linkedCts.Token);
                var completedTask = await Task.WhenAny(Task.WhenAny(tasks), delayTask).ConfigureAwait(false);

                // Check if any task completed before the delay
                if (completedTask != delayTask)
                {
                    var finishedTask = await Task.WhenAny(tasks).ConfigureAwait(false);

                    try
                    {
                        var result = await finishedTask.ConfigureAwait(false);

                        // Check if we should continue hedging
                        if (config.ShouldHedge == null || !config.ShouldHedge(result))
                        {
                            if (config.CancelPendingOnSuccess)
                            {
                                linkedCts.Cancel();
                                ObserveRemainingTasks(tasks, finishedTask);
                            }
                            return result;
                        }
                        // Result wasn't satisfactory, continue hedging
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
                        tasks.Remove(finishedTask);
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
                    // Result wasn't satisfactory, try next
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

            // All attempts failed
            if (exceptions.Count == 1)
            {
                throw exceptions[0];
            }
            throw new AggregateException("All hedged attempts failed", exceptions);
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
                    t.ContinueWith(static task => { var _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
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
