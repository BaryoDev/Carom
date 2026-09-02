using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Registering one resource key twice with different configuration is a programming error, and
    /// the store rejects it rather than picking a winner.
    /// </summary>
    /// <remarks>
    /// Silently keeping the first configuration is the dangerous outcome: the bulkhead then admits
    /// a concurrency nobody wrote down, and nothing anywhere reports it. The failure surfaces later
    /// as a resource being over- or under-protected, which is expensive to trace back to a
    /// duplicate registration.
    /// </remarks>
    public class CompartmentStoreConflictTests
    {
        private static Compartment Config(string key, int maxConcurrency, int queueDepth) =>
            Compartment.ForResource(key).WithMaxConcurrency(maxConcurrency).WithQueueDepth(queueDepth).Build();

        [Fact]
        public void A_second_registration_with_the_same_configuration_is_allowed()
        {
            var key = "same-" + Guid.NewGuid();

            var first = CompartmentStore.GetOrCreate(key, Config(key, 4, 2));
            var second = CompartmentStore.GetOrCreate(key, Config(key, 4, 2));

            // The positive control. Without it every test below would still pass if GetOrCreate
            // threw unconditionally, and "rejects everything" is not the behaviour being asserted.
            Assert.Same(first, second);
        }

        [Theory]
        [InlineData(8, 2, "MaxConcurrency")]
        [InlineData(4, 5, "QueueDepth")]
        public void A_conflicting_registration_is_rejected(int maxConcurrency, int queueDepth, string field)
        {
            var key = "conflict-" + Guid.NewGuid();
            CompartmentStore.GetOrCreate(key, Config(key, 4, 2));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                CompartmentStore.GetOrCreate(key, Config(key, maxConcurrency, queueDepth)));

            Assert.Contains(field, ex.Message);
            Assert.Contains(key, ex.Message);
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
                var a = Config(key, 4, 2);
                var b = Config(key, 8, 2);

                using var barrier = new Barrier(2);
                var failures = 0;

                Action<Compartment> register = cfg =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        CompartmentStore.GetOrCreate(key, cfg);
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
                // MaxConcurrency and must be told so. Zero failures means the loser silently
                // accepted the winner's concurrency limit.
                Assert.Equal(1, failures);
            }
        }
    }
}
