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
    internal class DefaultHttpClientFactory : IHttpClientFactory, IHttpMessageHandlerFactory
    {
        private readonly IServiceProvider _services;
        private readonly IOptionsMonitor<HttpClientFactoryOptions> _optionsMonitor;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHttpMessageHandlerBuilderFilter[] _filters;
        private readonly ServiceProviderChecker _serviceProviderChecker;

        // internal for tests
        internal readonly DefaultHttpMessageHandlerCache _singletonCache;

        public DefaultHttpClientFactory(
            IServiceProvider services,
            IServiceScopeFactory scopeFactory,
            ServiceProviderChecker serviceProviderChecker,
            IOptionsMonitor<HttpClientFactoryOptions> optionsMonitor,
            DefaultHttpMessageHandlerCache singletonCache,
            IEnumerable<IHttpMessageHandlerBuilderFilter> filters)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            if (scopeFactory == null)
            {
                throw new ArgumentNullException(nameof(scopeFactory));
            }
            if (serviceProviderChecker == null)
            {
                throw new ArgumentNullException(nameof(serviceProviderChecker));
            }
            if (optionsMonitor == null)
            {
                throw new ArgumentNullException(nameof(optionsMonitor));
            }
            if (singletonCache == null)
            {
                throw new ArgumentNullException(nameof(singletonCache));
            }
            if (filters == null)
            {
                throw new ArgumentNullException(nameof(filters));
            }

            _services = services;
            _scopeFactory = scopeFactory;
            _serviceProviderChecker = serviceProviderChecker;
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

            if (options.PreserveExistingScope && !_serviceProviderChecker.IsScoped(_services))
            {
                throw new Exception(); //todo
            }

            if (options.PreserveExistingScope)
            {
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
                    // Wrap the handler so we can ensure the inner handler outlives the outer handler
                    var handler = new LifetimeTrackingHttpMessageHandler(new HttpClientHandler());
                    return new ActiveHandlerTrackingEntry(name, handler, true, null, options.HandlerLifetime);
                });
            Debug.Assert(entry.IsPrimary);
            LifetimeTrackingHttpMessageHandler primaryHandler = entry.Handler;

            HttpMessageHandlerBuilder builder = _services.GetRequiredService<HttpMessageHandlerBuilder>();
            builder.Name = name;
            ConfigureBuilder(builder, options);

            if (builder.PrimaryHandlerChanged)
            {
                throw new InvalidOperationException(SR.PreserveExistingScope_CannotChangePrimaryHandler);
            }

            // Wrap the handler so we can ensure the inner handler outlives HttpClient
            return new LifetimeTrackingHttpMessageHandler(builder.Build(primaryHandler));
        }

        private ActiveHandlerTrackingEntry CreateRotatedHandlerEntry(string name, HttpClientFactoryOptions options)
        {
            Debug.Assert(!options.PreserveExistingScope);

            IServiceScope scope = null;
            IServiceProvider services;

            try
            {
                if (!options.SuppressHandlerScope)
                {
                    scope = _scopeFactory.CreateScope();
                    services = scope.ServiceProvider;
                }
                else
                {
                    services = _serviceProviderChecker.RootServiceProvider;
                }

                var builder = services.GetRequiredService<HttpMessageHandlerBuilder>();
                builder.Name = name;
                ConfigureBuilder(builder, options);

                // Wrap the handler so we can ensure the inner handler outlives HttpClient
                var handler = new LifetimeTrackingHttpMessageHandler(builder.Build());

                // Note that we can't start the timer here. That would introduce a very very subtle race condition
                // with very short expiry times. We need to wait until we've actually handed out the handler once
                // to start the timer.
                //
                // Otherwise it would be possible that we start the timer here, immediately expire it (very short
                // timer) and then dispose it without ever creating a client. That would be bad. It's unlikely
                // this would happen, but we want to be sure.
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
