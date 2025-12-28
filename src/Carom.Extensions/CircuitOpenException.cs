using System;

namespace Carom.Extensions
{
    /// <summary>
    /// Exception thrown when a circuit breaker is open and rejects a request.
    /// </summary>
    public class CircuitOpenException : Exception
    {
        /// <summary>
        /// The service key for the circuit breaker that opened.
        /// </summary>
        public string ServiceKey { get; }

        public CircuitOpenException(string serviceKey)
            : base($"Circuit breaker for '{serviceKey}' is open")
        {
            ServiceKey = serviceKey;
        }

        public CircuitOpenException(string serviceKey, Exception innerException)
            : base($"Circuit breaker for '{serviceKey}' is open", innerException)
        {
            ServiceKey = serviceKey;
        }
    }
}
