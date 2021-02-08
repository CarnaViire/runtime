// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Microsoft.Extensions.Http
{
    internal class ScopedHttpMessageHandlerCache
    {
        // Cache for creating a chain only once per scope
        private readonly ConcurrentDictionary<string, Lazy<LifetimeTrackingHttpMessageHandler>> _cache;

        public ScopedHttpMessageHandlerCache()
        {
            // Same comparer as for named options
            _cache = new ConcurrentDictionary<string, Lazy<LifetimeTrackingHttpMessageHandler>>(StringComparer.Ordinal);
        }

        public LifetimeTrackingHttpMessageHandler GetOrAdd(string name, Func<LifetimeTrackingHttpMessageHandler> factory)
        {
            var lazy = _cache.GetOrAdd(name,
                new Lazy<LifetimeTrackingHttpMessageHandler>(factory, LazyThreadSafetyMode.ExecutionAndPublication));
            return lazy.Value;
        }
    }
}
