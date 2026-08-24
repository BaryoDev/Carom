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
