using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Extensions.Tests
{
    /// <summary>
    /// Tests for RingBuffer seqlock pattern and thread safety.
    /// </summary>
    public class RingBufferTests
    {
        [Fact]
        public void RingBuffer_BasicAddAndCount_Works()
        {
            var buffer = new RingBuffer<int>(10);

            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            Assert.Equal(3, buffer.Count);
            Assert.Equal(3, buffer.CountWhere(x => x > 0));
        }

        [Fact]
        public void RingBuffer_CountWhere_FiltersCorrectly()
        {
            var buffer = new RingBuffer<bool>(10);

            buffer.Add(true);
            buffer.Add(false);
            buffer.Add(true);
            buffer.Add(false);
            buffer.Add(true);

            Assert.Equal(5, buffer.Count);
            Assert.Equal(3, buffer.CountWhere(x => x));
            Assert.Equal(2, buffer.CountWhere(x => !x));
        }

        [Fact]
        public void RingBuffer_WrapsAround_Correctly()
        {
            var buffer = new RingBuffer<int>(5);

            // Add more items than capacity
            for (int i = 1; i <= 10; i++)
            {
                buffer.Add(i);
            }

            Assert.Equal(5, buffer.Count);
            // The buffer should contain 6, 7, 8, 9, 10 (the last 5)
            Assert.Equal(5, buffer.CountWhere(x => x > 5));
        }

        [Fact]
        public void RingBuffer_Reset_ClearsBuffer()
        {
            var buffer = new RingBuffer<int>(10);

            buffer.Add(1);
            buffer.Add(2);
            buffer.Add(3);

            Assert.Equal(3, buffer.Count);

            buffer.Reset();

            Assert.Equal(0, buffer.Count);
            Assert.Equal(0, buffer.CountWhere(x => true));
        }

        [Fact]
        public void RingBuffer_ConcurrentAddAndCount_NoRaceCondition()
        {
            var buffer = new RingBuffer<int>(100);
            var exceptions = new List<Exception>();
            var writerRunning = true;

            // Start writer thread
            var writer = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 10000; i++)
                    {
                        buffer.Add(i % 2); // Add 0 or 1
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
                finally
                {
                    writerRunning = false;
                }
            });

            // Start reader thread
            var reader = new Thread(() =>
            {
                try
                {
                    while (writerRunning)
                    {
                        // This should not throw or return inconsistent results
                        var total = buffer.Count;
                        var ones = buffer.CountWhere(x => x == 1);
                        var zeros = buffer.CountWhere(x => x == 0);

                        // Due to concurrent writes, we can't assert exact values,
                        // but we shouldn't get exceptions
                        if (total > 100)
                        {
                            // Count should never exceed capacity
                            throw new Exception($"Count {total} exceeds capacity 100");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                }
            });

            writer.Start();
            reader.Start();

            writer.Join();
            reader.Join();

            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task RingBuffer_HighContention_MaintainsConsistency()
        {
            var buffer = new RingBuffer<int>(50);
            var tasks = new List<Task>();
            var exceptions = new List<Exception>();

            // Create multiple concurrent writers
            for (int w = 0; w < 4; w++)
            {
                var writerId = w;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 1000; i++)
                        {
                            buffer.Add(writerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                }));
            }

            // Create multiple concurrent readers
            for (int r = 0; r < 4; r++)
            {
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 500; i++)
                        {
                            var count = buffer.CountWhere(x => x >= 0);
                            if (count < 0 || count > 50)
                            {
                                throw new Exception($"Invalid count: {count}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            Assert.Equal(50, buffer.Count); // Should be at capacity
        }

        [Fact]
        public void RingBuffer_ZeroCapacity_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new RingBuffer<int>(0));
        }

        [Fact]
        public void RingBuffer_NegativeCapacity_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() => new RingBuffer<int>(-1));
        }
    }
}
