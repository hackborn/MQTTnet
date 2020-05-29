#define WILD_FIXES

#if NETSTANDARD1_3
#undef WILD_FIXES
#endif

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet.Diagnostics;
using MQTTnet.Extensions;

namespace MQTTnet.Internal {
    public sealed class AsyncQueue<TItem> : IDisposable {
        public IMqttNetScopedLogger _logger;
#if WILD_FIXES
        private readonly EventWaitHandle _waiter = new AutoResetEvent(false);
        private const int INFINITE = -1; // Matches win32 (uint 4294967295)
#else
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0);
#endif

        private ConcurrentQueue<TItem> _queue = new ConcurrentQueue<TItem>();

        public int Count => _queue.Count;

        public void LogInfo() {
            var waiter = "semaphoreslim";
#if WILD_FIXES
            waiter = "autoreset";
#endif
            _logger?.Info("AsyncQueue lock=" + waiter + " count=" + Count);
        }

        public void Enqueue(TItem item) {
            _queue.Enqueue(item);
#if WILD_FIXES
//            _logger?.Info("Enqueue() count " + Count);
            _waiter.Set();
#else
            _semaphore.Release();
#endif
        }

#if WILD_FIXES
        // There is a specific bug on iOS where we stop receiving MQTT data.
        // It appears to be deadlockign in _semaphore.WaitAsync() but I haven't
        // tracked down why that is, so the current solution is to flip checking
        // the queue with performing the wait.
        //
        // In theory this would lead to an unbalanced SemaphoreSlim; in practice
        // I've seen no issues. But to continue these changes, should probably
        // adjust the producer / consumer pattern to not be tied to push / pop
        // pairings but instead conceptually be completely separate, with one end
        // pushing as much as possible and the other popping as much as possible.
        public async Task<AsyncQueueDequeueResult<TItem>> TryDequeueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var item))
                {
                    return new AsyncQueueDequeueResult<TItem>(true, item);
                }
                try
                {
                    await _waiter.WaitOneAsync(INFINITE, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException)
                {
                    return new AsyncQueueDequeueResult<TItem>(false, default(TItem));
                }
            }

            return new AsyncQueueDequeueResult<TItem>(false, default(TItem));
        }
#else
        public async Task<AsyncQueueDequeueResult<TItem>> TryDequeueAsync(CancellationToken cancellationToken) {
            while (!cancellationToken.IsCancellationRequested) {
                try {
                    await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                } catch (OperationCanceledException) {
                    return new AsyncQueueDequeueResult<TItem>(false, default(TItem));
                }

                if (_queue.TryDequeue(out var item)) {
                    return new AsyncQueueDequeueResult<TItem>(true, item);
                }
            }

            return new AsyncQueueDequeueResult<TItem>(false, default(TItem));
        }
#endif

        public AsyncQueueDequeueResult<TItem> TryDequeue() {
            if (_queue.TryDequeue(out var item)) {
                return new AsyncQueueDequeueResult<TItem>(true, item);
            }

            return new AsyncQueueDequeueResult<TItem>(false, default(TItem));
        }

        public void Clear() {
            Interlocked.Exchange(ref _queue, new ConcurrentQueue<TItem>());
        }

        public void Dispose() {
#if WILD_FIXES
            _waiter?.Dispose();
#else
            _semaphore?.Dispose();
#endif
        }
    }
}
