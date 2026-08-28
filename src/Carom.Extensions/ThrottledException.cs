// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

using System;

namespace Carom.Extensions
{
    /// <summary>
    /// Exception thrown when rate limit is exceeded.
    /// </summary>
    public class ThrottledException : Exception
    {
        public string ServiceKey { get; }
        public int MaxRequests { get; }
        public TimeSpan TimeWindow { get; }

        public ThrottledException(string serviceKey, int maxRequests, TimeSpan timeWindow)
            : base($"Rate limit exceeded for '{serviceKey}' ({maxRequests} requests per {timeWindow.TotalSeconds}s)")
        {
            ServiceKey = serviceKey;
            MaxRequests = maxRequests;
            TimeWindow = timeWindow;
        }

        public ThrottledException(string serviceKey, int maxRequests, TimeSpan timeWindow, Exception innerException)
            : base($"Rate limit exceeded for '{serviceKey}' ({maxRequests} requests per {timeWindow.TotalSeconds}s)", innerException)
        {
            ServiceKey = serviceKey;
            MaxRequests = maxRequests;
            TimeWindow = timeWindow;
        }
    }
}
