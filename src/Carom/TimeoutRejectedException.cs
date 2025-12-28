using System;

namespace Carom
{
    /// <summary>
    /// Exception thrown when an operation exceeds its timeout.
    /// </summary>
    public class TimeoutRejectedException : OperationCanceledException
    {
        public TimeSpan Timeout { get; }

        public TimeoutRejectedException(TimeSpan timeout)
            : base($"Operation timed out after {timeout.TotalMilliseconds}ms")
        {
            Timeout = timeout;
        }

        public TimeoutRejectedException(TimeSpan timeout, Exception innerException)
            : base($"Operation timed out after {timeout.TotalMilliseconds}ms", innerException)
        {
            Timeout = timeout;
        }
    }
}
