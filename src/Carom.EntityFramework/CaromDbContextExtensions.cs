using Microsoft.EntityFrameworkCore;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.EntityFramework
{
    /// <summary>
    /// Extension methods for using Carom with Entity Framework Core.
    /// </summary>
    public static class CaromDbContextExtensions
    {
        /// <summary>
        /// Saves changes with automatic retry on transient database errors.
        /// </summary>
        /// <param name="context">The DbContext.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of state entries written to the database.</returns>
        public static async Task<int> SaveChangesWithRetryAsync(
            this DbContext context,
            int retries = 3,
            CancellationToken cancellationToken = default)
        {
            return await global::Carom.Carom.ShotAsync(
                () => context.SaveChangesAsync(cancellationToken),
                retries,
                baseDelay: TimeSpan.FromMilliseconds(100),
                shouldBounce: IsTransientError,
                ct: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Saves changes with automatic retry using a Bounce configuration.
        /// </summary>
        /// <param name="context">The DbContext.</param>
        /// <param name="bounce">The retry configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The number of state entries written to the database.</returns>
        public static async Task<int> SaveChangesWithRetryAsync(
            this DbContext context,
            Bounce bounce,
            CancellationToken cancellationToken = default)
        {
            return await global::Carom.Carom.ShotAsync(
                () => context.SaveChangesAsync(cancellationToken),
                bounce,
                ct: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Executes a database operation with automatic retry on transient errors.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="context">The DbContext.</param>
        /// <param name="operation">The database operation to execute.</param>
        /// <param name="retries">Number of retry attempts (default: 3).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The operation result.</returns>
        public static async Task<T> ExecuteWithRetryAsync<T>(
            this DbContext context,
            Func<Task<T>> operation,
            int retries = 3,
            CancellationToken cancellationToken = default)
        {
            return await global::Carom.Carom.ShotAsync(
                operation,
                retries,
                baseDelay: TimeSpan.FromMilliseconds(100),
                shouldBounce: IsTransientError,
                ct: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Determines if an exception is a transient database error that should be retried.
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            // Check for DbUpdateException (EF Core specific)
            if (ex is DbUpdateException)
                return true;

            // Check exception message for common transient error patterns
            var message = ex.Message.ToLowerInvariant();
            
            return message.Contains("timeout") ||
                   message.Contains("deadlock") ||
                   message.Contains("connection") ||
                   message.Contains("network") ||
                   message.Contains("transport");
        }
    }
}
