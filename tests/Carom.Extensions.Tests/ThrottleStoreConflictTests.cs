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
    /// Silently keeping the first configuration is the dangerous outcome: the throttle then runs at
    /// a rate nobody wrote down, and nothing anywhere reports it. The failure surfaces later as
    /// traffic being shaped wrongly, which is expensive to trace back to a duplicate registration.
    /// </remarks>
    public class ThrottleStoreConflictTests
    {
        private static Throttle Config(string key, int max, int burst, TimeSpan window) =>
            Throttle.ForService(key).WithRate(max, window).WithBurst(burst).Build();

        [Fact]
        public void A_second_registration_with_the_same_configuration_is_allowed()
        {
            var key = "same-" + Guid.NewGuid();
            var config = Config(key, 100, 20, TimeSpan.FromSeconds(1));

            var first = ThrottleStore.GetOrCreate(key, config);
            var second = ThrottleStore.GetOrCreate(key, Config(key, 100, 20, TimeSpan.FromSeconds(1)));

            // The positive control. Without it every test below would still pass if GetOrCreate
            // threw unconditionally, and "rejects everything" is not the behaviour being asserted.
            Assert.Same(first, second);
        }

        [Theory]
        [InlineData(200, 20, 1, "MaxRequests")]
        [InlineData(100, 50, 1, "BurstSize")]
        [InlineData(100, 20, 5, "TimeWindow")]
        public void A_conflicting_registration_is_rejected(int max, int burst, int windowSeconds, string field)
        {
            var key = "conflict-" + Guid.NewGuid();
            ThrottleStore.GetOrCreate(key, Config(key, 100, 20, TimeSpan.FromSeconds(1)));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ThrottleStore.GetOrCreate(key, Config(key, max, burst, TimeSpan.FromSeconds(windowSeconds))));

            Assert.Contains(field, ex.Message);
            Assert.Contains(key, ex.Message);
        }

        /// <summary>
        /// The lost-race path enforces the same rule as the sequential one.
        /// </summary>
        /// <remarks>
        /// This is the case the original fix missed. Both callers can miss <c>TryGetValue</c>, and
        /// only one of them wins <c>GetOrAdd</c>; the loser is handed the winner's entry. Validating
        /// only the <c>TryGetValue</c> branch left a conflicting registration throwing reliably when
        /// it happened sequentially and passing silently when it happened under load, which presents
        /// as an intermittent fault rather than a configuration error.
        ///
        /// Repeated because a race is not reproducible on demand: one attempt that happens to
        /// serialise proves nothing. A barrier gets both threads to the same point first, and the
        /// loop makes an accidental pass unlikely rather than merely possible.
        /// </remarks>
        [Fact]
        public void Exactly_one_of_two_racing_conflicting_registrations_succeeds()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var key = "race-" + Guid.NewGuid();
                var a = Config(key, 100, 20, TimeSpan.FromSeconds(1));
                var b = Config(key, 100, 20, TimeSpan.FromSeconds(5));

                using var barrier = new Barrier(2);
                var failures = 0;

                Action<Throttle> register = cfg =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        ThrottleStore.GetOrCreate(key, cfg);
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref failures);
                    }
                };

                Task.WaitAll(
                    Task.Run(() => register(a)),
                    Task.Run(() => register(b)));

                // Whichever thread wins the GetOrAdd, the other asked for a different TimeWindow and
                // must be told so. Zero failures means the loser silently accepted the winner's
                // refill interval.
                Assert.Equal(1, failures);
            }
        }
    }
}
