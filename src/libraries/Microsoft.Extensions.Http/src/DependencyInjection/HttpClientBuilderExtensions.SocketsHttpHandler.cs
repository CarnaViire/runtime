// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET5_0_OR_GREATER

using System;
using System.Net.Http;
using System.Net;
using Microsoft.Extensions.Http;
using System.Runtime.Versioning;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extension methods for configuring an <see cref="IHttpClientBuilder"/>
    /// </summary>
    public static partial class HttpClientBuilderExtensions
    {
        /// <summary>
        /// Adds a delegate that will be used to configure the primary <see cref="SocketsHttpHandler"/> for a
        /// named <see cref="HttpClient"/>.
        /// </summary>
        /// <param name="builder">The <see cref="IHttpClientBuilder"/>.</param>
        /// <param name="configureHandler">A delegate that is used to configure a previously set or default primary <see cref="SocketsHttpHandler"/>.</param>
        /// <returns>An <see cref="IHttpClientBuilder"/> that can be used to configure the client.</returns>
        /// <remarks>
        /// <para>
        /// The <see cref="IServiceProvider"/> argument provided to <paramref name="configureHandler"/> will be
        /// a reference to a scoped service provider that shares the lifetime of the handler being constructed.
        /// </para>
        /// </remarks>
        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static IHttpClientBuilder UseSocketsHttpHandler(this IHttpClientBuilder builder, Action<SocketsHttpHandler, IServiceProvider> configureHandler)
        {
            ThrowHelper.ThrowIfNull(builder);

            Action<IPrimaryHandlerBuilder> action = b =>
            {
                SocketsHttpHandler socketsHttpHandler;
                if (b is DefaultHttpMessageHandlerBuilder dhb && dhb._primaryHandler is SocketsHttpHandler dhmhbSocketsHandler) // accessing field to avoid unnecessary creation of an HttpClientHandler
                {
                    socketsHttpHandler = dhmhbSocketsHandler;
                }
                else
                {
                    socketsHttpHandler = new SocketsHttpHandler();
                }

                configureHandler(socketsHttpHandler, b.Services);
                b.PrimaryHandler = socketsHttpHandler;
            };

            if (builder.Name is null)
            {
                HttpClientFactoryOptions.Default!.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
                return builder;
            }

            builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
            {
                options.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
            });

            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static IHttpClientBuilder UseSocketsHttpHandler(this IHttpClientBuilder builder, Action<SocketsHttpHandler> configureHandler)
        {
            return UseSocketsHttpHandler(builder, (SocketsHttpHandler h, IServiceProvider _) => configureHandler(h));
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static IHttpClientBuilder UseSocketsHttpHandler(this IHttpClientBuilder builder, Action<ISocketsHttpHandlerBuilder> configureBuilder)
        {
            ThrowHelper.ThrowIfNull(builder);

            Action<IPrimaryHandlerBuilder> action = b =>
            {
                SocketsHttpHandler socketsHttpHandler;
                if (b is DefaultHttpMessageHandlerBuilder dhb && dhb._primaryHandler is SocketsHttpHandler dhmhbSocketsHandler) // accessing field to avoid unnecessary creation of an HttpClientHandler
                {
                    socketsHttpHandler = dhmhbSocketsHandler;
                }
                else
                {
                    socketsHttpHandler = new SocketsHttpHandler();
                }

                b.PrimaryHandler = socketsHttpHandler;
            };

            if (builder.Name is null)
            {
                HttpClientFactoryOptions.Default!.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
            }
            else
            {
                builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
                {
                    options.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
                });
            }

            var handlerBuilder = new DefaultSocketsHttpHandlerBuilder(builder);
            configureBuilder(handlerBuilder);
            handlerBuilder.Build(); // adds all actions to HCF's options PrimaryHandlerActions

            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static IHttpClientBuilder UseSocketsHttpHandler(this IHttpClientBuilder builder)
        {
            ThrowHelper.ThrowIfNull(builder);

            Action<IPrimaryHandlerBuilder> action = b =>
            {
                SocketsHttpHandler socketsHttpHandler;
                if (b is DefaultHttpMessageHandlerBuilder dhb && dhb._primaryHandler is SocketsHttpHandler dhmhbSocketsHandler) // accessing field to avoid unnecessary creation of an HttpClientHandler
                {
                    socketsHttpHandler = dhmhbSocketsHandler;
                }
                else
                {
                    socketsHttpHandler = new SocketsHttpHandler();
                }

                b.PrimaryHandler = socketsHttpHandler;
            };

            if (builder.Name is null)
            {
                HttpClientFactoryOptions.Default!.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
            }
            else
            {
                builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
                {
                    options.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
                });
            }

            return builder;
        }
    }

    public interface ISocketsHttpHandlerBuilder
    {
        string Name { get; }
        IServiceCollection Services { get; }
    }

    internal class DefaultSocketsHttpHandlerBuilder : ISocketsHttpHandlerBuilder
    {
        private SocketsHttpHandlerOptions _options;
        internal SocketsHttpHandlerOptions Options => _options;
        internal void CleanOptions() => _options = new();
        internal void UpdateOptions(Func<SocketsHttpHandlerOptions, SocketsHttpHandlerOptions> updateOptions)
        {
            _options = updateOptions(_options) with { Modified = true };
        }

        private IHttpClientBuilder _httpClientBuilder;
        public string Name => _httpClientBuilder.Name;
        public IServiceCollection Services => _httpClientBuilder.Services;

        public DefaultSocketsHttpHandlerBuilder(IHttpClientBuilder httpClientBuilder)
        {
            _httpClientBuilder = httpClientBuilder;
        }
    }


    public static class SocketsHttpHandlerBuilderExtensions
    {
        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder Configure(this ISocketsHttpHandlerBuilder builder, Action<SocketsHttpHandler, IServiceProvider> configure)
        {
            ThrowHelper.ThrowIfNull(builder);
            ThrowHelper.ThrowIfNull(configure);

            SocketsHttpHandlerOptions options = default;
            if (builder is DefaultSocketsHttpHandlerBuilder db && db.Options.Modified)
            {
                options = db.Options;
                db.CleanOptions();
            }

            if (options.Modified)
            {
                AddPrimaryHandlerAction(builder, handler => options.Apply(handler));
            }

            //if SocketsHttpHandlerBuilder was created, it ensures PrimaryHandler is SocketsHttpHandler
            Action<IPrimaryHandlerBuilder> action = b => configure((SocketsHttpHandler)b.PrimaryHandler, b.Services);

            if (builder.Name is null)
            {
                HttpClientFactoryOptions.Default!.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
            }
            else
            {
                builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
                {
                    options.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
                });
            }

            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder Configure(this ISocketsHttpHandlerBuilder builder, Action<SocketsHttpHandler> configure)
        {
            ThrowHelper.ThrowIfNull(builder);
            ThrowHelper.ThrowIfNull(configure);

            SocketsHttpHandlerOptions options = default;
            if (builder is DefaultSocketsHttpHandlerBuilder db && db.Options.Modified)
            {
                options = db.Options;
                db.CleanOptions();
            }

            if (options.Modified)
            {
                AddPrimaryHandlerAction(builder, handler => options.Apply(handler));
            }

            AddPrimaryHandlerAction(builder, configure);

            return builder;
        }

        internal static void Build(this ISocketsHttpHandlerBuilder builder)
        {
            if (builder is DefaultSocketsHttpHandlerBuilder db && db.Options.Modified)
            {
                SocketsHttpHandlerOptions options = db.Options;
                db.CleanOptions();
                AddPrimaryHandlerAction(builder, handler => options.Apply(handler));
            }
        }

        private static bool TryUpdateOptions(this ISocketsHttpHandlerBuilder builder, Func<SocketsHttpHandlerOptions, SocketsHttpHandlerOptions> updateOptions)
        {
            if (builder is DefaultSocketsHttpHandlerBuilder db)
            {
                db.UpdateOptions(updateOptions);
                return true;
            }
            else
            {
                return false;
            }
        }

        private static void AddPrimaryHandlerAction(this ISocketsHttpHandlerBuilder builder,  Action<SocketsHttpHandler> configure)
        {
            //if SocketsHttpHandlerBuilder was created, it ensures PrimaryHandler is SocketsHttpHandler
            Action<IPrimaryHandlerBuilder> action = b => configure((SocketsHttpHandler)b.PrimaryHandler);

            if (builder.Name is null)
            {
                HttpClientFactoryOptions.Default!.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
            }
            else
            {
                builder.Services.Configure<HttpClientFactoryOptions>(builder.Name, options =>
                {
                    options.AddPrimaryHandlerAction(action, disregardPreviousActions: false);
                });
            }
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetAllowAutoRedirect(this ISocketsHttpHandlerBuilder builder, bool allowAutoRedirect)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { AllowAutoRedirect = allowAutoRedirect }))
            {
                return builder;
            }

            Configure(builder, handler => handler.AllowAutoRedirect = allowAutoRedirect);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetUseCookies(this ISocketsHttpHandlerBuilder builder, bool useCookies)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { UseCookies = useCookies }))
            {
                return builder;
            }

            Configure(builder, handler => handler.UseCookies = useCookies);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetMaxConnectionsPerServer(this ISocketsHttpHandlerBuilder builder, int maxConnectionsPerServer)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { MaxConnectionsPerServer = maxConnectionsPerServer }))
            {
                return builder;
            }

            Configure(builder, handler => handler.MaxConnectionsPerServer = maxConnectionsPerServer);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetAutomaticDecompression(this ISocketsHttpHandlerBuilder builder, DecompressionMethods automaticDecompression)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { AutomaticDecompression = automaticDecompression }))
            {
                return builder;
            }

            Configure(builder, handler => handler.AutomaticDecompression = automaticDecompression);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetConnectTimeout(this ISocketsHttpHandlerBuilder builder, TimeSpan connectTimeout)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { ConnectTimeout = connectTimeout }))
            {
                return builder;
            }

            Configure(builder, handler => handler.ConnectTimeout = connectTimeout);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetPooledConnectionLifetime(this ISocketsHttpHandlerBuilder builder, TimeSpan pooledConnectionLifetime)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { PooledConnectionLifetime = pooledConnectionLifetime }))
            {
                return builder;
            }

            Configure(builder, handler => handler.PooledConnectionLifetime = pooledConnectionLifetime);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetPooledConnectionIdleTimeout(this ISocketsHttpHandlerBuilder builder, TimeSpan pooledConnectionIdleTimeout)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { PooledConnectionIdleTimeout = pooledConnectionIdleTimeout }))
            {
                return builder;
            }

            Configure(builder, handler => handler.PooledConnectionIdleTimeout = pooledConnectionIdleTimeout);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetKeepAlivePingDelay(this ISocketsHttpHandlerBuilder builder, TimeSpan keepAlivePingDelay)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { KeepAlivePingDelay = keepAlivePingDelay }))
            {
                return builder;
            }

            Configure(builder, handler => handler.KeepAlivePingDelay = keepAlivePingDelay);
            return builder;
        }

        [UnsupportedOSPlatform("browser")] // todo: what else do I need to set up PNSE?
        public static ISocketsHttpHandlerBuilder SetKeepAlivePingTimeout(this ISocketsHttpHandlerBuilder builder, TimeSpan keepAlivePingTimeout)
        {
            ThrowHelper.ThrowIfNull(builder);

            if (builder.TryUpdateOptions(o => o with { KeepAlivePingTimeout = keepAlivePingTimeout }))
            {
                return builder;
            }

            Configure(builder, handler => handler.KeepAlivePingTimeout = keepAlivePingTimeout);
            return builder;
        }
    }

    internal record struct SocketsHttpHandlerOptions
    {
        public bool Modified { get; init; }

        public bool? AllowAutoRedirect { get; init; }
        public DecompressionMethods? AutomaticDecompression { get; init; }
        public TimeSpan? ConnectTimeout { get; init; }
        public bool? EnableMultipleHttp2Connections { get; init; }
        public TimeSpan? Expect100ContinueTimeout { get; init; }
        public int? InitialHttp2StreamWindowSize { get; init; }
        public TimeSpan? KeepAlivePingDelay { get; init; }
        public HttpKeepAlivePingPolicy? KeepAlivePingPolicy { get; init; }
        public TimeSpan? KeepAlivePingTimeout { get; init; }
        public int? MaxAutomaticRedirections { get; init; }
        public int? MaxConnectionsPerServer { get; init; }
        public int? MaxResponseDrainSize { get; init; }
        public int? MaxResponseHeadersLength { get; init; }
        public TimeSpan? PooledConnectionIdleTimeout { get; init; }
        public TimeSpan? PooledConnectionLifetime { get; init; }
        public bool? PreAuthenticate { get; init; }
        public TimeSpan? ResponseDrainTimeout { get; init; }
        public bool? UseCookies { get; init; }
        public bool? UseProxy { get; init; }

        public void Apply(SocketsHttpHandler handler)
        {
            handler.AllowAutoRedirect = AllowAutoRedirect ?? handler.AllowAutoRedirect;
            handler.AutomaticDecompression = AutomaticDecompression ?? handler.AutomaticDecompression;
            handler.ConnectTimeout = ConnectTimeout ?? handler.ConnectTimeout;
            handler.EnableMultipleHttp2Connections = EnableMultipleHttp2Connections ?? handler.EnableMultipleHttp2Connections;
            handler.Expect100ContinueTimeout = Expect100ContinueTimeout ?? handler.Expect100ContinueTimeout;
            handler.InitialHttp2StreamWindowSize = InitialHttp2StreamWindowSize ?? handler.InitialHttp2StreamWindowSize;
            handler.KeepAlivePingDelay = KeepAlivePingDelay ?? handler.KeepAlivePingDelay;
            handler.KeepAlivePingPolicy = KeepAlivePingPolicy ?? handler.KeepAlivePingPolicy;
            handler.KeepAlivePingTimeout = KeepAlivePingTimeout ?? handler.KeepAlivePingTimeout;
            handler.MaxAutomaticRedirections = MaxAutomaticRedirections ?? handler.MaxAutomaticRedirections;
            handler.MaxConnectionsPerServer = MaxConnectionsPerServer ?? handler.MaxConnectionsPerServer;
            handler.MaxResponseDrainSize = MaxResponseDrainSize ?? handler.MaxResponseDrainSize;
            handler.MaxResponseHeadersLength = MaxResponseHeadersLength ?? handler.MaxResponseHeadersLength;
            handler.PooledConnectionIdleTimeout = PooledConnectionIdleTimeout ?? handler.PooledConnectionIdleTimeout;
            handler.PooledConnectionLifetime = PooledConnectionLifetime ?? handler.PooledConnectionLifetime;
            handler.PreAuthenticate = PreAuthenticate ?? handler.PreAuthenticate;
            handler.ResponseDrainTimeout = ResponseDrainTimeout ?? handler.ResponseDrainTimeout;
            handler.UseCookies = UseCookies ?? handler.UseCookies;
            handler.UseProxy = UseProxy ?? handler.UseProxy;
        }
    }
}

#endif
