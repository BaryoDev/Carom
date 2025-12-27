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

        private Bounce(int retries, TimeSpan baseDelay, bool disableJitter, Func<Exception, bool>? shouldBounce)
        {
            Retries = retries;
            BaseDelay = baseDelay;
            DisableJitter = disableJitter;
            ShouldBounce = shouldBounce;
        }

        /// <summary>
        /// Creates a bounce configuration with the specified number of retries.
        /// </summary>
        /// <param name="count">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce Times(int count = 3) =>
            new Bounce(count, JitterStrategy.DefaultBaseDelay, disableJitter: false, shouldBounce: null);

        /// <summary>
        /// Creates a bounce configuration that retries on the specified exception type.
        /// </summary>
        /// <typeparam name="TException">The exception type to retry on.</typeparam>
        /// <param name="retries">The number of retry attempts (default: 3).</param>
        /// <returns>A new Bounce configuration.</returns>
        public static Bounce On<TException>(int retries = 3) where TException : Exception =>
            new Bounce(retries, JitterStrategy.DefaultBaseDelay, disableJitter: false, 
                shouldBounce: ex => ex is TException);

        /// <summary>
        /// Sets the base delay between retries.
        /// </summary>
        /// <param name="delay">The base delay.</param>
        /// <returns>A new Bounce configuration with the specified delay.</returns>
        public Bounce WithDelay(TimeSpan delay) =>
            new Bounce(Retries, delay, DisableJitter, ShouldBounce);

        /// <summary>
        /// Disables jitter, using fixed exponential backoff instead.
        /// WARNING: This can cause Thundering Herd issues and is not recommended.
        /// </summary>
        /// <returns>A new Bounce configuration with jitter disabled.</returns>
        public Bounce WithoutJitter() =>
            new Bounce(Retries, BaseDelay, disableJitter: true, ShouldBounce);

        /// <summary>
        /// Sets a predicate to determine which exceptions should trigger a retry.
        /// </summary>
        /// <param name="predicate">The exception predicate.</param>
        /// <returns>A new Bounce configuration with the specified predicate.</returns>
        public Bounce When(Func<Exception, bool> predicate) =>
            new Bounce(Retries, BaseDelay, DisableJitter, predicate);
    }
}
