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
        private void SetUpPrimaryHandlersCache(){}
        internal static void ConfigurePrimaryHandler(HttpMessageHandlerBuilder builder, HttpClientFactoryOptions options)
        {
            if (options.PrimaryHandlerFactory != null)
            {
                builder.PrimaryHandler = options.PrimaryHandlerFactory();
            }
        }
    }
}
