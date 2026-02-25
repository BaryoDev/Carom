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

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
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
