using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
        /// Enable only when requests are idempotent (e.g., carry an idempotency key).
        /// </summary>
        public bool RetryNonIdempotentRequests { get; set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!RetryNonIdempotentRequests && !IsIdempotent(request.Method))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            // Buffered before the first attempt, because every attempt re-sends this same request and
            // a forward-only body is already consumed by the time the second one runs. The failure
            // without this is silent rather than loud: no exception, the retry simply sends an empty
            // body and the server answers it. Measured before fixing, with a forward-only stream:
            // two calls, bodies ["payload", ""].
            //
            // The cost is holding the body in memory, which is why retry stays off for
            // non-idempotent methods unless asked for.
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            }

            return await Carom.ShotAsync(
                async () =>
                {
                    var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    if (IsTransientError(response.StatusCode))
                    {
                        var statusCode = response.StatusCode;
                        response.Dispose();
                        throw new TransientHttpException($"Transient HTTP error: {statusCode}", statusCode);
                    }
                    return response;
                },
                _config.Retries,
                _config.BaseDelay,
                timeout: null,
                shouldBounce: IsTransientException,
                disableJitter: _config.DisableJitter,
                ct: cancellationToken).ConfigureAwait(false);
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
            // Retry on HttpRequestException (network errors) and TransientHttpException
            return ex is HttpRequestException || ex is TransientHttpException;
        }

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
        public HttpStatusCode StatusCode { get; }

        /// <summary>
        /// Creates a new TransientHttpException.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public TransientHttpException(string message, HttpStatusCode statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
