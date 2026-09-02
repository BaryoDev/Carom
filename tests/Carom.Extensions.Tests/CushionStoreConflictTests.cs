using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Registering one service key twice with different configuration is a programming error, and
    /// the store rejects it rather than picking a winner.
    /// </summary>
    /// <remarks>
    /// Silently keeping the first configuration is the dangerous outcome: the circuit then trips at
    /// a threshold nobody wrote down, or recovers on someone else's half-open delay. The failure
    /// surfaces later as a breaker misbehaving, which is expensive to trace back to a duplicate
    /// registration.
    /// </remarks>
    public class CushionStoreConflictTests
    {
        private static Cushion Config(string key, int failures, int trackingLast,
            TimeSpan halfOpenDelay, TimeSpan samplingDuration) =>
            Cushion.ForService(key)
                .OpenAfter(failures, trackingLast)
                .WithinLast(samplingDuration)
                .HalfOpenAfter(halfOpenDelay);

        private static Cushion Baseline(string key) =>
            Config(key, 2, 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

        [Fact]
        public void A_second_registration_with_the_same_configuration_is_allowed()
        {
            var key = "same-" + Guid.NewGuid();

            var first = CushionStore.GetOrCreate(key, Baseline(key));
            var second = CushionStore.GetOrCreate(key, Baseline(key));

            // The positive control. Without it every test below would still pass if GetOrCreate
            // threw unconditionally, and "rejects everything" is not the behaviour being asserted.
            Assert.Same(first, second);
        }

        [Theory]
        [InlineData(3, 10, 30, 60, "FailureThreshold")]
        [InlineData(2, 20, 30, 60, "SamplingWindow")]
        [InlineData(2, 10, 60, 60, "HalfOpenDelay")]
        [InlineData(2, 10, 30, 120, "SamplingDuration")]
        public void A_conflicting_registration_is_rejected(
            int failures, int trackingLast, int halfOpenSeconds, int samplingSeconds, string field)
        {
            var key = "conflict-" + Guid.NewGuid();
            CushionStore.GetOrCreate(key, Baseline(key));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                CushionStore.GetOrCreate(key, Config(key, failures, trackingLast,
                    TimeSpan.FromSeconds(halfOpenSeconds), TimeSpan.FromSeconds(samplingSeconds))));

            Assert.Contains(field, ex.Message);
            Assert.Contains(key, ex.Message);
        }

        [Fact]
        public void Registrations_differing_only_in_the_trip_predicate_are_allowed()
        {
            var key = "predicate-" + Guid.NewGuid();

            // ShouldTrip is a delegate and cannot be value-compared, so it is deliberately
            // excluded from the conflict check. Two call sites with equivalent lambdas would
            // otherwise always conflict, because each lambda is a distinct instance.
            var first = CushionStore.GetOrCreate(key, Cushion.ForService(key)
                .OpenAfter(2, 10).When(ex => ex is TimeoutException).HalfOpenAfter(TimeSpan.FromSeconds(30)));
            var second = CushionStore.GetOrCreate(key, Cushion.ForService(key)
                .OpenAfter(2, 10).When(ex => ex is InvalidOperationException).HalfOpenAfter(TimeSpan.FromSeconds(30)));

            Assert.Same(first, second);
        }

        /// <summary>
        /// The lost-race path enforces the same rule as the sequential one.
        /// </summary>
        /// <remarks>
        /// Both callers can miss <c>TryGetValue</c>, and only one of them wins <c>GetOrAdd</c>; the
        /// loser is handed the winner's entry. Validating only the <c>TryGetValue</c> branch leaves
        /// a conflicting registration throwing reliably when it happens sequentially and passing
        /// silently when it happens under load, which presents as an intermittent fault rather than
        /// a configuration error.
        ///
        /// Repeated because a race is not reproducible on demand: one attempt that happens to
        /// serialise proves nothing. A barrier gets both threads to the same point first, and the
        /// loop makes an accidental pass unlikely rather than merely possible.
        /// </remarks>
        [Fact]
        public async Task Exactly_one_of_two_racing_conflicting_registrations_succeeds()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var key = "race-" + Guid.NewGuid();
                var a = Config(key, 2, 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
                var b = Config(key, 5, 10, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

                using var barrier = new Barrier(2);
                var failures = 0;

                Action<Cushion> register = cfg =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        CushionStore.GetOrCreate(key, cfg);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref failures);
                    }
                };

                await Task.WhenAll(
                    Task.Run(() => register(a)),
                    Task.Run(() => register(b)));

                // Whichever thread wins the GetOrAdd, the other asked for a different
                // FailureThreshold and must be told so. Zero failures means the loser silently
                // accepted the winner's trip threshold.
                Assert.Equal(1, failures);
            }
        }
    }
}
