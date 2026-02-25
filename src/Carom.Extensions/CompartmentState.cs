using System;
using System.Threading;
using System.Threading.Tasks;

namespace Carom.Extensions
{
    /// <summary>
    /// Manages the state of a bulkhead compartment using a semaphore.
    /// Implements IDisposable to properly clean up the semaphore resource.
    /// </summary>
    internal class CompartmentState : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly int _maxConcurrency;
        private readonly int _queueDepth;
        private int _activeCount;
        private int _disposed; // 0 = not disposed, 1 = disposed

        public CompartmentState(int maxConcurrency, int queueDepth)
        {
            _maxConcurrency = maxConcurrency;
            _queueDepth = queueDepth;

            // Fix: maxCount should be maxConcurrency, not maxConcurrency + queueDepth
            // The semaphore's initial and max count represent available slots
            // Queue depth is handled by waiting/timing out, not by the semaphore count
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }

        /// <summary>
        /// Gets the current number of active executions.
        /// </summary>
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>
        /// Gets the maximum allowed concurrent executions.
        /// </summary>
        public int MaxConcurrency => _maxConcurrency;

        /// <summary>
        /// Gets the configured queue depth.
        /// </summary>
        public int QueueDepth => _queueDepth;

        /// <summary>
        /// Attempts to enter the compartment synchronously.
        /// </summary>
        public bool TryEnter()
        {
            ThrowIfDisposed();

            if (_semaphore.Wait(0))
            {
                Interlocked.Increment(ref _activeCount);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Attempts to enter the compartment synchronously with a timeout.
        /// Uses queue depth to determine wait time.
        /// </summary>
        public bool TryEnter(TimeSpan timeout)
        {
            ThrowIfDisposed();

            // If queue depth is 0, don't wait at all
            if (_queueDepth == 0)
            {
                return TryEnter();
            }

            if (_semaphore.Wait(timeout))
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
            ThrowIfDisposed();

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
        /// Attempts to enter the compartment asynchronously with a timeout.
        /// Uses queue depth to determine wait time.
        /// </summary>
        public async Task<bool> TryEnterAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            // If queue depth is 0, don't wait at all
            if (_queueDepth == 0)
            {
                return await TryEnterAsync(ct).ConfigureAwait(false);
            }

            try
            {
                if (await _semaphore.WaitAsync(timeout, ct).ConfigureAwait(false))
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
            if (Volatile.Read(ref _disposed) == 1) return;

            Interlocked.Decrement(ref _activeCount);
            try
            {
                _semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore was disposed, ignore
            }
            catch (SemaphoreFullException)
            {
                // Semaphore is already at max count, ignore
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(CompartmentState));
            }
        }

        /// <summary>
        /// Disposes the semaphore resource.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            _semaphore.Dispose();
        }
    }
}
