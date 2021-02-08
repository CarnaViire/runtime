// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Extensions.DependencyInjection
{
    internal class ServiceProviderChecker // registered as singleton
    {
        private readonly IServiceProvider _rootServiceProvider;

        public IServiceProvider RootServiceProvider => _rootServiceProvider;

        public ServiceProviderChecker(IServiceProvider rootServiceProvider)
        {
            _rootServiceProvider = rootServiceProvider;
        }

        public bool IsScoped(IServiceProvider serviceProvider)
        {
            return serviceProvider != _rootServiceProvider;
        }
    }
}
