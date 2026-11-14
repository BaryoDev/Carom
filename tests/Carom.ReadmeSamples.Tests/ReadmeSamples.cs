// Every sample here is copied from README.md and compiled as a consumer would write it:
// the using directives sit at the top of the file, outside any namespace, which is where
// `Carom.ShotAsync` stops resolving because the namespace Carom hides the class Carom.
// ReadmeSamplesTests checks that each fragment below still matches the README text.
//
// readme:begin basic-usage
using Carom;
using Retry = Carom.Carom;
// readme:end basic-usage
// readme:begin attempt-timeout
using Carom.Extensions;
// readme:end attempt-timeout
using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class RateLimitedException : Exception
{
    public TimeSpan RetryAfter { get; set; }
}

public sealed class ApiClient
{
    public Task<int> CallAsync() => Task.FromResult(1);
    public Task<string> FetchAsync() => Task.FromResult("data");
    public Task<string> FetchAsync(CancellationToken ct) => Task.FromResult("data");
}

public static class ReadmeSamples
{
    private static readonly ApiClient api = new ApiClient();
    private static readonly ApiClient apiClient = new ApiClient();

    public static async Task BasicUsage()
    {
        // readme:begin basic-usage
        // Simple retry with exponential backoff
        var result = await Retry.ShotAsync(() => api.CallAsync(), retries: 3);

        // With timeout
        var bounce = Bounce.Times(5).WithTimeout(TimeSpan.FromSeconds(30));
        var data = await Retry.ShotAsync(() => apiClient.FetchAsync(), bounce);
        // readme:end basic-usage

        GC.KeepAlive(result);
        GC.KeepAlive(data);
    }

    public static async Task AttemptTimeout()
    {
        // readme:begin attempt-timeout
        // Each try gets 2 seconds, the whole call gets 10
        var bounce = Bounce.Times(3).WithTimeout(TimeSpan.FromSeconds(10));

        var data = await CaromAttemptTimeoutExtensions.ShotAsync(
            ct => apiClient.FetchAsync(ct), bounce, attemptTimeout: TimeSpan.FromSeconds(2));
        // readme:end attempt-timeout

        GC.KeepAlive(data);
    }

    public static async Task DelayHint()
    {
        // readme:begin delay-hint
        // Wait the Retry-After the server sent, up to MaxDelay
        var bounce = Bounce.Times(3)
            .WithMaxDelay(TimeSpan.FromSeconds(60))
            .WithDelayHint(ex => ex is RateLimitedException limited ? limited.RetryAfter : null);

        var data = await Retry.ShotAsync(() => apiClient.FetchAsync(), bounce);
        // readme:end delay-hint

        GC.KeepAlive(data);
    }
}
