using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Carom.Extensions;
using Xunit;

namespace Carom.Http.Tests;

/// <summary>
/// The handler hands back the last response, honours Retry-After, tells safe resends from unsafe
/// ones and can carry a breaker per host (BaryoDev/Carom#47).
/// </summary>
public class CaromHttpHandlerFeatureTests
{
    private static readonly Uri Url = new("http://carom.test/thing");

    /// <summary>A response that records whether it was disposed.</summary>
    private sealed class TrackedResponse : HttpResponseMessage
    {
        public bool Disposed { get; private set; }

        public TrackedResponse(HttpStatusCode status, string body) : base(status)
        {
            Content = new StringContent(body);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Answers from a script: a status, or an exception, per call.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, Task<HttpResponseMessage>> _script;
        private int _calls;

        public ScriptedHandler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> script) => _script = script;

        public int Calls => Volatile.Read(ref _calls);
        public List<TrackedResponse> Responses { get; } = new();
        public List<long> Ticks { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            lock (Ticks) Ticks.Add(Stopwatch.GetTimestamp());
            return _script(n, request);
        }

        public static ScriptedHandler Statuses(Func<int, HttpStatusCode> status) =>
            new((n, _) => Task.FromResult<HttpResponseMessage>(new TrackedResponse(status(n), $"body {n}")));
    }

    private static Bounce Quick(int retries) => Bounce.Times(retries).WithDelay(TimeSpan.Zero);

    private static HttpRequestMessage Get() => new(HttpMethod.Get, Url);

    private static HttpRequestMessage Post(string body = "payload") =>
        new(HttpMethod.Post, Url) { Content = new StringContent(body) };

    private static async Task<HttpResponseMessage> Send(CaromHttpHandler handler, HttpRequestMessage request)
    {
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        return await invoker.SendAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task Two_503s_then_a_200_is_one_success_after_three_calls()
    {
        var inner = ScriptedHandler.Statuses(n => n <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var handler = new CaromHttpHandler(inner, Quick(3));

        var response = await Send(handler, Get());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task Tries_spent_with_the_option_on_hands_back_the_last_503_with_its_body()
    {
        var inner = ScriptedHandler.Statuses(_ => HttpStatusCode.ServiceUnavailable);
        var handler = new CaromHttpHandler(inner, Quick(2)) { ReturnLastResponse = true };

        var response = await Send(handler, Get());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, inner.Calls);
        Assert.Equal("body 3", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tries_spent_with_the_option_off_still_throws()
    {
        var inner = ScriptedHandler.Statuses(_ => HttpStatusCode.ServiceUnavailable);
        var handler = new CaromHttpHandler(inner, Quick(2));

        var ex = await Assert.ThrowsAsync<TransientHttpException>(() => Send(handler, Get()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task A_retried_response_is_disposed_and_only_the_returned_one_is_not()
    {
        var all = new List<TrackedResponse>();
        var spy = new ScriptedHandler((n, _) =>
        {
            var r = new TrackedResponse(HttpStatusCode.ServiceUnavailable, $"body {n}");
            lock (all) all.Add(r);
            return Task.FromResult<HttpResponseMessage>(r);
        });
        var handler = new CaromHttpHandler(spy, Quick(2)) { ReturnLastResponse = true };

        var response = await Send(handler, Get());

        Assert.Equal(3, all.Count);
        Assert.True(all[0].Disposed);
        Assert.True(all[1].Disposed);
        Assert.False(all[2].Disposed);
        Assert.Same(all[2], response);
    }

    [Fact]
    public async Task With_the_option_off_every_transient_response_is_disposed()
    {
        var all = new List<TrackedResponse>();
        var spy = new ScriptedHandler((n, _) =>
        {
            var r = new TrackedResponse(HttpStatusCode.ServiceUnavailable, "x");
            lock (all) all.Add(r);
            return Task.FromResult<HttpResponseMessage>(r);
        });
        var handler = new CaromHttpHandler(spy, Quick(2));

        await Assert.ThrowsAsync<TransientHttpException>(() => Send(handler, Get()));

        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.True(r.Disposed));
    }

    [Fact]
    public async Task A_post_that_timed_out_is_not_resent_unless_marked()
    {
        var calls = 0;
        var slow = new SlowInner(() => Interlocked.Increment(ref calls));
        var handler = new CaromHttpHandler(slow, Quick(3).WithTimeout(TimeSpan.FromMilliseconds(150)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(handler, Post()));

        Assert.Equal(1, calls);
    }

    private sealed class SlowInner : HttpMessageHandler
    {
        private readonly Action _onCall;
        public SlowInner(Action onCall) => _onCall = onCall;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _onCall();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_post_with_a_503_is_not_resent_by_default()
    {
        var inner = ScriptedHandler.Statuses(n => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var handler = new CaromHttpHandler(inner, Quick(3));

        var response = await Send(handler, Post());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_post_marked_with_the_request_option_is_resent()
    {
        var inner = ScriptedHandler.Statuses(n => n <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var handler = new CaromHttpHandler(inner, Quick(3));
        var request = Post();
        request.Options.Set(CaromHttpHandler.AllowResend, true);

        var response = await Send(handler, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task A_post_carrying_an_idempotency_key_is_resent()
    {
        var inner = ScriptedHandler.Statuses(n => n <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var handler = new CaromHttpHandler(inner, Quick(3));
        var request = Post();
        request.Headers.Add("Idempotency-Key", "abc-123");

        var response = await Send(handler, request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task The_handler_wide_flag_still_resends_a_post()
    {
        var inner = ScriptedHandler.Statuses(n => n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        var handler = new CaromHttpHandler(inner, Quick(3)) { RetryNonIdempotentRequests = true };

        var response = await Send(handler, Post());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.NameResolutionError)]
    public async Task A_connect_failure_on_a_post_is_resent(HttpRequestError error)
    {
        var inner = new ScriptedHandler((n, _) => n == 1
            ? throw new HttpRequestException(error, "refused")
            : Task.FromResult<HttpResponseMessage>(new HttpResponseMessage(HttpStatusCode.OK)));
        var handler = new CaromHttpHandler(inner, Quick(3));

        var response = await Send(handler, Post());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Other_request_errors_on_a_post_are_not_resent()
    {
        var inner = new ScriptedHandler((n, _) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "dropped mid-exchange"));
        var handler = new CaromHttpHandler(inner, Quick(3));

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(handler, Post()));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_tls_failure_is_not_retried()
    {
        var inner = new ScriptedHandler((n, _) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "bad certificate"));
        var handler = new CaromHttpHandler(inner, Quick(3));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Send(handler, Get()));

        Assert.Equal(HttpRequestError.SecureConnectionError, ex.HttpRequestError);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_marked_post_does_not_retry_a_tls_failure_either()
    {
        var inner = new ScriptedHandler((n, _) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "bad certificate"));
        var handler = new CaromHttpHandler(inner, Quick(3)) { RetryNonIdempotentRequests = true };

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(handler, Post()));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Any_other_request_error_on_a_get_keeps_the_old_rule_and_is_retried()
    {
        var inner = new ScriptedHandler((n, _) => n == 1
            ? throw new HttpRequestException(HttpRequestError.Unknown, "reset")
            : Task.FromResult<HttpResponseMessage>(new HttpResponseMessage(HttpStatusCode.OK)));
        var handler = new CaromHttpHandler(inner, Quick(3));

        var response = await Send(handler, Get());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;
        public ForwardOnlyStream(byte[] data) => _inner = new MemoryStream(data);
        public override bool CanSeek => false;
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => _inner.Read(b, o, c);
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Unbuffered_content_is_not_resent_after_a_connect_failure()
    {
        var inner = new ScriptedHandler((n, _) => n == 1
            ? throw new HttpRequestException(HttpRequestError.ConnectionError, "refused")
            : Task.FromResult<HttpResponseMessage>(new HttpResponseMessage(HttpStatusCode.OK)));
        var handler = new CaromHttpHandler(inner, Quick(3));
        var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StreamContent(new ForwardOnlyStream(Encoding.UTF8.GetBytes("payload")))
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Send(handler, request));

        Assert.Equal(HttpRequestError.ConnectionError, ex.HttpRequestError);
        Assert.Equal(1, inner.Calls);
    }

    private static Cushion Breaker(string host) =>
        Cushion.ForService($"carom-http-{host}-{Guid.NewGuid():N}")
            .OpenAfter(2, 2)
            .HalfOpenAfter(TimeSpan.FromHours(1));

    private static ScriptedHandler HostScript() => new((n, req) =>
        Task.FromResult<HttpResponseMessage>(new TrackedResponse(
            req.RequestUri!.Host == "down.test" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, "x")));

    [Fact]
    public async Task The_breaker_opens_for_one_host_and_not_another()
    {
        var inner = HostScript();
        var handler = new CaromHttpHandler(inner, Quick(0)) { CushionForHost = Breaker };
        var down = new Uri("http://down.test/x");
        var up = new Uri("http://up.test/x");

        await Assert.ThrowsAsync<TransientHttpException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, down)));
        await Assert.ThrowsAsync<TransientHttpException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, down)));
        Assert.Equal(2, inner.Calls);

        await Assert.ThrowsAsync<CircuitOpenException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, down)));
        Assert.Equal(2, inner.Calls);

        var response = await Send(handler, new HttpRequestMessage(HttpMethod.Get, up));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task A_returned_503_still_counts_as_a_failure_for_the_breaker()
    {
        var inner = HostScript();
        var handler = new CaromHttpHandler(inner, Quick(0)) { CushionForHost = Breaker, ReturnLastResponse = true };
        var down = new Uri("http://down.test/x");

        var first = await Send(handler, new HttpRequestMessage(HttpMethod.Get, down));
        var second = await Send(handler, new HttpRequestMessage(HttpMethod.Get, down));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        await Assert.ThrowsAsync<CircuitOpenException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, down)));
    }

    [Fact]
    public async Task A_503_on_a_request_sent_once_still_counts_as_a_failure_for_the_breaker()
    {
        var inner = HostScript();
        var handler = new CaromHttpHandler(inner, Quick(3)) { CushionForHost = Breaker };
        var down = new Uri("http://down.test/x");

        Func<HttpRequestMessage> post = () => new HttpRequestMessage(HttpMethod.Post, down) { Content = new StringContent("x") };
        var first = await Send(handler, post());
        var second = await Send(handler, post());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        Assert.Equal(2, inner.Calls);
        await Assert.ThrowsAsync<CircuitOpenException>(() => Send(handler, post()));
    }

    [Fact]
    public async Task The_factory_runs_once_per_host()
    {
        var inner = HostScript();
        var made = new List<string>();
        var handler = new CaromHttpHandler(inner, Quick(0))
        {
            CushionForHost = host =>
            {
                made.Add(host);
                return Breaker(host);
            }
        };
        var down = new Uri("http://down.test/x");

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAnyAsync<HttpRequestException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, down)));
        }

        Assert.Equal(new[] { "down.test" }, made);
    }

    [Fact]
    public async Task A_host_past_the_bound_gets_no_breaker_and_is_not_remembered()
    {
        var inner = HostScript();
        var made = new List<string>();
        var handler = new CaromHttpHandler(inner, Quick(0))
        {
            MaxCushionHosts = 1,
            CushionForHost = host =>
            {
                made.Add(host);
                return Breaker(host);
            }
        };
        var other = new Uri("http://other-down.test/x");

        await Send(handler, new HttpRequestMessage(HttpMethod.Get, new Uri("http://up.test/x")));
        for (var i = 0; i < 4; i++)
        {
            // No breaker for this host, so every call reaches the server and none is rejected.
            await Assert.ThrowsAsync<TransientHttpException>(() => Send(handler, new HttpRequestMessage(HttpMethod.Get, other)));
        }

        Assert.Equal(5, inner.Calls);
        Assert.Equal(new[] { "up.test" }, made);
    }

    [Fact]
    public void Max_cushion_hosts_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaromHttpHandler { MaxCushionHosts = 0 });
    }

    private static HttpResponseMessage WithRetryAfter(RetryConditionHeaderValue value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = value;
        return response;
    }

    [Fact]
    public void Retry_after_in_seconds_reads_as_that_wait()
    {
        var handler = new CaromHttpHandler();

        var wait = handler.ReadRetryAfter(WithRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromSeconds(2))));

        Assert.Equal(TimeSpan.FromSeconds(2), wait);
    }

    [Fact]
    public void Retry_after_as_an_http_date_is_read_against_the_handler_clock()
    {
        var now = new DateTimeOffset(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        var handler = new CaromHttpHandler { Clock = () => now };

        var future = handler.ReadRetryAfter(WithRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(2))));
        var past = handler.ReadRetryAfter(WithRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(-2))));

        Assert.Equal(TimeSpan.FromSeconds(2), future);
        Assert.Null(past);
    }

    [Fact]
    public void A_missing_or_unparseable_retry_after_is_no_hint()
    {
        var handler = new CaromHttpHandler();
        var garbage = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        garbage.Headers.TryAddWithoutValidation("Retry-After", "soon");

        Assert.Null(handler.ReadRetryAfter(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        Assert.Null(handler.ReadRetryAfter(garbage));
    }

    private static ScriptedHandler RetryAfterThenOk(string value) => new((n, _) =>
    {
        HttpResponseMessage response = n == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.OK);
        if (n == 1) response.Headers.TryAddWithoutValidation("Retry-After", value);
        return Task.FromResult(response);
    });

    private static TimeSpan Gap(ScriptedHandler inner)
    {
        lock (inner.Ticks)
        {
            return TimeSpan.FromSeconds((inner.Ticks[1] - inner.Ticks[0]) / (double)Stopwatch.Frequency);
        }
    }

    [Fact]
    public async Task A_retry_after_of_two_seconds_is_waited()
    {
        var inner = RetryAfterThenOk("2");
        var handler = new CaromHttpHandler(inner, Quick(1));

        var response = await Send(handler, Get());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The computed backoff is zero here, so any wait comes from the header.
        Assert.True(Gap(inner) >= TimeSpan.FromMilliseconds(1900), $"waited {Gap(inner)}");
    }

    [Fact]
    public async Task A_retry_after_past_max_delay_is_clamped()
    {
        var inner = RetryAfterThenOk("3600");
        var handler = new CaromHttpHandler(inner, Quick(1).WithMaxDelay(TimeSpan.FromMilliseconds(200)));

        var response = await Send(handler, Get());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var gap = Gap(inner);
        Assert.True(gap >= TimeSpan.FromMilliseconds(150), $"waited {gap}");
        Assert.True(gap < TimeSpan.FromSeconds(10), $"waited {gap}");
    }
}
