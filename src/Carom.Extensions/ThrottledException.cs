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
