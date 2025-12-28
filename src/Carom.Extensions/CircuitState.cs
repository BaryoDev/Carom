namespace Carom.Extensions
{
    /// <summary>
    /// Represents the state of a circuit breaker.
    /// </summary>
    public enum CircuitState
    {
        /// <summary>
        /// Circuit is closed (normal operation, requests flowing).
        /// </summary>
        Closed = 0,

        /// <summary>
        /// Circuit is open (rejecting all requests to protect backend).
        /// </summary>
        Open = 1,

        /// <summary>
        /// Circuit is half-open (testing if backend recovered).
        /// </summary>
        HalfOpen = 2
    }
}
