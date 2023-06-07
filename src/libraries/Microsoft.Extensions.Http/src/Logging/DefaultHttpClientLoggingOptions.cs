// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Http.Logging
{
    /// <summary>
    /// Options for configuring logging for <see cref="HttpClient"/> instances returned by <see cref="IHttpClientFactory"/>.
    /// </summary>
    internal class DefaultHttpClientLoggingOptions : IHttpClientLoggingOptions
    {
        private List<Action<IAdditionalHandlersBuilder>> _additionalHandlersActions = new List<Action<IAdditionalHandlersBuilder>>();
        internal IReadOnlyList<Action<IAdditionalHandlersBuilder>> AdditionalHandlersActions => (IReadOnlyList<Action<IAdditionalHandlersBuilder>>)_additionalHandlersActions;
        private bool _defaultOuterLogHandlerAdded;
        private bool _defaultInnerLogHandlerAdded;

        public DefaultHttpClientLoggingOptions()
        {
            AddDefaultProviders();
        }

        // removes all logging
        public IHttpClientLoggingOptions ClearProviders()
        {
            _additionalHandlersActions.Clear();
            _defaultOuterLogHandlerAdded = false;
            _defaultInnerLogHandlerAdded = false;
            return this;
        }

        // adds the default logging ("inner" LoggingHttpMessageHandler + "outer" LoggingScopeHttpMessageHandler)
        public IHttpClientLoggingOptions AddDefaultProviders()
        {
            if (!_defaultOuterLogHandlerAdded)
            {
                _additionalHandlersActions.Add(AddDefaultOuterLogHandler);
                _defaultOuterLogHandlerAdded = true;
            }

            if (!_defaultInnerLogHandlerAdded)
            {
                _additionalHandlersActions.Add(AddDefaultInnerLogHandler);
                _defaultInnerLogHandlerAdded = true;
            }

            return this;
        }

        public IHttpClientLoggingOptions AddRequestStartProvider(Func<HttpRequestMessage, string> getRequestStartMessage, LogLevel level = LogLevel.Information)
        {
            _additionalHandlersActions.Add(b => AddCustomLogHandler(b, getRequestStartMessage, getRequestEndMessage: null, level, isOuter: false));
            return this;
        }

        public IHttpClientLoggingOptions AddRequestEndProvider(Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string> getRequestEndMessage, LogLevel level = LogLevel.Information)
        {
            _additionalHandlersActions.Add(b => AddCustomLogHandler(b, getRequestStartMessage: null, getRequestEndMessage, level, isOuter: true));
            return this;
        }

        private static void AddDefaultOuterLogHandler(IAdditionalHandlersBuilder builder)
        {
            ILogger outerLogger = GetLogger(builder, "LogicalHandler");
            HttpClientFactoryOptions options = GetHttpClientFactoryOptions(builder);

            // The 'scope' handler goes first so it can surround everything.
            builder.AdditionalHandlers.Insert(0, new LoggingScopeHttpMessageHandler(outerLogger, options));
        }

        private static void AddDefaultInnerLogHandler(IAdditionalHandlersBuilder builder)
        {
            ILogger innerLogger = GetLogger(builder, "ClientHandler");
            HttpClientFactoryOptions options = GetHttpClientFactoryOptions(builder);

            // We want this handler to be last so we can log details about the request after
            // service discovery and security happen.
            builder.AdditionalHandlers.Add(new LoggingHttpMessageHandler(innerLogger, options));
        }

        private static void AddCustomLogHandler(IAdditionalHandlersBuilder builder, Func<HttpRequestMessage, string>? getRequestStartMessage, Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string>? getRequestEndMessage, LogLevel level, bool isOuter)
        {
            ILogger logger = GetLogger(builder, "LoggingHandler");

            var loggingHandler = new CustomLoggingHttpMessageHandler(logger, getRequestStartMessage, getRequestEndMessage, level);

            if (isOuter)
            {
                builder.AdditionalHandlers.Insert(0, loggingHandler);
            }
            else
            {
                builder.AdditionalHandlers.Add(loggingHandler);
            }
        }

        private static ILogger GetLogger(IAdditionalHandlersBuilder builder, string handlerName)
        {
            ILoggerFactory loggerFactory = builder.Services.GetRequiredService<ILoggerFactory>();
            string loggerName = !string.IsNullOrEmpty(builder.Name) ? builder.Name : "Default";
            // We want all of our logging message to show up as-if they are coming from HttpClient,
            // but also to include the name of the client for more fine-grained control.
            return loggerFactory.CreateLogger($"System.Net.Http.HttpClient.{loggerName}.{handlerName}");
        }

        private static HttpClientFactoryOptions GetHttpClientFactoryOptions(IAdditionalHandlersBuilder builder)
        {
            IOptionsMonitor<HttpClientFactoryOptions> optionsMonitor = builder.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
            return optionsMonitor.Get(builder.Name);
        }
    }
}
