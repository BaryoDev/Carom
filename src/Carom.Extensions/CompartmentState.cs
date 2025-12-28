using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Manages the state of a bulkhead compartment using a semaphore.
    /// </summary>
    internal class CompartmentState
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxConcurrency;
        private int _activeCount;

        public CompartmentState(int maxConcurrency, int queueDepth)
        {
            _maxConcurrency = maxConcurrency;
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency + queueDepth);
        }

        /// <summary>
        /// Gets the current number of active executions.
        /// </summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>
        /// Attempts to enter the compartment synchronously.
        /// </summary>
        public bool TryEnter()
        {
            if (_semaphore.Wait(0))
            {
                Interlocked.Increment(ref _activeCount);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Attempts to enter the compartment asynchronously.
        /// </summary>
        public async Task<bool> TryEnterAsync(CancellationToken ct = default)
        {
            try
            {
                if (await _semaphore.WaitAsync(0, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _activeCount);
                    return true;
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Releases the compartment slot.
        /// </summary>
        public void Release()
        {
            Interlocked.Decrement(ref _activeCount);
            _semaphore.Release();
        }
    }
}
