// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading;

namespace Microsoft.Extensions.Http
{
    internal sealed class PrimaryHandlerTrackingEntry
    {
        private long _idleSinceTickCount;
        private readonly long _idleTimeout;

        public PrimaryHandlerTrackingEntry(
            string name,
            LifetimeTrackingHttpMessageHandler handler,
            long idleTimeout)
        {
            Name = name;
            Handler = handler;
            _idleTimeout = idleTimeout;
            _idleSinceTickCount = Environment.TickCount64;
        }

        public string Name { get; }

        public LifetimeTrackingHttpMessageHandler Handler { get; }

        public void KeepAlive() => Interlocked.Exchange(ref _idleSinceTickCount, Environment.TickCount64);

        public bool IsStale => (Environment.TickCount64 - Interlocked.Read(ref _idleSinceTickCount)) > _idleTimeout;
    }
}
