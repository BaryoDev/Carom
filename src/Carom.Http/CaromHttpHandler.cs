// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Carom.Extensions;

namespace Carom.Http
{
    /// <summary>
    /// A DelegatingHandler that automatically retries HTTP requests on transient failures.
    /// Uses Carom's decorrelated jitter for retry delays.
    /// </summary>
    public class CaromHttpHandler : DelegatingHandler
    {
        private readonly Bounce _config;

        /// <summary>
        /// The HTTP status code for "Too Many Requests" (429).
        /// </summary>
        private const int TooManyRequestsStatusCode = 429;

        /// <summary>
        /// Creates a new CaromHttpHandler with default settings (3 retries, 100ms base delay).
        /// </summary>
        public CaromHttpHandler() : this(Bounce.Times(3))
        {
        }

        /// <summary>
        /// Creates a new CaromHttpHandler with the specified bounce configuration.
        /// </summary>
        /// <param name="config">The retry configuration.</param>
        public CaromHttpHandler(Bounce config)
        {
            _config = config;
        }

        /// <summary>
        /// Creates a new CaromHttpHandler with the specified number of retries.
        /// </summary>
        /// <param name="retries">The number of retry attempts.</param>
        public CaromHttpHandler(int retries) : this(Bounce.Times(retries))
        {
        }

        /// <summary>
        /// Creates a new CaromHttpHandler with an inner handler and default settings.
        /// </summary>
        /// <param name="innerHandler">The inner handler.</param>
        public CaromHttpHandler(HttpMessageHandler innerHandler) : this()
        {
            InnerHandler = innerHandler;
        }

        /// <summary>
        /// Creates a new CaromHttpHandler with an inner handler and bounce configuration.
        /// </summary>
        /// <param name="innerHandler">The inner handler.</param>
        /// <param name="config">The retry configuration.</param>
        public CaromHttpHandler(HttpMessageHandler innerHandler, Bounce config) : this(config)
        {
            InnerHandler = innerHandler;
        }

        /// <summary>
        /// Whether to retry non-idempotent requests (POST, PATCH).
        /// Defaults to false: if the server processed a POST but the response was lost
        /// (a common cause of 502/504 under failover), a retry would duplicate the
        /// side effect. Retrying also re-serializes the same HttpContent instance,
        /// which silently sends an empty body for non-rewindable stream content.
        /// Enable only when requests are idempotent (e.g., carry an idempotency key). An explicit
        /// <see cref="AllowResend"/> value on a request overrides this flag.
        /// </summary>
        public bool RetryNonIdempotentRequests { get; set; }

        /// <summary>
        /// The largest request body, in bytes, that will be buffered so a retry can re-send it.
        /// Defaults to 1 MiB. A body larger than this is sent once, without retries.
        /// </summary>
        /// <remarks>
        /// A retried request re-sends the same message, so its body has to be replayable, and making
        /// a forward-only body replayable means holding it in memory. Without a bound that is the
        /// whole upload, on the way to a request that may well succeed on the first attempt.
        ///
        /// Over the bound the request is sent once rather than refused. Retry is an optimisation and
        /// a large body is a reason to skip it, not a reason to fail the call.
        /// </remarks>
        public long MaxRetryBufferBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// The name of the request option that marks one request as safe to resend. Set it to true
        /// to let a POST or PATCH be retried when this handler's <see cref="RetryNonIdempotentRequests"/>
        /// is off. On net8.0 and later use the typed key <see cref="AllowResend"/>; on netstandard2.0
        /// set <c>request.Properties[CaromHttpHandler.AllowResendOptionName] = true</c>.
        /// </summary>
        public const string AllowResendOptionName = "Carom.Http.AllowResend";

#if NET5_0_OR_GREATER
        /// <summary>
        /// A request option the caller sets to true to say this request is safe to resend, for
        /// example <c>request.Options.Set(CaromHttpHandler.AllowResend, true)</c>. A request that
        /// carries a non-empty <c>Idempotency-Key</c> header is treated the same way. An explicit
        /// value on the request wins: false stops a resend even when the header is present or
        /// <see cref="RetryNonIdempotentRequests"/> is on, because the most specific setting wins.
        /// </summary>
        public static readonly HttpRequestOptionsKey<bool> AllowResend =
            new HttpRequestOptionsKey<bool>(AllowResendOptionName);
#endif

        /// <summary>
        /// When the tries run out on a transient status (502, 503, 504, 408, 429), hand back the
        /// last response, body readable, instead of throwing <see cref="TransientHttpException"/>.
        /// Defaults to false, which keeps the old behaviour: the response is disposed and the
        /// exception is thrown. Every response that is retried is disposed before the next try, and
        /// only the returned one is left for the caller to dispose. The overall timeout cuts a
        /// Retry-After wait: the held response is then disposed and
        /// <see cref="TimeoutRejectedException"/> is thrown.
        /// </summary>
        public bool ReturnLastResponse { get; set; }

        /// <summary>
        /// Gives a circuit breaker per request host. The function receives <c>RequestUri.Host</c> and
        /// returns the <see cref="Cushion"/> for it, so a down provider stops being called while the
        /// others carry on. It is called once per host and the result is kept. Give each host its own
        /// service key (for example <c>"http:" + host</c>), because a Cushion's state is stored by key.
        /// The breaker wraps the whole retry chain, so one call records one outcome, and a response
        /// handed back by <see cref="ReturnLastResponse"/> still counts as a failure. Null (the
        /// default) means no breaker.
        /// </summary>
        public Func<string, Cushion>? CushionForHost { get; set; }

        /// <summary>
        /// The most hosts <see cref="CushionForHost"/> is asked about. Defaults to 100. A host that
        /// arrives after the bound is reached is sent without a breaker and is not remembered, so the
        /// table never grows past the bound. Must be at least 1.
        /// </summary>
        public int MaxCushionHosts
        {
            get => _maxCushionHosts;
            set
            {
                if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "MaxCushionHosts must be at least 1");
                _maxCushionHosts = value;
            }
        }

        private int _maxCushionHosts = 100;
        private readonly ConcurrentDictionary<string, Lazy<Cushion>> _cushions =
            new ConcurrentDictionary<string, Lazy<Cushion>>(StringComparer.OrdinalIgnoreCase);
        private int _cushionSlots;

        // Test seam for reading an HTTP-date Retry-After against a fixed time.
        internal Func<DateTimeOffset>? Clock { get; set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var cushion = CushionFor(request);

            if (!CanResend(request))
            {
#if NET8_0_OR_GREATER
                // A connect or name-resolution failure means nothing reached the server, so even a
                // POST can go again. The body is not buffered here, so only content that can be
                // serialized more than once is resent; anything else fails as if the tries ran out.
                if (IsReplayable(request.Content))
                {
                    var user = _config.ShouldBounce;
                    var connectOnly = _config
                        .When(ex => IsConnectFailure(ex) && (user == null || user(ex)))
                        .WithDelayHint(RetryAfterOf);
                    return await RunAsync(request, connectOnly, returnResponse: true, cushion, cancellationToken)
                        .ConfigureAwait(false);
                }
#endif
                // Sent once, no retries, but the Bounce timeout still applies.
                return await SendSingleAsync(request, cushion, cancellationToken).ConfigureAwait(false);
            }

            // Buffered before the first attempt, because every attempt re-sends this same request and
            // a forward-only body is already consumed by the time the second one runs. The failure
            // without this is silent rather than loud: no exception, the retry simply sends an empty
            // body and the server answers it. Measured before fixing, with a forward-only stream:
            // two calls, bodies ["payload", ""].
            //
            // Bounded, and a body over the bound is sent once rather than refused. LoadIntoBufferAsync
            // defaults to int.MaxValue, so an unbounded upload would be held whole in memory on the
            // way to a request that may well succeed first time. Retry is an optimisation; a large
            // body is a reason to skip it, not a reason to fail the call.
            if (request.Content != null)
            {
                var declaredLength = request.Content.Headers.ContentLength;

                if (declaredLength > MaxRetryBufferBytes)
                {
                    // Known to be too big, and nothing has touched the body yet, so it can still be
                    // sent exactly once. Retry is an optimisation; a large upload is a reason to skip
                    // it rather than to fail the call.
                    return await SendSingleAsync(request, cushion, cancellationToken).ConfigureAwait(false);
                }

                // Unknown length, or known and within the bound. LoadIntoBufferAsync partially
                // consumes the stream before it throws on overflow, so there is no falling back to an
                // unbuffered send from here: the body is already damaged. Saying so is better than
                // sending a truncated one.
                try
                {
                    await request.Content.LoadIntoBufferAsync(MaxRetryBufferBytes).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new HttpRequestException(
                        $"The request body exceeded MaxRetryBufferBytes ({MaxRetryBufferBytes}) and its "
                        + "length was not declared, so it could not be buffered for retry and cannot now "
                        + "be sent unbuffered. Raise MaxRetryBufferBytes, set Content-Length, or use a "
                        + "handler without retry for this request.",
                        ex);
                }
            }

            // Pass the whole Bounce so every field, present and future, reaches core. The
            // attempt token carries the Bounce timeout, so a timeout cancels the send itself.
            // The hint replaces any WithDelayHint on the configured Bounce.
            var effective = (_config.ShouldBounce == null ? _config.When(IsTransientException) : _config)
                .WithDelayHint(RetryAfterOf);
            return await RunAsync(request, effective, ReturnLastResponse, cushion, cancellationToken)
                .ConfigureAwait(false);
        }

        // One attempt, no retries, but the Bounce timeout still applies.
        private Task<HttpResponseMessage> SendSingleAsync(
            HttpRequestMessage request, Cushion? cushion, CancellationToken cancellationToken)
        {
            var once = Bounce.Times(0);
            if (_config.Timeout is TimeSpan timeout) once = once.WithTimeout(timeout);

            if (cushion is Cushion)
            {
                // Through the breaker, so a 503 counts as a failure. The response is still returned.
                return RunAsync(request, once, returnResponse: true, cushion, cancellationToken);
            }

            return Carom.ShotAsync(token => base.SendAsync(request, token), once, cancellationToken);
        }

        // Runs the tries. A transient status becomes a TransientHttpException so the retry loop and
        // the breaker see a failure. With returnResponse the response is kept (not disposed) on that
        // exception and handed back once the tries are over; a response that is followed by another
        // try is disposed when that try starts.
        private async Task<HttpResponseMessage> RunAsync(
            HttpRequestMessage request,
            Bounce bounce,
            bool returnResponse,
            Cushion? cushion,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage? held = null;
            var handedBack = false;
            try
            {
                Func<CancellationToken, Task<HttpResponseMessage>> attempt = async token =>
                {
                    var previous = held;
                    held = null;
                    previous?.Dispose();

                    var response = await base.SendAsync(request, token).ConfigureAwait(false);
                    if (!IsTransientError(response.StatusCode))
                    {
                        return response;
                    }

                    var failure = new TransientHttpException(
                        $"Transient HTTP error: {response.StatusCode}", response.StatusCode)
                    {
                        RetryAfter = ReadRetryAfter(response)
                    };

                    if (returnResponse)
                    {
                        failure.Response = response;
                        held = response;
                    }
                    else
                    {
                        response.Dispose();
                    }

                    throw failure;
                };

                if (cushion is Cushion breaker)
                {
                    return await breaker.ExecuteAsync(
                        () => Carom.ShotAsync(attempt, bounce, cancellationToken)).ConfigureAwait(false);
                }

                return await Carom.ShotAsync(attempt, bounce, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientHttpException ex) when (held != null && ReferenceEquals(ex.Response, held))
            {
                handedBack = true;
                var last = held;
                held = null;
                return last;
            }
            finally
            {
                if (!handedBack)
                {
                    held?.Dispose();
                }
            }
        }

        private Cushion? CushionFor(HttpRequestMessage request)
        {
            var factory = CushionForHost;
            var uri = request.RequestUri;
            if (factory == null || uri == null || !uri.IsAbsoluteUri)
            {
                return null;
            }

            var host = uri.Host;
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }

            if (_cushions.TryGetValue(host, out var known))
            {
                return ValueOf(host, known);
            }

            // Reserve a slot before the factory runs, so the bound holds under parallel first
            // requests. No lock: the count only goes up by Interlocked, and a loser gives it back.
            if (Interlocked.Increment(ref _cushionSlots) > _maxCushionHosts)
            {
                // Past the bound: this host runs without a breaker, and nothing is stored.
                Interlocked.Decrement(ref _cushionSlots);
                return null;
            }

            var fresh = new Lazy<Cushion>(() => factory(host), LazyThreadSafetyMode.ExecutionAndPublication);
            var entry = _cushions.GetOrAdd(host, fresh);
            if (!ReferenceEquals(entry, fresh))
            {
                // Another thread's entry won, so its slot is the one in use.
                Interlocked.Decrement(ref _cushionSlots);
            }

            return ValueOf(host, entry);
        }

        // Runs the factory (once, through the Lazy). If it throws, nothing stays stored and the
        // slot is released, so the next request calls the factory again.
        private Cushion ValueOf(string host, Lazy<Cushion> entry)
        {
            try
            {
                return entry.Value;
            }
            catch
            {
                var pair = new KeyValuePair<string, Lazy<Cushion>>(host, entry);
                if (((ICollection<KeyValuePair<string, Lazy<Cushion>>>)_cushions).Remove(pair))
                {
                    Interlocked.Decrement(ref _cushionSlots);
                }

                throw;
            }
        }

        private bool CanResend(HttpRequestMessage request)
        {
            if (IsIdempotent(request.Method))
            {
                return true;
            }

            // The most specific setting wins: an explicit AllowResend on the request, either way,
            // beats the Idempotency-Key header and the handler-wide flag.
            if (TryReadResendMark(request, out var marked))
            {
                return marked;
            }

            return RetryNonIdempotentRequests || HasIdempotencyKey(request);
        }

        private static bool HasIdempotencyKey(HttpRequestMessage request)
        {
            if (!request.Headers.TryGetValues("Idempotency-Key", out var values))
            {
                return false;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadResendMark(HttpRequestMessage request, out bool marked)
        {
#if NET5_0_OR_GREATER
            return request.Options.TryGetValue(AllowResend, out marked);
#else
            if (request.Properties.TryGetValue(AllowResendOptionName, out var value) && value is bool allowed)
            {
                marked = allowed;
                return true;
            }

            marked = false;
            return false;
#endif
        }

#if NET8_0_OR_GREATER
        // Content that serializes from memory every time it is asked, so it can be sent again
        // without buffering it first. JsonContent serializes its value on every send. A multipart
        // body is replayable when every part is, at any depth. StreamContent is not.
        private static bool IsReplayable(HttpContent? content)
        {
            if (content == null || content is ByteArrayContent || content is System.Net.Http.Json.JsonContent)
            {
                return true;
            }

            if (content is MultipartContent multipart)
            {
                foreach (var part in multipart)
                {
                    if (!IsReplayable(part))
                    {
                        return false;
                    }
                }

                return true;
            }

            return false;
        }
#endif

        private static TimeSpan? RetryAfterOf(Exception ex) => (ex as TransientHttpException)?.RetryAfter;

        // Retry-After as a wait from now: delta seconds, or an HTTP-date read against the handler's
        // clock. A negative or missing value is no hint.
        internal TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header == null)
            {
                return null;
            }

            TimeSpan wait;
            if (header.Delta is TimeSpan delta)
            {
                wait = delta;
            }
            else if (header.Date is DateTimeOffset date)
            {
                wait = date - (Clock ?? (() => DateTimeOffset.UtcNow))();
            }
            else
            {
                return null;
            }

            return wait < TimeSpan.Zero ? (TimeSpan?)null : wait;
        }

        /// <summary>
        /// Determines if an HTTP method is idempotent per RFC 9110 and therefore safe to retry.
        /// </summary>
        private static bool IsIdempotent(HttpMethod method)
        {
            return method == HttpMethod.Get
                || method == HttpMethod.Head
                || method == HttpMethod.Options
                || method == HttpMethod.Put
                || method == HttpMethod.Delete
                || method == HttpMethod.Trace;
        }

        /// <summary>
        /// Determines if an exception represents a transient failure that should be retried.
        /// </summary>
        /// <param name="ex">The exception to check.</param>
        /// <returns>True if the exception is transient and should be retried.</returns>
        private static bool IsTransientException(Exception ex)
        {
            if (ex is TransientHttpException)
            {
                return true;
            }

            if (ex is HttpRequestException request)
            {
#if NET8_0_OR_GREATER
                // A TLS failure will fail the same way next time.
                return request.HttpRequestError != HttpRequestError.SecureConnectionError;
#else
                return true;
#endif
            }

            return false;
        }

#if NET8_0_OR_GREATER
        // The request never reached the server, so it is safe to send again whatever the method.
        // A TransientHttpException always carries HttpRequestError.Unknown, so it never matches.
        private static bool IsConnectFailure(Exception ex)
        {
            return ex is HttpRequestException request
                && (request.HttpRequestError == HttpRequestError.ConnectionError
                    || request.HttpRequestError == HttpRequestError.NameResolutionError);
        }
#endif

        /// <summary>
        /// Determines if an HTTP status code represents a transient error that should be retried.
        /// </summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <returns>True if the status code is transient and should be retried.</returns>
        private static bool IsTransientError(HttpStatusCode statusCode)
        {
            return statusCode switch
            {
                HttpStatusCode.ServiceUnavailable => true,  // 503
                HttpStatusCode.RequestTimeout => true,       // 408
                (HttpStatusCode)TooManyRequestsStatusCode => true, // 429
                HttpStatusCode.GatewayTimeout => true,       // 504
                HttpStatusCode.BadGateway => true,           // 502
                _ => false
            };
        }
    }

    /// <summary>
    /// Exception thrown when a transient HTTP error is encountered.
    /// </summary>
    public class TransientHttpException : HttpRequestException
    {
        /// <summary>
        /// The HTTP status code that caused this exception.
        /// </summary>
#if NET5_0_OR_GREATER
        public new HttpStatusCode StatusCode { get; }
#else
        public HttpStatusCode StatusCode { get; }
#endif

        // The Retry-After the response carried, as a wait from when it arrived. Null for none.
        internal TimeSpan? RetryAfter { get; set; }

        // The undisposed response, set only when the handler hands the last response back.
        internal HttpResponseMessage? Response { get; set; }

        /// <summary>
        /// Creates a new TransientHttpException.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public TransientHttpException(string message, HttpStatusCode statusCode)
#if NET5_0_OR_GREATER
            : base(message, null, statusCode)
#else
            : base(message)
#endif
        {
            StatusCode = statusCode;
        }
    }
}
