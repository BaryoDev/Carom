using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for integrating Circuit Breaker (Cushion) with Carom retry logic.
    /// </summary>
    public static class CaromCushionExtensions
    {
        private static bool DefaultShouldBounce(Exception ex) => ex is not CircuitOpenException;

        /// <summary>
        /// Executes a synchronous shot with circuit breaker protection.
        /// Retry logic wraps circuit breaker logic.
        /// </summary>
        public static T Shot<T>(
            Func<T> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return global::Carom.Carom.Shot(
                () => cushion.Execute(action),
                retries,
                baseDelay,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter);
        }

        /// <summary>
        /// Executes a synchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static T Shot<T>(Func<T> action, Cushion cushion, Bounce bounce)
        {
            return global::Carom.Carom.Shot(
                () => cushion.Execute(action),
                bounce);
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker protection.
        /// </summary>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return await global::Carom.Carom.ShotAsync(
                () => cushion.ExecuteAsync(action),
                retries,
                baseDelay,
                timeout: null,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter,
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an asynchronous shot with circuit breaker and Bounce configuration.
        /// </summary>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Cushion cushion,
            Bounce bounce,
            CancellationToken ct = default)
        {
            return global::Carom.Carom.ShotAsync(
                () => cushion.ExecuteAsync(action),
                bounce,
                ct);
        }
    }
}
