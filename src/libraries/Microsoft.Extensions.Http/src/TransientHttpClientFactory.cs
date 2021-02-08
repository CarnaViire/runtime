// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.Http
{
    internal class TransientHttpClientFactory : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        private readonly IServiceProvider _services;
        private readonly IOptionsMonitor<HttpClientFactoryOptions> _optionsMonitor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpMessageHandlerBuilderFilter[] _filters;

        // internal for tests
        internal readonly SingletonHttpMessageHandlerCache _singletonCache;

        public TransientHttpClientFactory(
            IServiceProvider services,
            IServiceScopeFactory scopeFactory,
            IOptionsMonitor<HttpClientFactoryOptions> optionsMonitor,
            SingletonHttpMessageHandlerCache singletonCache,
            IEnumerable<IHttpMessageHandlerBuilderFilter> filters)
        {
            // todo: check for nulls

            _services = services;
            _scopeFactory = scopeFactory;
            _optionsMonitor = optionsMonitor;
            _singletonCache = singletonCache;
            _filters = filters.ToArray();
        }

        public HttpClient CreateClient(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            HttpClientFactoryOptions options = _optionsMonitor.Get(name);
            HttpMessageHandler handler = GetOrCreateHandler(name, options);

            var client = new HttpClient(handler);
            for (int i = 0; i < options.HttpClientActions.Count; i++)
            {
                options.HttpClientActions[i](client);
            }

            return client;
        }

        public HttpMessageHandler CreateHandler(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            HttpClientFactoryOptions options = _optionsMonitor.Get(name);
            return GetOrCreateHandler(name, options);
        }

        private HttpMessageHandler GetOrCreateHandler(string name, HttpClientFactoryOptions options)
        {
            if (options.PreserveExistingScope && options.SuppressHandlerScope)
            {
                throw new InvalidOperationException(SR.PreserveExistingScope_SuppressHandlerScope_BothTrueIsInvalid);
            }

            if (options.PreserveExistingScope)
            {
                // todo: check _services is scoped
                ScopedHttpMessageHandlerCache scopedCache = _services.GetRequiredService<ScopedHttpMessageHandlerCache>();
                return scopedCache.GetOrAdd(name, () => CreateScopedHandler(name, options));
            }
            else
            {
                ActiveHandlerTrackingEntry entry = _singletonCache.GetOrAdd(name, () => CreateRotatedHandlerEntry(name, options));
                Debug.Assert(!entry.IsPrimary);
                return entry.Handler;
            }
        }

        private LifetimeTrackingHttpMessageHandler CreateScopedHandler(string name, HttpClientFactoryOptions options)
        {
            Debug.Assert(options.PreserveExistingScope);
            Debug.Assert(!options.SuppressHandlerScope);

            if (options._primaryHandlerChanged)
            {
                throw new InvalidOperationException(SR.PreserveExistingScope_CannotChangePrimaryHandler);
            }

            ActiveHandlerTrackingEntry entry = _singletonCache.GetOrAdd(name, () =>
                {
                    var handler = new LifetimeTrackingHttpMessageHandler(new HttpClientHandler());
                    return new ActiveHandlerTrackingEntry(name, handler, true, null, options.HandlerLifetime);
                });
            Debug.Assert(entry.IsPrimary);
            LifetimeTrackingHttpMessageHandler primaryHandler = entry.Handler;

            // todo: check _services is scoped
            HttpMessageHandlerBuilder builder = _services.GetRequiredService<HttpMessageHandlerBuilder>();
            builder.Name = name;
            ConfigureBuilder(builder, options);

            if (builder.PrimaryHandlerChanged)
            {
                throw new InvalidOperationException(SR.PreserveExistingScope_CannotChangePrimaryHandler);
            }

            return new LifetimeTrackingHttpMessageHandler(builder.Build(primaryHandler));
        }

        private ActiveHandlerTrackingEntry CreateRotatedHandlerEntry(string name, HttpClientFactoryOptions options)
        {
            Debug.Assert(!options.PreserveExistingScope);

            // todo: check _services is root
            IServiceProvider services = _services;
            var scope = (IServiceScope)null;

            if (!options.SuppressHandlerScope)
            {
                scope = _scopeFactory.CreateScope();
                services = scope.ServiceProvider;
            }

            try
            {
                var builder = services.GetRequiredService<HttpMessageHandlerBuilder>();
                builder.Name = name;
                ConfigureBuilder(builder, options);

                var handler = new LifetimeTrackingHttpMessageHandler(builder.Build());
                return new ActiveHandlerTrackingEntry(name, handler, false, scope, options.HandlerLifetime);
            }
            catch
            {
                // If something fails while creating the handler, dispose the services.
                scope?.Dispose();
                throw;
            }
        }

        private void ConfigureBuilder(HttpMessageHandlerBuilder builder, HttpClientFactoryOptions options)
        {
            // This is similar to the initialization pattern in:
            // https://github.com/aspnet/Hosting/blob/e892ed8bbdcd25a0dafc1850033398dc57f65fe1/src/Microsoft.AspNetCore.Hosting/Internal/WebHost.cs#L188
            Action<HttpMessageHandlerBuilder> configure = Configure;
            for (int i = _filters.Length - 1; i >= 0; i--)
            {
                configure = _filters[i].Configure(configure);
            }

            configure(builder);

            void Configure(HttpMessageHandlerBuilder b)
            {
                for (int i = 0; i < options.HttpMessageHandlerBuilderActions.Count; i++)
                {
                    options.HttpMessageHandlerBuilderActions[i](b);
                }
            }
        }
    }
}
