// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http;
using Microsoft.Extensions.Http;

namespace Microsoft.Extensions.DependencyInjection
{
    internal sealed class DefaultHttpClientBuilder : IHttpClientBuilder
    {
        public DefaultHttpClientBuilder(IServiceCollection services, string name)
        {
            Services = services;
            Name = name;

            if (name is null && HttpClientFactoryOptions.Default is null) // default instance first-time config
            {
                HttpClientFactoryOptions.InitDefault(d =>
                {
                    d._handlerLifetime = HttpClientFactoryOptions.DefaultHandlerLifetime;
                    d.AddPrimaryHandlerAction(b =>
                    {
                        b.PrimaryHandler = HttpClientFactoryOptions.NewDefaultPrimaryHandler();

#if NET5_0_OR_GREATER
                        if (SocketsHttpHandler.IsSupported && b.PrimaryHandler is SocketsHttpHandler socketsHttpHandler)
                        {
                            socketsHttpHandler.UseCookies = false;
                            socketsHttpHandler.PooledConnectionLifetime = HttpClientFactoryOptions.DefaultHandlerLifetime;
                        }
#endif
                    },
                    disregardPreviousActions: true);
                });
            }
        }

        public string Name { get; }

        public IServiceCollection Services { get; }
    }
}
