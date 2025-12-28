using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Extension methods for fallback (Safety Pocket) pattern.
    /// Returns safe defaults when operations fail.
    /// </summary>
    public static class CaromFallbackExtensions
    {
        #region Synchronous Fallback

        /// <summary>
        /// Executes an action and returns a fallback value if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, T fallback)
        {
            try
            {
                return action();
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an action and invokes a fallback function if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, Func<T> fallbackFn)
        {
            try
            {
                return action();
            }
            catch
            {
                return fallbackFn();
            }
        }

        /// <summary>
        /// Executes an action and invokes a fallback function with exception if it fails.
        /// </summary>
        public static T Pocket<T>(this Func<T> action, Func<Exception, T> fallbackFn)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                return fallbackFn(ex);
            }
        }

        #endregion

        #region Asynchronous Fallback

        /// <summary>
        /// Executes an async action and returns a fallback value if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            T fallback,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an async action and invokes a fallback function if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<T> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallbackFn();
            }
        }

        /// <summary>
        /// Executes an async action and invokes an async fallback function if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return await fallbackFn().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Executes an async action and invokes an async fallback with exception if it fails.
        /// </summary>
        public static async Task<T> PocketAsync<T>(
            this Func<Task<T>> action,
            Func<Exception, Task<T>> fallbackFn,
            CancellationToken ct = default)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return await fallbackFn(ex).ConfigureAwait(false);
            }
        }

        #endregion

        #region Composition with Carom.Shot

        /// <summary>
        /// Executes a shot with retry, then falls back to a value if all retries fail.
        /// </summary>
        public static T ShotWithPocket<T>(
            Func<T> action,
            T fallback,
            int retries = 3,
            TimeSpan? baseDelay = null)
        {
            try
            {
                return global::Carom.Carom.Shot(action, retries, baseDelay);
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>
        /// Executes an async shot with retry, then falls back to a value if all retries fail.
        /// </summary>
        public static async Task<T> ShotWithPocketAsync<T>(
            Func<Task<T>> action,
            T fallback,
            int retries = 3,
            TimeSpan? baseDelay = null,
            CancellationToken ct = default)
        {
            try
            {
                return await global::Carom.Carom.ShotAsync(action, retries, baseDelay, ct: ct)
                    .ConfigureAwait(false);
            }
            catch when (!ct.IsCancellationRequested)
            {
                return fallback;
            }
        }

        #endregion
    }
}
