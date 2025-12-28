using System.Collections.Concurrent;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for throttle states, ensuring one state per service.
    /// </summary>
    internal static class ThrottleStore
    {
        private static readonly ConcurrentDictionary<string, ThrottleState> _states = new();

        /// <summary>
        /// Gets or creates a throttle state for the specified service.
        /// </summary>
        public static ThrottleState GetOrCreate(string serviceKey, Throttle config)
        {
            return _states.GetOrAdd(serviceKey, _ =>
                new ThrottleState(config.MaxRequests, config.TimeWindow, config.BurstSize));
        }

        /// <summary>
        /// Clears all throttle states (for testing).
        /// </summary>
        public static void Clear() => _states.Clear();
    }
}
