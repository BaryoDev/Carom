// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

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
        internal static bool DefaultShouldBounce(Exception ex) => ex is not CompartmentFullException;

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
            // Pass the whole Bounce so every field, present and future, reaches core.
            var effective = bounce.ShouldBounce == null ? bounce.When(DefaultShouldBounce) : bounce;
            return global::Carom.Carom.Shot(() => compartment.Execute(action), effective);
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
            // Pass the whole Bounce so every field, present and future, reaches core.
            var effective = bounce.ShouldBounce == null ? bounce.When(DefaultShouldBounce) : bounce;
            return global::Carom.Carom.ShotAsync(() => compartment.ExecuteAsync(action, ct), effective, ct);
        }
    }
}
