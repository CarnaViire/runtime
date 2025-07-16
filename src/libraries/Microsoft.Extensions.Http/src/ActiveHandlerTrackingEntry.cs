// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace Microsoft.Extensions.Http
{
    // Thread-safety: We treat this class as immutable except for the timer. Creating a new object
    // for the 'expiry' pool simplifies the threading requirements significantly.
    internal sealed class ActiveHandlerTrackingEntry : IDisposable
    {
        private static readonly TimerCallback _timerCallback = (s) => ((ActiveHandlerTrackingEntry)s!).Timer_Tick();
        private readonly object _lock;
        private bool _timerInitialized;
        private Timer? _timer;
        private TimerCallback? _callback;

        public ActiveHandlerTrackingEntry(
            string name,
            LifetimeTrackingHttpMessageHandler handler,
            IServiceScope? scope,
            TimeSpan lifetime)
        {
            Name = name;
            Handler = handler;
            Scope = scope;
            Lifetime = lifetime;

            _lock = new object();
        }

        public LifetimeTrackingHttpMessageHandler Handler { get; }

        public TimeSpan Lifetime { get; }

        public string Name { get; }

        public IServiceScope? Scope { get; }

        public void StartExpiryTimer(TimerCallback callback)
        {
            if (Lifetime == Timeout.InfiniteTimeSpan)
            {
                return; // never expires.
            }

            if (Volatile.Read(ref _timerInitialized))
            {
                return;
            }

            StartExpiryTimerSlow(callback);
        }

        private void StartExpiryTimerSlow(TimerCallback callback)
        {
            Debug.Assert(Lifetime != Timeout.InfiniteTimeSpan);

            lock (_lock)
            {
                if (Volatile.Read(ref _timerInitialized))
                {
                    return;
                }

                _callback = callback;
                _timer = NonCapturingTimer.Create(_timerCallback, this, Lifetime, Timeout.InfiniteTimeSpan);
                _timerInitialized = true;
            }
        }

        private void Timer_Tick()
        {
            Debug.Assert(_callback != null);

            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;

                    // Timer expired - invoke the callback to move this to expired handlers
                    _callback(this);
                }
            }
        }

        public void StopTimer()
        {
            lock (_lock)
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                // If timer is already disposed or entry was never initialized, nothing to do
                if (_timerInitialized && _timer == null)
                {
                    return; // Already disposed
                }

                // Set the timer as initialized in case Dispose happens before timer creation
                _timerInitialized = true;

                // Stop and dispose the timer if it exists
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                // Dispose the inner handler and scope directly instead of invoking callback
                Handler.InnerHandler?.Dispose();
                Scope?.Dispose();
            }
        }
    }
}
