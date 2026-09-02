// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System;
using System.Threading.Tasks;
using Carom.Extensions;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// CAR-01: the breaker counted every exception, so a bug in calling code could
    /// open the circuit for a healthy dependency. CushionBuilder.When decides which
    /// exceptions are the dependency's fault; the rest pass through without
    /// touching the circuit. Default: everything except OperationCanceledException.
    /// </summary>
    public class TripPredicateTests
    {
        private class DependencyDownException : Exception { }

        [Fact]
        public void Excluded_exceptions_never_open_the_circuit()
        {
            var key = "car01-excluded-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .When(ex => ex is DependencyDownException)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            // Caller-side bugs the full width of the sampling window.
            for (int i = 0; i < 5; i++)
            {
                Assert.Throws<ArgumentException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new ArgumentException("caller bug"), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));

            // Real dependency failures still open it.
            for (int i = 0; i < 2; i++)
            {
                Assert.Throws<DependencyDownException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new DependencyDownException(), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public async Task Excluded_exceptions_never_open_the_circuit_async()
        {
            var key = "car01-excluded-async-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .When(ex => ex is DependencyDownException)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            for (int i = 0; i < 5; i++)
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    CaromCushionExtensions.ShotAsync<int>(
                        () => throw new ArgumentException("caller bug"), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));

            for (int i = 0; i < 2; i++)
            {
                await Assert.ThrowsAsync<DependencyDownException>(() =>
                    CaromCushionExtensions.ShotAsync<int>(
                        () => throw new DependencyDownException(), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public void Default_predicate_ignores_cancellation_and_trips_on_everything_else()
        {
            var key = "car01-default-" + Guid.NewGuid();
            var cushion = Cushion.ForService(key)
                .OpenAfter(failures: 2, trackingLast: 5)
                .HalfOpenAfter(TimeSpan.FromMinutes(10));

            for (int i = 0; i < 5; i++)
            {
                Assert.Throws<OperationCanceledException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new OperationCanceledException(), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Closed, Cushion.GetState(key));

            for (int i = 0; i < 2; i++)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException("boom"), cushion, retries: 0));
            }

            Assert.Equal(CircuitState.Open, Cushion.GetState(key));
        }

        [Fact]
        public void When_rejects_a_null_predicate()
        {
            Assert.Throws<ArgumentNullException>(() =>
                Cushion.ForService("car01-null-" + Guid.NewGuid()).When(null!));
        }
    }
}
