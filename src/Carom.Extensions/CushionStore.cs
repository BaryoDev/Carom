using System.Collections.Concurrent;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for circuit breaker states, keyed by service name.
    /// </summary>
    internal static class CushionStore
    {
        private static readonly ConcurrentDictionary<string, CushionState> States = new ConcurrentDictionary<string, CushionState>();

        /// <summary>
        /// Gets or creates a circuit breaker state for the given service key.
        /// </summary>
        public static CushionState GetOrCreate(string serviceKey, Cushion config)
        {
            return States.GetOrAdd(serviceKey, _ => new CushionState(config.SamplingWindow));
        }

        /// <summary>
        /// Clears all circuit breaker states (useful for testing).
        /// </summary>
        internal static void Clear()
        {
            States.Clear();
        }
    }
}
