// Example: Resilient Microservice with Carom
// This example demonstrates real-world usage patterns for a payment processing service.

using System.Diagnostics;
using Carom;
using Carom.Extensions;

Console.WriteLine("=== Carom Resilience Library - Real World Example ===\n");

// Scenario 1: Payment Gateway with Circuit Breaker
await PaymentGatewayExample();

// Scenario 2: Database Connection Pool with Bulkhead
await DatabasePoolExample();

// Scenario 3: External API Rate Limiting
await ExternalApiExample();

// Scenario 4: Composing All Patterns Together
await CompositeResilienceExample();

// Scenario 5: Retry with Exponential Backoff
await RetryExample();

Console.WriteLine("\n=== All Examples Completed ===");

static async Task PaymentGatewayExample()
{
    Console.WriteLine("--- Scenario 1: Payment Gateway with Circuit Breaker ---\n");

    // Configure circuit breaker: open after 3 failures in 5 calls, half-open after 5 seconds
    var paymentCircuit = Cushion.ForService("payment-gateway")
        .OpenAfter(failures: 3, within: 5)
        .HalfOpenAfter(TimeSpan.FromSeconds(5));

    var successCount = 0;
    var failureCount = 0;
    var circuitOpenCount = 0;

    // Simulate payment processing with intermittent failures
    for (int i = 0; i < 10; i++)
    {
        try
        {
            var result = await CaromCushionExtensions.ShotAsync(
                async () =>
                {
                    // Simulate payment processing with 40% failure rate
                    await Task.Delay(10);
                    if (Random.Shared.NextDouble() < 0.4)
                        throw new HttpRequestException("Payment gateway timeout");
                    return new { TransactionId = Guid.NewGuid(), Amount = 99.99m };
                },
                paymentCircuit,
                retries: 2);

            Console.WriteLine($"  Payment {i+1}: SUCCESS - Transaction {result.TransactionId}");
            successCount++;
        }
        catch (CircuitOpenException)
        {
            Console.WriteLine($"  Payment {i+1}: CIRCUIT OPEN - Fast fail protecting gateway");
            circuitOpenCount++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Payment {i+1}: FAILED - {ex.Message}");
            failureCount++;
        }
    }

    Console.WriteLine($"\n  Summary: {successCount} success, {failureCount} failures, {circuitOpenCount} circuit open\n");
}

static async Task DatabasePoolExample()
{
    Console.WriteLine("--- Scenario 2: Database Connection Pool with Bulkhead ---\n");

    // Configure bulkhead: max 3 concurrent database connections
    var dbPool = Compartment.ForResource("database-pool")
        .WithMaxConcurrency(3)
        .Build();

    var tasks = new List<Task>();
    var completedCount = 0;
    var rejectedCount = 0;

    // Simulate 10 concurrent database queries
    for (int i = 0; i < 10; i++)
    {
        int queryId = i;
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                var result = await CaromCompartmentExtensions.ShotAsync(
                    async () =>
                    {
                        Console.WriteLine($"  Query {queryId}: Executing...");
                        await Task.Delay(100); // Simulate query time
                        return $"Result for query {queryId}";
                    },
                    dbPool,
                    retries: 0);

                Console.WriteLine($"  Query {queryId}: Completed - {result}");
                Interlocked.Increment(ref completedCount);
            }
            catch (CompartmentFullException)
            {
                Console.WriteLine($"  Query {queryId}: REJECTED - Pool full");
                Interlocked.Increment(ref rejectedCount);
            }
        }));
    }

    await Task.WhenAll(tasks);
    Console.WriteLine($"\n  Summary: {completedCount} completed, {rejectedCount} rejected (pool protected)\n");
}

static async Task ExternalApiExample()
{
    Console.WriteLine("--- Scenario 3: External API Rate Limiting ---\n");

    // Configure rate limiter: 5 requests per second
    var apiThrottle = Throttle.ForService("weather-api")
        .WithRate(5, TimeSpan.FromSeconds(1))
        .WithBurst(5)
        .Build();

    var allowedCount = 0;
    var throttledCount = 0;

    // Try to make 15 rapid API calls
    var stopwatch = Stopwatch.StartNew();
    for (int i = 0; i < 15; i++)
    {
        try
        {
            var temp = await CaromThrottleExtensions.ShotAsync(
                async () =>
                {
                    await Task.Delay(5); // Simulate API call
                    return Random.Shared.Next(60, 90);
                },
                apiThrottle,
                retries: 0);

            Console.WriteLine($"  Request {i+1}: {temp}F (allowed)");
            allowedCount++;
        }
        catch (ThrottledException)
        {
            Console.WriteLine($"  Request {i+1}: THROTTLED");
            throttledCount++;
        }
    }
    stopwatch.Stop();

    Console.WriteLine($"\n  Summary: {allowedCount} allowed, {throttledCount} throttled in {stopwatch.ElapsedMilliseconds}ms\n");
}

static async Task CompositeResilienceExample()
{
    Console.WriteLine("--- Scenario 4: Composite Resilience (All Patterns) ---\n");

    // Layer 1: Rate limiting to protect external API
    var throttle = Throttle.ForService("composite-api")
        .WithRate(10, TimeSpan.FromSeconds(1))
        .WithBurst(10)
        .Build();

    // Layer 2: Bulkhead to limit concurrent operations
    var compartment = Compartment.ForResource("composite-pool")
        .WithMaxConcurrency(3)
        .Build();

    // Layer 3: Circuit breaker for failure protection
    var cushion = Cushion.ForService("composite-service")
        .OpenAfter(failures: 2, within: 5)
        .HalfOpenAfter(TimeSpan.FromSeconds(10));

    Console.WriteLine("  Processing orders with layered resilience...\n");

    var tasks = new List<Task>();
    var succeeded = 0;
    var failed = 0;

    for (int i = 0; i < 10; i++)
    {
        int orderId = i;
        tasks.Add(Task.Run(async () =>
        {
            try
            {
                // Outer: Rate limiting
                var result = await CaromThrottleExtensions.ShotAsync(
                    async () =>
                    {
                        // Middle: Bulkhead
                        return await CaromCompartmentExtensions.ShotAsync(
                            async () =>
                            {
                                // Inner: Circuit breaker with retry
                                return await CaromCushionExtensions.ShotAsync(
                                    async () =>
                                    {
                                        await Task.Delay(20);
                                        if (orderId % 4 == 0)
                                            throw new InvalidOperationException("Order processing failed");
                                        return new { OrderId = orderId, Status = "Processed" };
                                    },
                                    cushion,
                                    retries: 1);
                            },
                            compartment,
                            retries: 0);
                    },
                    throttle,
                    retries: 0);

                Console.WriteLine($"  Order {orderId}: {result.Status}");
                Interlocked.Increment(ref succeeded);
            }
            catch (Exception ex)
            {
                var reason = ex switch
                {
                    ThrottledException => "Rate limited",
                    CompartmentFullException => "Pool exhausted",
                    CircuitOpenException => "Circuit open",
                    _ => ex.Message
                };
                Console.WriteLine($"  Order {orderId}: FAILED ({reason})");
                Interlocked.Increment(ref failed);
            }
        }));
    }

    await Task.WhenAll(tasks);
    Console.WriteLine($"\n  Summary: {succeeded} succeeded, {failed} failed\n");
}

static async Task RetryExample()
{
    Console.WriteLine("--- Scenario 5: Retry with Exponential Backoff ---\n");

    var attemptCount = 0;

    try
    {
        // Retry up to 3 times with exponential backoff and jitter
        var result = await Carom.Carom.ShotAsync(
            async () =>
            {
                attemptCount++;
                Console.WriteLine($"  Attempt {attemptCount}...");

                if (attemptCount < 3)
                {
                    throw new HttpRequestException("Connection timeout");
                }

                await Task.Delay(10);
                return "Data fetched successfully";
            },
            retries: 3,
            baseDelay: TimeSpan.FromMilliseconds(50));

        Console.WriteLine($"  Result: {result} (after {attemptCount} attempts)\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Final failure: {ex.Message}\n");
    }

    // Retry with fallback
    Console.WriteLine("  Retry with fallback:");

    var data = await new Func<Task<string>>(async () =>
    {
        await Task.Delay(10);
        throw new HttpRequestException("Service unavailable");
    }).PocketAsync("Cached fallback data");

    Console.WriteLine($"  Result: {data}\n");
}
