using System.Collections.Concurrent;

namespace Carom.Extensions
{
    /// <summary>
    /// Static store for compartment states, ensuring one state per resource.
    /// </summary>
    internal static class CompartmentStore
    {
        private static readonly ConcurrentDictionary<string, CompartmentState> _states = new();

        /// <summary>
        /// Gets or creates a compartment state for the specified resource.
        /// </summary>
        public static CompartmentState GetOrCreate(string resourceKey, Compartment config)
        {
            return _states.GetOrAdd(resourceKey, _ => 
                new CompartmentState(config.MaxConcurrency, config.QueueDepth));
        }

        /// <summary>
        /// Clears all compartment states (for testing).
        /// </summary>
        public static void Clear() => _states.Clear();
    }
}
