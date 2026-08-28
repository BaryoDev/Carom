// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

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
