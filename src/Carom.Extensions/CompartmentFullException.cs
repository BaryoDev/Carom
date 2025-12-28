using System;

namespace Carom.Extensions
{
    /// <summary>
    /// Exception thrown when a compartment is full and cannot accept more requests.
    /// </summary>
    public class CompartmentFullException : Exception
    {
        public string ResourceKey { get; }
        public int MaxConcurrency { get; }

        public CompartmentFullException(string resourceKey, int maxConcurrency)
            : base($"Compartment '{resourceKey}' is full (max concurrency: {maxConcurrency})")
        {
            ResourceKey = resourceKey;
            MaxConcurrency = maxConcurrency;
        }

        public CompartmentFullException(string resourceKey, int maxConcurrency, Exception innerException)
            : base($"Compartment '{resourceKey}' is full (max concurrency: {maxConcurrency})", innerException)
        {
            ResourceKey = resourceKey;
            MaxConcurrency = maxConcurrency;
        }
    }
}
