// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;

namespace Microsoft.Extensions.Http
{
    internal sealed class DefaultHttpMessageHandlerBuilder : HttpMessageHandlerBuilder
    {
        public DefaultHttpMessageHandlerBuilder(IServiceProvider services)
        {
            Services = services;
        }

        private string? _name;

        [DisallowNull]
        public override string? Name
        {
            get => _name;
            set
            {
                ThrowHelper.ThrowIfNull(value);
                _name = value;
            }
        }

        private HttpMessageHandler? _primaryHandler;
        private bool _primaryHandlerSet;
        public override HttpMessageHandler PrimaryHandler
        {
            get
            {
                if (_primaryHandler is null && !_primaryHandlerSet)
                {
                    _primaryHandler = new HttpClientHandler(); // backward compatibility
                }
                return _primaryHandler!; // this can be null if user explicitly set null
            }
            set
            {
                _primaryHandlerSet = true;
                _primaryHandler = value;
            }
        }

        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();

        public override IServiceProvider Services { get; }

        public override HttpMessageHandler Build()
        {
            if (PrimaryHandler == null)
            {
                string message = SR.Format(SR.HttpMessageHandlerBuilder_PrimaryHandlerIsNull, nameof(PrimaryHandler));
                throw new InvalidOperationException(message);
            }

            return CreateHandlerPipeline(PrimaryHandler, AdditionalHandlers);
        }
    }
}
