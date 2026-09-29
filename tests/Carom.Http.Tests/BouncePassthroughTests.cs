// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Net;
using Xunit;
using Carom.Http;

namespace Carom.Http.Tests;

/// <summary>
/// The handler used to copy Retries, BaseDelay and DisableJitter out of its Bounce and drop the
/// rest. Measured before the fix: WithTimeout(200ms) against a 2s server returned 200 OK after 2s,
/// When(_ => false) still made 4 calls, and WithMaxDelay(10ms) on a 2s base delay waited 4s.
/// </summary>
public class BouncePassthroughTests
{
    private sealed class SlowHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> SawCancellation =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SawCancellation.TrySetResult(true);
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task Timeout_from_the_bounce_cancels_the_send()
    {
        var inner = new SlowHandler();
        var bounce = Bounce.Times(0).WithTimeout(TimeSpan.FromMilliseconds(200));
        // No HttpClient timeout, so only the Bounce timeout can end the send. The token
        // only stops a failing run from leaking the request.
        using var client = new HttpClient(new CaromHttpHandler(inner, bounce)) { Timeout = Timeout.InfiniteTimeSpan };
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var call = client.GetAsync("http://carom.test/", cleanup.Token);
        Assert.Same(call, await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(30))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        var winner = await Task.WhenAny(inner.SawCancellation.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(inner.SawCancellation.Task, winner);
    }

    [Fact]
    public async Task Predicate_from_the_bounce_decides_what_is_retried()
    {
        var inner = new UnavailableHandler();
        var bounce = Bounce.Times(3).WithDelay(TimeSpan.Zero).When(_ => false);
        using var client = new HttpClient(new CaromHttpHandler(inner, bounce));

        await Assert.ThrowsAsync<TransientHttpException>(() => client.GetAsync("http://carom.test/"));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task No_predicate_still_retries_transient_status_codes()
    {
        var inner = new UnavailableHandler();
        var bounce = Bounce.Times(2).WithDelay(TimeSpan.Zero);
        using var client = new HttpClient(new CaromHttpHandler(inner, bounce));

        await Assert.ThrowsAsync<TransientHttpException>(() => client.GetAsync("http://carom.test/"));

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Max_delay_from_the_bounce_caps_the_backoff()
    {
        var inner = new UnavailableHandler();
        var bounce = Bounce.Times(1)
            .WithDelay(TimeSpan.FromSeconds(30))
            .WithoutJitter()
            .WithMaxDelay(TimeSpan.FromMilliseconds(10));
        using var client = new HttpClient(new CaromHttpHandler(inner, bounce));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TransientHttpException>(() => client.GetAsync("http://carom.test/"));

        Assert.Equal(2, inner.Calls);
        // Uncapped, the backoff is the 30 second default ceiling.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
    }
}
