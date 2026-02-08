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

        private Bounce(int retries, TimeSpan baseDelay, TimeSpan? timeout, bool disableJitter, Func<Exception, bool>? shouldBounce)
        {
            Retries = retries;
            BaseDelay = baseDelay;
            Timeout = timeout;
            DisableJitter = disableJitter;
            ShouldBounce = shouldBounce;
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
        /// <param name="delay">The base delay.</param>
        /// <returns>A new Bounce configuration with the specified delay.</returns>
        public Bounce WithDelay(TimeSpan delay) =>
            new Bounce(Retries, delay, Timeout, DisableJitter, ShouldBounce);

        /// <summary>
        /// Disables jitter, using fixed exponential backoff instead.
        /// WARNING: This can cause Thundering Herd issues and is not recommended.
        /// </summary>
        /// <returns>A new Bounce configuration with jitter disabled.</returns>
        public Bounce WithoutJitter() =>
            new Bounce(Retries, BaseDelay, Timeout, disableJitter: true, ShouldBounce);

        /// <summary>
        /// Sets a predicate to determine which exceptions should trigger a retry.
        /// </summary>
        /// <param name="predicate">The exception predicate.</param>
        /// <returns>A new Bounce configuration with the specified predicate.</returns>
        public Bounce When(Func<Exception, bool> predicate) =>
            new Bounce(Retries, BaseDelay, Timeout, DisableJitter, predicate);

        /// <summary>
        /// Sets the timeout for the operation.
        /// </summary>
        /// <param name="timeout">The timeout duration.</param>
        /// <returns>A new Bounce configuration with the specified timeout.</returns>
        public Bounce WithTimeout(TimeSpan timeout) =>
            new Bounce(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce);
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

        private Bounce(int retries, TimeSpan baseDelay, TimeSpan? timeout, bool disableJitter,
            Func<Exception, bool>? shouldBounce, Func<T, bool>? shouldRetryResult)
        {
            Retries = retries;
            BaseDelay = baseDelay;
            Timeout = timeout;
            DisableJitter = disableJitter;
            ShouldBounce = shouldBounce;
            ShouldRetryResult = shouldRetryResult;
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
        /// <param name="delay">The base delay.</param>
        /// <returns>A new Bounce configuration with the specified delay.</returns>
        public Bounce<T> WithDelay(TimeSpan delay) =>
            new Bounce<T>(Retries, delay, Timeout, DisableJitter, ShouldBounce, ShouldRetryResult);

        /// <summary>
        /// Disables jitter, using fixed exponential backoff instead.
        /// WARNING: This can cause Thundering Herd issues and is not recommended.
        /// </summary>
        /// <returns>A new Bounce configuration with jitter disabled.</returns>
        public Bounce<T> WithoutJitter() =>
            new Bounce<T>(Retries, BaseDelay, Timeout, disableJitter: true, ShouldBounce, ShouldRetryResult);

        /// <summary>
        /// Sets a predicate to determine which exceptions should trigger a retry.
        /// </summary>
        /// <param name="predicate">The exception predicate.</param>
        /// <returns>A new Bounce configuration with the specified predicate.</returns>
        public Bounce<T> When(Func<Exception, bool> predicate) =>
            new Bounce<T>(Retries, BaseDelay, Timeout, DisableJitter, predicate, ShouldRetryResult);

        /// <summary>
        /// Sets a predicate to determine which results should trigger a retry.
        /// </summary>
        /// <param name="predicate">The result predicate (returns true to retry).</param>
        /// <returns>A new Bounce configuration with the specified result predicate.</returns>
        public Bounce<T> WhenResult(Func<T, bool> predicate) =>
            new Bounce<T>(Retries, BaseDelay, Timeout, DisableJitter, ShouldBounce, predicate);

        /// <summary>
        /// Sets the timeout for the operation.
        /// </summary>
        /// <param name="timeout">The timeout duration.</param>
        /// <returns>A new Bounce configuration with the specified timeout.</returns>
        public Bounce<T> WithTimeout(TimeSpan timeout) =>
            new Bounce<T>(Retries, BaseDelay, timeout, DisableJitter, ShouldBounce, ShouldRetryResult);
    }
}
