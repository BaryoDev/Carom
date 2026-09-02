// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;

namespace Carom
{
    /// <summary>
    /// A lightweight, immutable configuration struct for retry operations.
    /// Provides a fluent API for configuring retry behavior without allocations.
    /// </summary>
    public readonly struct Bounce
    {
        /// <summary>
        /// Creates a typed Bounce configuration for result-based retry.
        /// </summary>
        /// <typeparam name="T">The result type to retry on.</typeparam>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <returns>A typed Bounce configuration.</returns>
        public static Bounce<T> For<T>(int retries = 3) => Bounce<T>.Times(retries);

        /// <summary>
        /// The number of retry attempts.
        /// </summary>
        public int Retries { get; }

        /// <summary>
        /// The base delay between retries.
        /// </summary>
        public TimeSpan BaseDelay { get; }

        /// <summary>
        /// Whether to disable jitter (not recommended).
        /// </summary>
        public bool DisableJitter { get; }

        /// <summary>
        /// The exception predicate to determine if an exception should trigger a retry.
        /// </summary>
        public Func<Exception, bool>? ShouldBounce { get; }

        /// <summary>
        /// Optional timeout for the entire operation (including retries).
        /// </summary>
        public TimeSpan? Timeout { get; }

        /// <summary>
        /// Ceiling for any computed retry delay, jittered or fixed. Defaults to 30 seconds.
        /// A base delay above this ceiling is clamped to it.
        /// </summary>
        public TimeSpan MaxDelay => _maxDelay ?? JitterStrategy.DefaultMaxDelay;

        // Nullable backing so default(Bounce) still reports the 30 second default.
        private readonly TimeSpan? _maxDelay;

        private Bounce(int retries, TimeSpan baseDelay, TimeSpan? timeout, bool disableJitter, Func<Exception, bool>? shouldBounce, TimeSpan? maxDelay = null)
        {
            Retries = retries;
            BaseDelay = baseDelay;
            Timeout = timeout;
            DisableJitter = disableJitter;
            ShouldBounce = shouldBounce;
            _maxDelay = maxDelay;
        }

        /// <summary>
        /// Creates a bounce configuration with the specified number of retries.
        /// </summary>
        /// <param name="count">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce Times(int count = 3) =>
            new Bounce(count, JitterStrategy.DefaultBaseDelay, timeout: null, disableJitter: false, shouldBounce: null);

        /// <summary>
        /// Creates a bounce configuration that retries on the specified exception type.
        /// </summary>
        /// <typeparam name="TException">The exception type to retry on.</typeparam>
        /// <param name="retries">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce On<TException>(int retries = 3) where TException : Exception =>
            new Bounce(retries, JitterStrategy.DefaultBaseDelay, timeout: null, disableJitter: false, 
                shouldBounce: ex => ex is TException);

        /// <summary>
        /// Sets the base delay between retries.
        /// </summary>
        /// <param name="delay">The base delay. Must not be negative.</param>
        /// <returns>A new Bounce configuration with the specified delay.</returns>
        public Bounce WithDelay(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay), "Delay cannot be negative");
            return new Bounce(Retries, delay, Timeout, DisableJitter, ShouldBounce, _maxDelay);
        }

        /// <summary>
        /// Disables jitter, using fixed exponential backoff instead.
        /// WARNING: This can cause Thundering Herd issues and is not recommended.
        /// </summary>
        /// <returns>A new Bounce configuration with jitter disabled.</returns>
        public Bounce WithoutJitter() =>
            new Bounce(Retries, BaseDelay, Timeout, disableJitter: true, ShouldBounce, _maxDelay);

        /// <summary>
        /// Sets a predicate to determine which exceptions should trigger a retry.
        /// </summary>
        /// <param name="predicate">The exception predicate.</param>
        /// <returns>A new Bounce configuration with the specified predicate.</returns>
        public Bounce When(Func<Exception, bool> predicate) =>
            new Bounce(Retries, BaseDelay, Timeout, DisableJitter, predicate, _maxDelay);

        /// <summary>
        /// Sets the maximum delay between retries.
        /// Caps every computed backoff, jittered or fixed. Defaults to 30 seconds.
        /// A base delay above this ceiling is clamped down to it, not rejected,
        /// matching how the fixed 30 second cap has always behaved.
        /// </summary>
        /// <param name="maxDelay">The delay ceiling. Must be positive.</param>
        /// <returns>A new Bounce configuration with the specified maximum delay.</returns>
        public Bounce WithMaxDelay(TimeSpan maxDelay)
        {
            if (maxDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be positive");
            return new Bounce(Retries, BaseDelay, Timeout, DisableJitter, ShouldBounce, maxDelay);
        }

        /// <summary>
        /// Sets the timeout for the operation.
        /// The timeout is honored only by the asynchronous ShotAsync overloads.
        /// The synchronous Shot overloads have no cancellation mechanism and ignore it.
        /// </summary>
        /// <param name="timeout">The timeout duration. Must be positive.</param>
        /// <returns>A new Bounce configuration with the specified timeout.</returns>
        public Bounce WithTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive");
            return new Bounce(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce, _maxDelay);
        }
    }

    /// <summary>
    /// A lightweight, immutable configuration struct for retry operations with result-based retry.
    /// Provides a fluent API for configuring retry behavior including result predicates.
    /// </summary>
    /// <typeparam name="T">The result type for result-based retry.</typeparam>
    public readonly struct Bounce<T>
    {
        /// <summary>
        /// The number of retry attempts.
        /// </summary>
        public int Retries { get; }

        /// <summary>
        /// The base delay between retries.
        /// </summary>
        public TimeSpan BaseDelay { get; }

        /// <summary>
        /// Whether to disable jitter (not recommended).
        /// </summary>
        public bool DisableJitter { get; }

        /// <summary>
        /// The exception predicate to determine if an exception should trigger a retry.
        /// </summary>
        public Func<Exception, bool>? ShouldBounce { get; }

        /// <summary>
        /// The result predicate to determine if a result should trigger a retry.
        /// Returns true if the result should cause a retry.
        /// </summary>
        public Func<T, bool>? ShouldRetryResult { get; }

        /// <summary>
        /// Optional timeout for the entire operation (including retries).
        /// </summary>
        public TimeSpan? Timeout { get; }

        /// <summary>
        /// Ceiling for any computed retry delay, jittered or fixed. Defaults to 30 seconds.
        /// A base delay above this ceiling is clamped to it.
        /// </summary>
        public TimeSpan MaxDelay => _maxDelay ?? JitterStrategy.DefaultMaxDelay;

        // Nullable backing so default(Bounce<T>) still reports the 30 second default.
        private readonly TimeSpan? _maxDelay;

        private Bounce(int retries, TimeSpan baseDelay, TimeSpan? timeout, bool disableJitter,
            Func<Exception, bool>? shouldBounce, Func<T, bool>? shouldRetryResult, TimeSpan? maxDelay = null)
        {
            Retries = retries;
            BaseDelay = baseDelay;
            Timeout = timeout;
            DisableJitter = disableJitter;
            ShouldBounce = shouldBounce;
            ShouldRetryResult = shouldRetryResult;
            _maxDelay = maxDelay;
        }

        /// <summary>
        /// Creates a bounce configuration with the specified number of retries.
        /// </summary>
        /// <param name="count">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce<T> Times(int count = 3) =>
            new Bounce<T>(count, JitterStrategy.DefaultBaseDelay, timeout: null, disableJitter: false,
                shouldBounce: null, shouldRetryResult: null);

        /// <summary>
        /// Creates a bounce configuration that retries on the specified exception type.
        /// </summary>
        /// <typeparam name="TException">The exception type to retry on.</typeparam>
        /// <param name="retries">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce<T> On<TException>(int retries = 3) where TException : Exception =>
            new Bounce<T>(retries, JitterStrategy.DefaultBaseDelay, timeout: null, disableJitter: false,
                shouldBounce: ex => ex is TException, shouldRetryResult: null);

        /// <summary>
        /// Sets the base delay between retries.
        /// </summary>
        /// <param name="delay">The base delay. Must not be negative.</param>
        /// <returns>A new Bounce configuration with the specified delay.</returns>
        public Bounce<T> WithDelay(TimeSpan delay)
        {
            if (delay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(delay), "Delay cannot be negative");
            return new Bounce<T>(Retries, delay, Timeout, DisableJitter, ShouldBounce, ShouldRetryResult, _maxDelay);
        }

        /// <summary>
        /// Disables jitter, using fixed exponential backoff instead.
        /// WARNING: This can cause Thundering Herd issues and is not recommended.
        /// </summary>
        /// <returns>A new Bounce configuration with jitter disabled.</returns>
        public Bounce<T> WithoutJitter() =>
            new Bounce<T>(Retries, BaseDelay, Timeout, disableJitter: true, ShouldBounce, ShouldRetryResult, _maxDelay);

        /// <summary>
        /// Sets a predicate to determine which exceptions should trigger a retry.
        /// </summary>
        /// <param name="predicate">The exception predicate.</param>
        /// <returns>A new Bounce configuration with the specified predicate.</returns>
        public Bounce<T> When(Func<Exception, bool> predicate) =>
            new Bounce<T>(Retries, BaseDelay, Timeout, DisableJitter, predicate, ShouldRetryResult, _maxDelay);

        /// <summary>
        /// Sets a predicate to determine which results should trigger a retry.
        /// </summary>
        /// <param name="predicate">The result predicate (returns true to retry).</param>
        /// <returns>A new Bounce configuration with the specified result predicate.</returns>
        public Bounce<T> WhenResult(Func<T, bool> predicate) =>
            new Bounce<T>(Retries, BaseDelay, Timeout, DisableJitter, ShouldBounce, predicate, _maxDelay);

        /// <summary>
        /// Sets the maximum delay between retries.
        /// Caps every computed backoff, jittered or fixed. Defaults to 30 seconds.
        /// A base delay above this ceiling is clamped down to it, not rejected,
        /// matching how the fixed 30 second cap has always behaved.
        /// </summary>
        /// <param name="maxDelay">The delay ceiling. Must be positive.</param>
        /// <returns>A new Bounce configuration with the specified maximum delay.</returns>
        public Bounce<T> WithMaxDelay(TimeSpan maxDelay)
        {
            if (maxDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be positive");
            return new Bounce<T>(Retries, BaseDelay, Timeout, DisableJitter, ShouldBounce, ShouldRetryResult, maxDelay);
        }

        /// <summary>
        /// Sets the timeout for the operation.
        /// The timeout is honored only by the asynchronous ShotAsync overloads.
        /// The synchronous Shot overloads have no cancellation mechanism and ignore it.
        /// </summary>
        /// <param name="timeout">The timeout duration. Must be positive.</param>
        /// <returns>A new Bounce configuration with the specified timeout.</returns>
        public Bounce<T> WithTimeout(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive");
            return new Bounce<T>(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce, ShouldRetryResult, _maxDelay);
        }
    }
}
