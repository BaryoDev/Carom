using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Carom.Http
{
    /// <summary>
    /// Extension methods for configuring Carom with HttpClient.
    /// </summary>
    public static class CaromHttpExtensions
    {
        /// <summary>
        /// Adds Carom resilience to an HttpClient using the default configuration.
        /// </summary>
        /// <param name="builder">The HttpClient builder.</param>
        /// <returns>The HttpClient builder for chaining.</returns>
        public static IHttpClientBuilder AddCaromResilience(this IHttpClientBuilder builder)
        {
            return builder.AddHttpMessageHandler(() => new CaromHttpHandler());
        }

        /// <summary>
        /// Adds Carom resilience to an HttpClient with the specified number of retries.
        /// </summary>
        /// <param name="builder">The HttpClient builder.</param>
        /// <param name="retries">The number of retry attempts.</param>
        /// <returns>The HttpClient builder for chaining.</returns>
        public static IHttpClientBuilder AddCaromResilience(this IHttpClientBuilder builder, int retries)
        {
            return builder.AddHttpMessageHandler(() => new CaromHttpHandler(retries));
        }

        /// <summary>
        /// Adds Carom resilience to an HttpClient with the specified configuration.
        /// </summary>
        /// <param name="builder">The HttpClient builder.</param>
        /// <param name="config">The bounce configuration.</param>
        /// <returns>The HttpClient builder for chaining.</returns>
        public static IHttpClientBuilder AddCaromResilience(this IHttpClientBuilder builder, Bounce config)
        {
            return builder.AddHttpMessageHandler(() => new CaromHttpHandler(config));
        }

        /// <summary>
        /// Adds Carom resilience to an HttpClient with a configuration action.
        /// </summary>
        /// <param name="builder">The HttpClient builder.</param>
        /// <param name="configure">An action to configure the bounce settings.</param>
        /// <returns>The HttpClient builder for chaining.</returns>
        public static IHttpClientBuilder AddCaromResilience(
            this IHttpClientBuilder builder,
            Func<Bounce, Bounce> configure)
        {
            var config = configure(Bounce.Times(3));
            return builder.AddHttpMessageHandler(() => new CaromHttpHandler(config));
        }
    }
}
