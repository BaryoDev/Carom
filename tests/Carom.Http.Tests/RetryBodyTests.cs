using System.Net;
using System.Text;
using Xunit;
using Carom.Http;

namespace Carom.Http.Tests;

/// <summary>
/// A retried request has to send the same body every time.
/// </summary>
/// <remarks>
/// The handler re-sends one <see cref="HttpRequestMessage"/> for every attempt. A body backed by a
/// forward-only stream is consumed by the first attempt, and the failure that follows is silent: no
/// exception, the retry simply sends nothing and the server answers an empty request.
///
/// Measured before the fix, with a forward-only stream: two calls, bodies <c>["payload", ""]</c>.
/// </remarks>
public class RetryBodyTests
{
    /// <summary>
    /// Records what each attempt actually received.
    /// </summary>
    /// <remarks>
    /// Writes the body the way the transport does. Two other approaches were tried first and each
    /// measured the test rather than the code: <c>ReadAsStringAsync</c> buffers, so a forward-only
    /// body became rewindable and the unfixed handler looked correct; <c>ReadAsStreamAsync</c>
    /// returns one cached stream, so reading it here consumed the buffer the fix installs and the
    /// fixed handler looked broken.
    /// </remarks>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly int _failFirst;
        private int _calls;

        public RecordingHandler(int failFirst) => _failFirst = failFirst;

        public int Calls => _calls;
        public List<string?> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;

            if (request.Content is null)
            {
                Bodies.Add(null);
            }
            else
            {
                // CopyToAsync, because that is what the transport does when it writes the body to the
                // wire. ReadAsStringAsync buffers, which would make a forward-only body rewindable and
                // hide the defect; ReadAsStreamAsync hands back one cached stream, so reading it here
                // drains the very buffer the fix creates. Both were tried and both measured the
                // harness rather than the handler.
                using var sink = new MemoryStream();
                await request.Content.CopyToAsync(sink, cancellationToken).ConfigureAwait(false);
                Bodies.Add(Encoding.UTF8.GetString(sink.ToArray()));
            }

            return new HttpResponseMessage(
                _calls <= _failFirst ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }

    /// <summary>A stream that cannot be rewound, which is what makes the body unrepeatable.</summary>
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

    private static StreamContent ForwardOnly(string body) =>
        new(new ForwardOnlyStream(Encoding.UTF8.GetBytes(body)));

    [Fact]
    public async Task A_retried_put_sends_the_same_forward_only_body_every_time()
    {
        var inner = new RecordingHandler(failFirst: 1);
        using var client = new HttpClient(new CaromHttpHandler { InnerHandler = inner });

        var response = await client.PutAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(new[] { "payload", "payload" }, inner.Bodies);
    }

    [Fact]
    public async Task The_body_survives_more_than_one_retry()
    {
        var inner = new RecordingHandler(failFirst: 2);
        using var client = new HttpClient(new CaromHttpHandler { InnerHandler = inner });

        await client.PutAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(3, inner.Calls);
        Assert.All(inner.Bodies, b => Assert.Equal("payload", b));
    }

    /// <summary>
    /// The positive control. Without it, every assertion above is satisfied by a handler that never
    /// retries at all, since one attempt trivially sends the right body once.
    /// </summary>
    [Fact]
    public async Task A_request_that_succeeds_first_time_is_sent_once()
    {
        var inner = new RecordingHandler(failFirst: 0);
        using var client = new HttpClient(new CaromHttpHandler { InnerHandler = inner });

        await client.PutAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(1, inner.Calls);
        Assert.Equal(new[] { "payload" }, inner.Bodies);
    }

    /// <summary>
    /// POST is not retried unless asked for, so its body is never at risk by default.
    /// </summary>
    [Fact]
    public async Task A_post_is_not_retried_by_default()
    {
        var inner = new RecordingHandler(failFirst: 1);
        using var client = new HttpClient(new CaromHttpHandler { InnerHandler = inner });

        var response = await client.PostAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    /// <summary>
    /// A body over the buffer bound is sent once rather than refused.
    /// </summary>
    /// <remarks>
    /// Making a forward-only body replayable means holding it in memory, so an unbounded upload
    /// would be buffered whole on the way to a request that may succeed first time. Over the bound
    /// the retry is skipped, which is what would have happened before any buffering existed.
    /// Refusing the call instead would turn a memory concern into an outage.
    /// </remarks>
    [Fact]
    public async Task A_body_over_the_buffer_bound_is_sent_once_and_not_retried()
    {
        var inner = new RecordingHandler(failFirst: 1);
        var handler = new CaromHttpHandler { InnerHandler = inner, MaxRetryBufferBytes = 8 };
        using var client = new HttpClient(handler);

        // StringContent declares Content-Length, so the handler can tell it is oversize before
        // touching the body and send it once intact. A forward-only body with no declared length
        // cannot be checked in advance; that case is covered below.
        const string body = "a body comfortably over eight bytes";
        var response = await client.PutAsync("http://localhost/thing", new StringContent(body));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, inner.Calls);

        // The body matters as much as the count. Skipping the retry is only correct if the one
        // request that does go out carries the whole body; asserting the count alone would pass on
        // an empty or truncated send, which is the defect this file exists to catch.
        Assert.Equal(new[] { body }, inner.Bodies);
    }

    /// <summary>
    /// An oversize body of unknown length reports why rather than sending a truncated one.
    /// </summary>
    /// <remarks>
    /// LoadIntoBufferAsync partially consumes the stream before throwing on overflow, so there is no
    /// unbuffered send to fall back to: the body is already damaged. Discovered by testing the
    /// fallback, which failed with "The stream was already consumed."
    /// </remarks>
    [Fact]
    public async Task An_oversize_body_of_unknown_length_reports_why()
    {
        var inner = new RecordingHandler(failFirst: 1);
        var handler = new CaromHttpHandler { InnerHandler = inner, MaxRetryBufferBytes = 8 };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.PutAsync("http://localhost/thing", ForwardOnly("a body comfortably over eight bytes")));

        Assert.Contains("MaxRetryBufferBytes", ex.Message);
    }

    /// <summary>
    /// The control for the bound: a body under it still retries.
    /// </summary>
    /// <remarks>
    /// Without this, the test above is satisfied by a handler that never retries anything.
    /// </remarks>
    [Fact]
    public async Task A_body_under_the_buffer_bound_still_retries()
    {
        var inner = new RecordingHandler(failFirst: 1);
        var handler = new CaromHttpHandler { InnerHandler = inner, MaxRetryBufferBytes = 1024 };
        using var client = new HttpClient(handler);

        await client.PutAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(2, inner.Calls);
        Assert.Equal(new[] { "payload", "payload" }, inner.Bodies);
    }

    [Fact]
    public async Task A_post_that_opts_in_keeps_its_body_across_retries()
    {
        var inner = new RecordingHandler(failFirst: 1);
        var handler = new CaromHttpHandler { InnerHandler = inner, RetryNonIdempotentRequests = true };
        using var client = new HttpClient(handler);

        await client.PostAsync("http://localhost/thing", ForwardOnly("payload"));

        Assert.Equal(2, inner.Calls);
        Assert.Equal(new[] { "payload", "payload" }, inner.Bodies);
    }
}
