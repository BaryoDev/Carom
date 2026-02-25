// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;

namespace Carom.DependencyInjection
{
    /// <summary>
    /// A composable resilience pipeline that chains multiple strategies.
    /// Strategies are executed in the order they were added (outer to inner).
    /// </summary>
    public class ResiliencePipeline
    {
        private readonly string _name;
        private readonly IReadOnlyList<IResilienceStrategy> _strategies;

        internal ResiliencePipeline(string name, IReadOnlyList<IResilienceStrategy> strategies)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _strategies = strategies ?? throw new ArgumentNullException(nameof(strategies));
        }

        /// <summary>
        /// Gets the name of this pipeline.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Executes an action through the resilience pipeline.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The action to execute.</param>
        /// <returns>The result of the action.</returns>
        public T Execute<T>(Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            Func<T> wrapped = action;

            // Wrap from innermost to outermost
            for (int i = _strategies.Count - 1; i >= 0; i--)
            {
                var strategy = _strategies[i];
                var current = wrapped;
                wrapped = () => strategy.Execute(current);
            }

            return wrapped();
        }

        /// <summary>
        /// Executes a void action through the resilience pipeline.
        /// </summary>
        /// <param name="action">The action to execute.</param>
        public void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            Execute(() =>
            {
                action();
                return 0;
            });
        }

        /// <summary>
        /// Executes an async action through the resilience pipeline.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The async action to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            Func<CancellationToken, Task<T>> wrapped = action;

            // Wrap from innermost to outermost
            for (int i = _strategies.Count - 1; i >= 0; i--)
            {
                var strategy = _strategies[i];
                var current = wrapped;
                wrapped = token => strategy.ExecuteAsync(current, token);
            }

            return wrapped(ct);
        }

        /// <summary>
        /// Executes an async action through the resilience pipeline.
        /// </summary>
        /// <typeparam name="T">The return type.</typeparam>
        /// <param name="action">The async action to execute (without CancellationToken).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The result of the action.</returns>
        public Task<T> ExecuteAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
        {
            return ExecuteAsync(_ => action(), ct);
        }

        /// <summary>
        /// Executes an async void action through the resilience pipeline.
        /// </summary>
        /// <param name="action">The async action to execute.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            await ExecuteAsync(async token =>
            {
                await action(token).ConfigureAwait(false);
                return 0;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an async void action through the resilience pipeline.
        /// </summary>
        /// <param name="action">The async action to execute (without CancellationToken).</param>
        /// <param name="ct">Cancellation token.</param>
        public Task ExecuteAsync(Func<Task> action, CancellationToken ct = default)
        {
            return ExecuteAsync(_ => action(), ct);
        }
    }

    /// <summary>
    /// Interface for resilience strategies that can be chained in a pipeline.
    /// </summary>
    public interface IResilienceStrategy
    {
        /// <summary>
        /// Executes an action with this strategy applied.
        /// </summary>
        T Execute<T>(Func<T> action);

        /// <summary>
        /// Executes an async action with this strategy applied.
        /// </summary>
        Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct);
    }

    /// <summary>
    /// Retry strategy using Carom.
    /// </summary>
    internal sealed class RetryStrategy : IResilienceStrategy
    {
        private readonly Bounce _config;

        public RetryStrategy(Bounce config)
        {
            _config = config;
        }

        public T Execute<T>(Func<T> action) => Carom.Shot(action, _config);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            Carom.ShotAsync(() => action(ct), _config, ct);
    }

    /// <summary>
    /// Circuit breaker strategy using Cushion.
    /// </summary>
    internal sealed class CircuitBreakerStrategy : IResilienceStrategy
    {
        private readonly string _serviceKey;
        private readonly Cushion _config;

        public CircuitBreakerStrategy(string serviceKey, Cushion config)
        {
            _serviceKey = serviceKey;
            _config = config;
        }

        public T Execute<T>(Func<T> action) => _config.Execute(action);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            _config.ExecuteAsync(() => action(ct));
    }

    /// <summary>
    /// Timeout strategy.
    /// </summary>
    internal sealed class TimeoutStrategy : IResilienceStrategy
    {
        private readonly TimeSpan _timeout;

        public TimeoutStrategy(TimeSpan timeout)
        {
            _timeout = timeout;
        }

        public T Execute<T>(Func<T> action)
        {
            // Synchronous timeout using task-based approach with cancellation
            using var cts = new CancellationTokenSource(_timeout);
            var token = cts.Token;
            var task = Task.Run(action, token);
            try
            {
                if (!task.Wait(_timeout))
                {
                    cts.Cancel();
                    // Observe any exception to avoid UnobservedTaskException
                    task.ContinueWith(static t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    throw new TimeoutException($"Operation timed out after {_timeout.TotalMilliseconds}ms");
                }
                return task.GetAwaiter().GetResult();
            }
            catch (AggregateException ae) when (ae.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw();
                throw; // Unreachable, satisfies compiler
            }
        }

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            try
            {
                return await action(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Operation timed out after {_timeout.TotalMilliseconds}ms");
            }
        }
    }

    /// <summary>
    /// Fallback strategy.
    /// </summary>
    internal sealed class FallbackStrategy<TResult> : IResilienceStrategy
    {
        private readonly Func<Exception, TResult> _fallback;

        public FallbackStrategy(Func<Exception, TResult> fallback)
        {
            _fallback = fallback;
        }

        public T Execute<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                if (typeof(T) == typeof(TResult))
                {
                    return (T)(object)_fallback(ex)!;
                }
                throw;
            }
        }

        public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (typeof(T) == typeof(TResult))
                {
                    return (T)(object)_fallback(ex)!;
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Bulkhead strategy using Compartment.
    /// </summary>
    internal sealed class BulkheadStrategy : IResilienceStrategy
    {
        private readonly string _resourceKey;
        private readonly Compartment _config;

        public BulkheadStrategy(string resourceKey, Compartment config)
        {
            _resourceKey = resourceKey;
            _config = config;
        }

        public T Execute<T>(Func<T> action) => _config.Execute(action);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            _config.ExecuteAsync(() => action(ct), ct);
    }

    /// <summary>
    /// Rate limiting strategy using Throttle.
    /// </summary>
    internal sealed class RateLimitStrategy : IResilienceStrategy
    {
        private readonly string _serviceKey;
        private readonly Throttle _config;

        public RateLimitStrategy(string serviceKey, Throttle config)
        {
            _serviceKey = serviceKey;
            _config = config;
        }

        public T Execute<T>(Func<T> action) => _config.Execute(action);

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            _config.ExecuteAsync(() => action(ct));
    }
}
