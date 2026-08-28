// Copyright (c) BaryoDev. All rights reserved.
// Licensed under the MPL-2.0 license. See LICENSE file in the project root for full license information.

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
        private int _queued;
        private int _disposed; // 0 = not disposed, 1 = disposed

        public CompartmentState(int maxConcurrency, int queueDepth)
        {
            _maxConcurrency = maxConcurrency;
            _queueDepth = queueDepth;

            // Fix: maxCount should be maxConcurrency, not maxConcurrency + queueDepth
            // The semaphore's initial and max count represent available slots
            // Queue depth bounds waiters via _queued, not the semaphore count
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
        /// Gets the current number of callers waiting for a slot.
        /// </summary>
        public int QueuedCount => Volatile.Read(ref _queued);

        /// <summary>
        /// Attempts to enter the compartment synchronously.
        /// A free slot is taken immediately. Otherwise, if the queue has room, the
        /// caller reserves a queue place and waits for a slot; past the bound it is
        /// shed immediately, because an unbounded wait queue is the failure a
        /// bulkhead exists to prevent.
        /// </summary>
        public bool TryEnter()
        {
            ThrowIfDisposed();

            if (_semaphore.Wait(0))
            {
                Interlocked.Increment(ref _activeCount);
                return true;
            }

            if (_queueDepth == 0)
            {
                return false;
            }

            // Reserve the queue place before waiting, so the bound is enforced at
            // reservation time and a caller past it sheds without ever parking.
            if (Interlocked.Increment(ref _queued) > _queueDepth)
            {
                Interlocked.Decrement(ref _queued);
                return false;
            }

            try
            {
                _semaphore.Wait();
                Interlocked.Increment(ref _activeCount);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _queued);
            }
        }

        /// <summary>
        /// Attempts to enter the compartment asynchronously.
        /// Same contract as <see cref="TryEnter()"/>; the queue-place reservation
        /// happens synchronously before the first await, so callers hold their place
        /// by the time this method returns its task.
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

                if (_queueDepth == 0)
                {
                    return false;
                }

                if (Interlocked.Increment(ref _queued) > _queueDepth)
                {
                    Interlocked.Decrement(ref _queued);
                    return false;
                }

                try
                {
                    await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _activeCount);
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref _queued);
                }
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
