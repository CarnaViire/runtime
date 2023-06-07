// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Http
{
    /// <summary>
    /// Options for configuring logging for <see cref="HttpClient"/> instances returned by <see cref="IHttpClientFactory"/>.
    /// </summary>
    public interface IHttpClientLoggingOptions
    {
        IHttpClientLoggingOptions ClearProviders(); // removes all logging
        IHttpClientLoggingOptions AddDefaultProviders(); // adds the default logging (LoggingHttpMessageHandler + LoggingScopeHttpMessageHandler)
        IHttpClientLoggingOptions AddRequestStartProvider(Func<HttpRequestMessage, string> getRequestStartMessage, LogLevel level = LogLevel.Information);
        IHttpClientLoggingOptions AddRequestEndProvider(Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string> getRequestEndMessage, LogLevel level = LogLevel.Information);
    }
}
