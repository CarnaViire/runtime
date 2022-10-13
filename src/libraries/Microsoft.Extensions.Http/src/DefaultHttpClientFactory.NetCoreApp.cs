// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Http
{
    internal partial class DefaultHttpClientFactory
    {
        internal ConcurrentDictionary<string, PrimaryHandlerTrackingEntry>? _primaryHandlers;
        private void SetUpPrimaryHandlersCache()
        {
            _primaryHandlers = new ConcurrentDictionary<string, PrimaryHandlerTrackingEntry>(StringComparer.Ordinal);
        }

        // This method executes from within a lock (via Lazy)
        internal void ConfigurePrimaryHandler(HttpMessageHandlerBuilder builder, HttpClientFactoryOptions options)
        {
            bool canCachePrimaryHandler = !_userFiltersInjected && options.HttpMessageHandlerBuilderActions.Count == 0;

            if (!canCachePrimaryHandler)
            {
                if (options.PrimaryHandlerFactory != null)
                {
                    builder.PrimaryHandler = options.PrimaryHandlerFactory();
                }
                return;
            }

            if (_primaryHandlers!.TryGetValue(builder.Name!, out PrimaryHandlerTrackingEntry? primaryHandlerEntry))
            {
                primaryHandlerEntry.KeepAlive();
                builder.PrimaryHandler = primaryHandlerEntry.Handler;
                return;
            }

            HttpMessageHandler primaryHandler;
            if (options.PrimaryHandlerFactory != null)
            {
                primaryHandler = options.PrimaryHandlerFactory();

                if (primaryHandler is SocketsHttpHandler socketsHandler)
                {
                    if (socketsHandler.PooledConnectionLifetime == Timeout.InfiniteTimeSpan || socketsHandler.PooledConnectionLifetime > options.HandlerLifetime)
                    {
                        socketsHandler.PooledConnectionLifetime = options.HandlerLifetime;
                    }
                }
                else if (primaryHandler is HttpClientHandler httpClientHandler)
                {
                    if (!OperatingSystem.IsBrowser()) // trust browser to handle DNS changes
                    {
                        canCachePrimaryHandler = TrySetPooledConnectionLifetime(httpClientHandler, options.HandlerLifetime);
                    }
                }
                else
                {
                    canCachePrimaryHandler = false;
                }
            }
            else
            {
                if (OperatingSystem.IsBrowser()) // trust browser to handle DNS changes
                {
                    primaryHandler = new HttpClientHandler();
                }
                else
                {
                    primaryHandler = new SocketsHttpHandler()
                    {
                        PooledConnectionLifetime = options.HandlerLifetime
                    };
                }
            }

            if (canCachePrimaryHandler)
            {
                var lifetimeHandler = new LifetimeTrackingHttpMessageHandler(primaryHandler);
                _primaryHandlers.TryAdd(builder.Name!, new PrimaryHandlerTrackingEntry(builder.Name!, lifetimeHandler, TimeSpan.FromMinutes(2).Ticks)); // TODO
                primaryHandler = lifetimeHandler;
            }

            builder.PrimaryHandler = primaryHandler;
        }

        private static bool TrySetPooledConnectionLifetime(HttpClientHandler handler, TimeSpan pooledConnectionLifetime)
        {
            //TODO
            return false;
        }
    }
}
