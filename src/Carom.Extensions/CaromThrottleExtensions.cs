using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for integrating Rate Limiting (Throttle) with Carom retry logic.
    /// </summary>
    public static class CaromThrottleExtensions
    {
        private static bool DefaultShouldBounce(Exception ex) => ex is not ThrottledException;

        /// <summary>
        /// Executes a synchronous shot with rate limiting.
        /// </summary>
        public static T Shot<T>(
            Func<T> action,
            Throttle throttle,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false)
        {
            return global::Carom.Carom.Shot(
                () => throttle.Execute(action),
                retries,
                baseDelay,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter);
        }

        /// <summary>
        /// Executes a synchronous shot with rate limiting and Bounce configuration.
        /// </summary>
        public static T Shot<T>(Func<T> action, Throttle throttle, Bounce bounce)
        {
            return global::Carom.Carom.Shot(
                () => throttle.Execute(action),
                bounce);
        }

        /// <summary>
        /// Executes an asynchronous shot with rate limiting.
        /// </summary>
        public static async Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Throttle throttle,
            int retries = 3,
            TimeSpan? baseDelay = null,
            Func<Exception, bool>? shouldBounce = null,
            bool disableJitter = false,
            CancellationToken ct = default)
        {
            return await global::Carom.Carom.ShotAsync(
                () => throttle.ExecuteAsync(action),
                retries,
                baseDelay,
                timeout: null,
                shouldBounce ?? DefaultShouldBounce,
                disableJitter,
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes an asynchronous shot with rate limiting and Bounce configuration.
        /// </summary>
        public static Task<T> ShotAsync<T>(
            Func<Task<T>> action,
            Throttle throttle,
            Bounce bounce,
            CancellationToken ct = default)
        {
            return global::Carom.Carom.ShotAsync(
                () => throttle.ExecuteAsync(action),
                bounce,
                ct);
        }
    }
}
