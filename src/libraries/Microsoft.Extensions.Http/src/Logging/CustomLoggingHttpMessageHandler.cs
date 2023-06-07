// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Http.Logging
{
    /// <summary>
    /// Handles custom logging of the lifecycle for an HTTP request.
    /// </summary>
    internal class CustomLoggingHttpMessageHandler : DelegatingHandler
    {
        private readonly ILogger _logger;
        private readonly Func<HttpRequestMessage, string>? _getRequestStartMessage;
        private readonly Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string>? _getRequestEndMessage;
        private readonly LogLevel _level;

        public CustomLoggingHttpMessageHandler(ILogger logger, Func<HttpRequestMessage, string>? getRequestStartMessage, Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string>? getRequestEndMessage, LogLevel level)
        {
            ThrowHelper.ThrowIfNull(logger);

            if (getRequestStartMessage == null && getRequestEndMessage == null)
            {
                throw new ArgumentException($"Either {nameof(getRequestStartMessage)} or {nameof(getRequestEndMessage)} should be supplied");
            }

            _logger = logger;
            _getRequestStartMessage = getRequestStartMessage;
            _getRequestEndMessage = getRequestEndMessage;
            _level = level;
        }

        private Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, bool useAsync, CancellationToken cancellationToken)
        {
            ThrowHelper.ThrowIfNull(request);
            return Core(request, cancellationToken);

            async Task<HttpResponseMessage> Core(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_getRequestStartMessage != null)
                {
                    Log.RequestStart(_logger, _level, _getRequestStartMessage, request);
                }

                HttpResponseMessage? response = null;
                Exception? exception = null;

                ValueStopwatch stopwatch = default;
                if (_getRequestEndMessage != null)
                {
                    stopwatch = ValueStopwatch.StartNew();
                }

                try
                {
                    response = useAsync
                        ? await base.SendAsync(request, cancellationToken).ConfigureAwait(false)
#if NET5_0_OR_GREATER
                        : base.Send(request, cancellationToken);
#else
                        : throw new NotImplementedException("Unreachable code");
#endif
                    return response;
                }
                catch (Exception e)
                {
                    exception = e;
                    throw;
                }
                finally
                {
                    if (_getRequestEndMessage != null)
                    {
                        Log.RequestEnd(_logger, _level, _getRequestEndMessage, request, stopwatch.GetElapsedTime(), response, exception);
                    }
                }
            }
        }

        /// <inheritdoc />
        /// <remarks>Logs the request to and response from the sent <see cref="HttpRequestMessage"/>.</remarks>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendCoreAsync(request, useAsync: true, cancellationToken);

#if NET5_0_OR_GREATER
        /// <inheritdoc />
        /// <remarks>Logs the request to and response from the sent <see cref="HttpRequestMessage"/>.</remarks>
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendCoreAsync(request, useAsync: false, cancellationToken).GetAwaiter().GetResult();
#endif

        // Used in tests.
        internal static class Log
        {
            public static class EventIds
            {
                public static readonly EventId RequestStart = new EventId(100, "RequestStart");
                public static readonly EventId RequestEnd = new EventId(101, "RequestEnd");
            }

            public static void RequestStart(ILogger logger, LogLevel level, Func<HttpRequestMessage, string> getRequestStartMessage, HttpRequestMessage request)
            {
                if (logger.IsEnabled(level))
                {
                    logger.Log(
                        level,
                        EventIds.RequestStart,
                        request,
                        null,
                        (state, ex) => getRequestStartMessage(state));
                }
            }

            public static void RequestEnd(ILogger logger, LogLevel level, Func<HttpRequestMessage, TimeSpan, HttpResponseMessage?, Exception?, string> getRequestEndMessage, HttpRequestMessage request, TimeSpan duration, HttpResponseMessage? response = null, Exception? exception = null)
            {
                if (response == null && exception == null)
                {
                    throw new ArgumentException($"Either {nameof(response)} or {nameof(exception)} should be supplied");
                }

                if (logger.IsEnabled(level))
                {
                    logger.Log(
                        level,
                        EventIds.RequestEnd,
                        new RequestEndState(request, duration, response),
                        exception,
                        (state, ex) => getRequestEndMessage(state.Request, state.Duration, state.Response, ex));
                }
            }
        }

        private record struct RequestEndState(HttpRequestMessage Request, TimeSpan Duration, HttpResponseMessage? Response);
    }
}
