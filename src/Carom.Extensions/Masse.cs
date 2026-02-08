// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;

namespace Carom.Extensions
{
    /// <summary>
    /// Configuration for hedging (Masse) pattern.
    /// Hedging launches parallel backup requests after a delay to improve latency.
    /// Named after the "masse" shot in billiards where the cue ball takes multiple paths.
    /// </summary>
    public readonly struct Masse
    {
        /// <summary>
        /// Maximum number of hedged attempts (including the initial request).
        /// </summary>
        public int MaxHedgedAttempts { get; }

        /// <summary>
        /// Delay before launching each additional hedged attempt.
        /// </summary>
        public TimeSpan HedgeDelay { get; }

        /// <summary>
        /// Whether to cancel pending attempts when one succeeds.
        /// Default is true.
        /// </summary>
        public bool CancelPendingOnSuccess { get; }

        /// <summary>
        /// Optional predicate to determine if a result should trigger additional hedges.
        /// If null, hedging continues regardless of intermediate results.
        /// </summary>
        public Func<object?, bool>? ShouldHedge { get; }

        private Masse(int maxHedgedAttempts, TimeSpan hedgeDelay, bool cancelPendingOnSuccess, Func<object?, bool>? shouldHedge)
        {
            if (maxHedgedAttempts < 1)
                throw new ArgumentOutOfRangeException(nameof(maxHedgedAttempts), "Must be at least 1");
            if (hedgeDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(hedgeDelay), "Cannot be negative");

            MaxHedgedAttempts = maxHedgedAttempts;
            HedgeDelay = hedgeDelay;
            CancelPendingOnSuccess = cancelPendingOnSuccess;
            ShouldHedge = shouldHedge;
        }

        /// <summary>
        /// Creates a hedging configuration with the specified number of attempts.
        /// </summary>
        /// <param name="count">Maximum number of parallel attempts (including initial).</param>
        /// <returns>A new Masse configuration.</returns>
        public static Masse WithAttempts(int count) =>
            new Masse(count, TimeSpan.FromMilliseconds(200), cancelPendingOnSuccess: true, shouldHedge: null);

        /// <summary>
        /// Sets the delay before launching each hedged attempt.
        /// </summary>
        /// <param name="delay">Delay between hedged attempts.</param>
        /// <returns>A new Masse configuration with the specified delay.</returns>
        public Masse After(TimeSpan delay) =>
            new Masse(MaxHedgedAttempts, delay, CancelPendingOnSuccess, ShouldHedge);

        /// <summary>
        /// Configures whether to cancel pending attempts when one succeeds.
        /// </summary>
        /// <param name="cancel">True to cancel pending attempts on success.</param>
        /// <returns>A new Masse configuration.</returns>
        public Masse WithCancellation(bool cancel) =>
            new Masse(MaxHedgedAttempts, HedgeDelay, cancel, ShouldHedge);

        /// <summary>
        /// Sets a predicate to determine if hedging should continue based on intermediate results.
        /// </summary>
        /// <param name="predicate">Predicate that returns true to continue hedging.</param>
        /// <returns>A new Masse configuration with the specified predicate.</returns>
        public Masse When(Func<object?, bool> predicate) =>
            new Masse(MaxHedgedAttempts, HedgeDelay, CancelPendingOnSuccess, predicate);
    }
}
