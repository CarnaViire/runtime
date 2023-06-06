// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Http;

namespace Microsoft.Extensions.DependencyInjection
{
    internal sealed class DefaultHttpClientBuilder : IHttpClientBuilder
    {
        public DefaultHttpClientBuilder(IServiceCollection services, string name, bool isAllClientDefaults = false)
        {
            Services = services;
            Name = name;
            IsAllClientDefaults = isAllClientDefaults;

            if (isAllClientDefaults)
            {
                services.Configure<HttpClientFactoryOptions>(name, options =>
                {
                    if (options._isAllClientDefaults is null) // first-time config
                    {
                        options._isAllClientDefaults = true;
                        options._handlerLifetime = HttpClientFactoryOptions.DefaultHandlerLifetime;
                    }
                });
            }
            else
            {
                services.Configure<HttpClientFactoryOptions>(name, options => options._isAllClientDefaults = false);
            }
        }

        public string Name { get; }

        public IServiceCollection Services { get; }

        internal bool IsAllClientDefaults { get; }
    }
}
