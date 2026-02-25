using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for integrating Bulkhead (Compartment) with Carom retry logic.
    /// </summary>
    public static class CaromCompartmentExtensions
    {
        private static bool DefaultShouldBounce(Exception ex) => ex is not CompartmentFullException;

        /// <summary>
        /// Executes a synchronous shot with bulkhead protection.
        /// </summary>
        public static T Shot<T>(
            Func<T> action,
            Compartment compartment,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return global::Carom.Carom.Shot(
                () => compartment.Execute(action),
                retries,
                baseDelay,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter);
        }

        /// <summary>
        /// Executes a synchronous shot with bulkhead and Bounce configuration.
        /// </summary>
        public static T Shot<T>(Func<T> action, Compartment compartment, Bounce bounce)
        {
            return global::Carom.Carom.Shot(
                () => compartment.Execute(action),
                bounce);
        }

        /// <summary>
        /// Executes an asynchronous shot with bulkhead protection.
        /// </summary>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Compartment compartment,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return await global::Carom.Carom.ShotAsync(
                () => compartment.ExecuteAsync(action, ct),
                retries,
                baseDelay,
                timeout: null,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter,
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an asynchronous shot with bulkhead and Bounce configuration.
        /// </summary>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Compartment compartment,
            Bounce bounce,
            CancellationToken ct = default)
        {
            return global::Carom.Carom.ShotAsync(
                () => compartment.ExecuteAsync(action, ct),
                bounce,
                ct);
        }
    }
}
