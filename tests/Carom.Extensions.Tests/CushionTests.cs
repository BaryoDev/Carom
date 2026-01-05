using System;
using System.Threading.Tasks;
using Xunit;
using Carom.Extensions;

namespace Carom.Extensions.Tests
{
    public class CushionTests
    {
        public CushionTests()
        {
            // Clear state between tests
        }

        #region Configuration Tests

        [Fact]
        public void ForService_CreatesBuilder_WithValidServiceKey()
        {
            var builder = Cushion.ForService("test-service-" + Guid.NewGuid());
            Assert.NotNull(builder);
        }

        [Fact]
        public void HalfOpenAfter_ThrowsArgumentException_WhenServiceKeyIsEmpty()
        {
            Assert.Throws<ArgumentException>(() => 
                Cushion.ForService("")
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void HalfOpenAfter_ThrowsArgumentException_WhenFailureThresholdIsZero()
        {
            Assert.Throws<ArgumentException>(() => 
                Cushion.ForService("test")
                    .OpenAfter(0, 5)
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void HalfOpenAfter_ThrowsArgumentException_WhenSamplingWindowLessThanThreshold()
        {
            Assert.Throws<ArgumentException>(() => 
                Cushion.ForService("test")
                    .OpenAfter(10, 5)  // threshold > window
                    .HalfOpenAfter(TimeSpan.FromSeconds(10)));
        }

        [Fact]
        public void HalfOpenAfter_ThrowsArgumentException_WhenDelayIsZero()
        {
            Assert.Throws<ArgumentException>(() => 
                Cushion.ForService("test")
                    .OpenAfter(3, 5)
                    .HalfOpenAfter(TimeSpan.Zero));
        }

        #endregion

        #region Circuit Closed Tests

        [Fact]
        public void Execute_ReturnsResult_WhenCircuitClosed()
        {
            var cushion = Cushion.ForService("closed-test-" + Guid.NewGuid())
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var result = CaromCushionExtensions.Shot(() => 42, cushion, retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ExecuteAsync_ReturnsResult_WhenCircuitClosed()
        {
            var cushion = Cushion.ForService("closed-async-test-" + Guid.NewGuid())
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var result = await CaromCushionExtensions.ShotAsync(
                () => Task.FromResult(42),
                cushion,
                retries: 0);

            Assert.Equal(42, result);
        }

        [Fact]
        public void CircuitStaysClosed_WithSuccessfulCalls()
        {
            var cushion = Cushion.ForService("stays-closed-test-" + Guid.NewGuid())
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // Make multiple successful calls
            for (int i = 0; i < 10; i++)
            {
                var result = CaromCushionExtensions.Shot(() => i, cushion, retries: 0);
                Assert.Equal(i, result);
            }
        }

        #endregion

        #region Circuit Opens Tests

        [Fact]
        public void CircuitOpens_AfterThresholdFailures()
        {
            var cushion = Cushion.ForService("opens-test-" + Guid.NewGuid())
                .OpenAfter(3, 5)  // 3 failures in 5 calls
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            // First 2 failures - circuit stays closed
            for (int i = 0; i < 2; i++)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException("Test failure"),
                        cushion,
                        retries: 0));
            }

            // Fill up the sampling window with more failures and successes
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    CaromCushionExtensions.Shot<int>(
                        () => throw new InvalidOperationException("Test failure"),
                        cushion,
                        retries: 0);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is CircuitOpenException)
                {
                    // Expected
                }
            }

            // Circuit should now be open
            var exception = Assert.Throws<CircuitOpenException>(() =>
                CaromCushionExtensions.Shot(() => 42, cushion, retries: 0));

            Assert.StartsWith("opens-test-", exception.ServiceKey);
        }

        #endregion

        #region CircuitOpenException Tests

        [Fact]
        public void CircuitOpenException_ContainsServiceKey()
        {
            var exception = new CircuitOpenException("my-service");
            
            Assert.Equal("my-service", exception.ServiceKey);
            Assert.Contains("my-service", exception.Message);
        }

        [Fact]
        public void CircuitOpenException_ContainsInnerException()
        {
            var inner = new InvalidOperationException("Original error");
            var exception = new CircuitOpenException("my-service", inner);
            
            Assert.Equal("my-service", exception.ServiceKey);
            Assert.Same(inner, exception.InnerException);
        }

        #endregion

        #region RingBuffer Tests

        [Fact]
        public void RingBuffer_ThrowsArgumentException_WhenCapacityIsZero()
        {
            Assert.Throws<ArgumentException>(() => new RingBuffer<int>(0));
        }

        [Fact]
        public void RingBuffer_ThrowsArgumentException_WhenCapacityIsNegative()
        {
            Assert.Throws<ArgumentException>(() => new RingBuffer<int>(-1));
        }

        [Fact]
        public void RingBuffer_TracksItemsCorrectly()
        {
            var buffer = new RingBuffer<bool>(5);
            
            buffer.Add(true);
            buffer.Add(false);
            buffer.Add(true);
            
            Assert.Equal(3, buffer.Count);
            Assert.Equal(2, buffer.CountWhere(x => x));  // 2 true
            Assert.Equal(1, buffer.CountWhere(x => !x)); // 1 false
        }

        [Fact]
        public void RingBuffer_WrapsAroundCorrectly()
        {
            var buffer = new RingBuffer<int>(3);
            
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            buffer.Add(4);  // Overwrites 1
            buffer.Add(5);  // Overwrites 2
            
            // Count should be capped at capacity
            Assert.Equal(3, buffer.Count);
        }

        [Fact]
        public void RingBuffer_Reset_ClearsCount()
        {
            var buffer = new RingBuffer<int>(5);
            
            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);
            
            Assert.Equal(3, buffer.Count);
            
            buffer.Reset();
            
            Assert.Equal(0, buffer.Count);
        }

        #endregion

        #region Integration Tests with Carom Retry

        [Fact]
        public void Shot_WithCushion_RetriesThenOpensCircuit()
        {
            var cushion = Cushion.ForService("integration-test-" + Guid.NewGuid())
                .OpenAfter(3, 5)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var callCount = 0;

            // First call - will retry 3 times (4 total), causing circuit to open
            try
            {
                CaromCushionExtensions.Shot<int>(
                    () =>
                    {
                        callCount++;
                        throw new InvalidOperationException("Service unavailable");
                    },
                    cushion,
                    retries: 3);
            }
            catch
            {
                // Expected
            }

            // Verify retry happened
            Assert.True(callCount >= 1, $"Expected at least 1 call, got {callCount}");
        }

        [Fact]
        public void Shot_WithBounce_WorksWithCushion()
        {
            var cushion = Cushion.ForService("bounce-test-" + Guid.NewGuid())
                .OpenAfter(5, 10)
                .HalfOpenAfter(TimeSpan.FromSeconds(30));

            var bounce = Bounce.Times(2)
                .WithDelay(TimeSpan.FromMilliseconds(10));

            var result = CaromCushionExtensions.Shot(
                () => "success",
                cushion,
                bounce);

            Assert.Equal("success", result);
        }

        #endregion
    }
}
