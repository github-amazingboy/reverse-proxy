// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Net.Http.Headers;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.Abstractions.Config;
using Microsoft.ReverseProxy.Service.Proxy;
using Microsoft.ReverseProxy.Service.RuntimeModel.Transforms;
using Microsoft.ReverseProxy.Utilities;

namespace Microsoft.ReverseProxy.Service.Config
{
    /// <summary>
    /// Validates and builds request and response transforms for a given route.
    /// </summary>
    internal class TransformBuilder : ITransformBuilder
    {
        private readonly IServiceProvider _services;
        private readonly IRandomFactory _randomFactory;
        private readonly List<ITransformFactory> _factories;
        private readonly List<ITransformFilter> _filters;

        /// <summary>
        /// Creates a new <see cref="TransformBuilder"/>
        /// </summary>
        public TransformBuilder(IServiceProvider services, IRandomFactory randomFactory, IEnumerable<ITransformFactory> factories, IEnumerable<ITransformFilter> filters)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _randomFactory = randomFactory ?? throw new ArgumentNullException(nameof(randomFactory));
            _factories = factories?.ToList() ?? throw new ArgumentNullException(nameof(factories));
            _filters = filters?.ToList() ?? throw new ArgumentNullException(nameof(filters));
        }

        /// <inheritdoc/>
        public IList<Exception> Validate(ProxyRoute route)
        {
            var errors = new List<Exception>();
            var rawTransforms = route?.Transforms;

            if (rawTransforms == null || rawTransforms.Count == 0)
            {
                return errors;
            }

            var context = new TransformValidationContext()
            {
                Services = _services,
                Route = route,
                Errors = errors,
            };

            foreach (var rawTransform in rawTransforms)
            {
                var handled = false;
                foreach (var factory in _factories)
                {
                    if (factory.Validate(context, rawTransform))
                    {
                        handled = true;
                        break;
                    }
                }

                if (!handled)
                {
                    throw new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}");
                }
            }

            foreach (var rawTransform in rawTransforms)
            {
                if (rawTransform.TryGetValue("RequestHeadersCopy", out var copyHeaders))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                    if (!string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", copyHeaders, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ArgumentException($"Unexpected value for RequestHeaderCopy: {copyHeaders}. Expected 'true' or 'false'"));
                    }
                }
                else if (rawTransform.TryGetValue("RequestHeaderOriginalHost", out var originalHost))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                    if (!string.Equals("True", originalHost, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", originalHost, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add(new ArgumentException($"Unexpected value for RequestHeaderOriginalHost: {originalHost}. Expected 'true' or 'false'"));
                    }
                }
                else if (rawTransform.TryGetValue("RequestHeader", out var headerName))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for RequestHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("ResponseHeader", out var _))
                {
                    if (rawTransform.TryGetValue("When", out var whenValue))
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 3);
                        if (!string.Equals("Always", whenValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("Success", whenValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for ResponseHeader:When: {whenValue}. Expected 'Always' or 'Success'"));
                        }
                    }
                    else
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    }

                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for ResponseHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("ResponseTrailer", out var _))
                {
                    if (rawTransform.TryGetValue("When", out var whenValue))
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 3);
                        if (!string.Equals("Always", whenValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("Success", whenValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for ResponseTrailer:When: {whenValue}. Expected 'Always' or 'Success'"));
                        }
                    }
                    else
                    {
                        TryCheckTooManyParameters(errors.Add, rawTransform, expected: 2);
                    }

                    if (!rawTransform.TryGetValue("Set", out var _) && !rawTransform.TryGetValue("Append", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for ResponseTrailer: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'"));
                    }
                }
                else if (rawTransform.TryGetValue("X-Forwarded", out var xforwardedHeaders))
                {
                    var expected = 1;

                    if (rawTransform.TryGetValue("Append", out var appendValue))
                    {
                        expected++;
                        if (!string.Equals("True", appendValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", appendValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded:Append: {appendValue}. Expected 'true' or 'false'"));
                        }
                    }

                    if (rawTransform.TryGetValue("Prefix", out var _))
                    {
                        expected++;
                    }

                    TryCheckTooManyParameters(errors.Add, rawTransform, expected);

                    // for, host, proto, PathBase
                    var tokens = xforwardedHeaders.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var token in tokens)
                    {
                        if (!string.Equals(token, "For", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "PathBase", StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'PathBase'"));
                        }
                    }
                }
                else if (rawTransform.TryGetValue("Forwarded", out var forwardedHeader))
                {
                    var expected = 1;

                    if (rawTransform.TryGetValue("Append", out var appendValue))
                    {
                        expected++;
                        if (!string.Equals("True", appendValue, StringComparison.OrdinalIgnoreCase) && !string.Equals("False", appendValue, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:Append: {appendValue}. Expected 'true' or 'false'"));
                        }
                    }

                    var enumValues = "Random,RandomAndPort,Unknown,UnknownAndPort,Ip,IpAndPort";
                    if (rawTransform.TryGetValue("ForFormat", out var forFormat))
                    {
                        expected++;
                        if (!Enum.TryParse<RequestHeaderForwardedTransform.NodeFormat>(forFormat, ignoreCase: true, out var _))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:ForFormat: {forFormat}. Expected: {enumValues}"));
                        }
                    }

                    if (rawTransform.TryGetValue("ByFormat", out var byFormat))
                    {
                        expected++;
                        if (!Enum.TryParse<RequestHeaderForwardedTransform.NodeFormat>(byFormat, ignoreCase: true, out var _))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for Forwarded:ByFormat: {byFormat}. Expected: {enumValues}"));
                        }
                    }

                    TryCheckTooManyParameters(errors.Add, rawTransform, expected);

                    // for, host, proto, by
                    var tokens = forwardedHeader.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var token in tokens)
                    {
                        if (!string.Equals(token, "By", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add(new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'by'"));
                        }
                    }
                }
                else if (rawTransform.TryGetValue("ClientCert", out var clientCertHeader))
                {
                    TryCheckTooManyParameters(errors.Add, rawTransform, expected: 1);
                }
                else if (rawTransform.TryGetValue("HttpMethod", out var fromHttpMethod))
                {
                    CheckTooManyParameters(rawTransform, expected: 2);
                    if (!rawTransform.TryGetValue("Set", out var _))
                    {
                        errors.Add(new ArgumentException($"Unexpected parameters for HttpMethod: {string.Join(';', rawTransform.Keys)}. Expected 'Set'"));
                    }
                }
                else
                {
                    errors.Add(new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}"));
                }
            }

            return errors;
        }

        /// <inheritdoc/>
        public HttpTransformer Build(ProxyRoute route)
        {
            return BuildInternal(route);
        }

        // This is separate from Build for testing purposes.
        internal StructuredTransformer BuildInternal(ProxyRoute route)
        {
            var rawTransforms = route.Transforms;

            bool? copyRequestHeaders = null;
            bool? copyResponseHeaders = null;
            bool? copyResponseTrailers = null;
            bool? useOriginalHost = null;
            bool? forwardersSet = null;
            var requestTransforms = new List<RequestTransform>();
            var responseTransforms = new List<ResponseTransform>();
            var responseTrailersTransforms = new List<ResponseTrailersTransform>();

            if (rawTransforms?.Count > 0)
            {
                foreach (var rawTransform in rawTransforms)
                {
                    if (rawTransform.TryGetValue("RequestHeadersCopy", out var copyHeaders))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        copyRequestHeaders = string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("ResponseHeadersCopy", out copyHeaders))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        copyResponseHeaders = string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("ResponseTrailersCopy", out copyHeaders))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        copyResponseTrailers = string.Equals("True", copyHeaders, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("RequestHeaderOriginalHost", out var originalHost))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        useOriginalHost = string.Equals("True", originalHost, StringComparison.OrdinalIgnoreCase);
                    }
                    else if (rawTransform.TryGetValue("RequestHeader", out var headerName))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            requestTransforms.Add(new RequestHeaderValueTransform(headerName, setValue, append: false));
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            requestTransforms.Add(new RequestHeaderValueTransform(headerName, appendValue, append: true));
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for RequestHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("ResponseHeader", out var responseHeaderName))
                    {
                        var always = false;
                        if (rawTransform.TryGetValue("When", out var whenValue))
                        {
                            CheckTooManyParameters(rawTransform, expected: 3);
                            always = string.Equals("always", whenValue, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            CheckTooManyParameters(rawTransform, expected: 2);
                        }

                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            responseTransforms.Add(new ResponseHeaderValueTransform(responseHeaderName, setValue, append: false, always));
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            responseTransforms.Add(new ResponseHeaderValueTransform(responseHeaderName, appendValue, append: true, always));
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for ResponseHeader: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("ResponseTrailer", out var responseTrailerName))
                    {
                        var always = false;
                        if (rawTransform.TryGetValue("When", out var whenValue))
                        {
                            CheckTooManyParameters(rawTransform, expected: 3);
                            always = string.Equals("always", whenValue, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            CheckTooManyParameters(rawTransform, expected: 2);
                        }

                        if (rawTransform.TryGetValue("Set", out var setValue))
                        {
                            responseTrailersTransforms.Add(new ResponseTrailerValueTransform(responseTrailerName, setValue, append: false, always));
                        }
                        else if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            responseTrailersTransforms.Add(new ResponseTrailerValueTransform(responseTrailerName, appendValue, append: true, always));
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected parameters for ResponseTrailer: {string.Join(';', rawTransform.Keys)}. Expected 'Set' or 'Append'");
                        }
                    }
                    else if (rawTransform.TryGetValue("X-Forwarded", out var xforwardedHeaders))
                    {
                        forwardersSet = true;
                        var expected = 1;

                        var append = true;
                        if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            expected++;
                            append = string.Equals("true", appendValue, StringComparison.OrdinalIgnoreCase);
                        }

                        var prefix = "X-Forwarded-";
                        if (rawTransform.TryGetValue("Prefix", out var prefixValue))
                        {
                            expected++;
                            prefix = prefixValue;
                        }

                        CheckTooManyParameters(rawTransform, expected);

                        // for, host, proto, PathBase
                        var tokens = xforwardedHeaders.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var token in tokens)
                        {
                            if (string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                            {
                                requestTransforms.Add(new RequestHeaderXForwardedForTransform(prefix + "For", append));
                            }
                            else if (string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                requestTransforms.Add(new RequestHeaderXForwardedHostTransform(prefix + "Host", append));
                            }
                            else if (string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase))
                            {
                                requestTransforms.Add(new RequestHeaderXForwardedProtoTransform(prefix + "Proto", append));
                            }
                            else if (string.Equals(token, "PathBase", StringComparison.OrdinalIgnoreCase))
                            {
                                requestTransforms.Add(new RequestHeaderXForwardedPathBaseTransform(prefix + "PathBase", append));
                            }
                            else
                            {
                                throw new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'PathBase'");
                            }
                        }
                    }
                    else if (rawTransform.TryGetValue("Forwarded", out var forwardedHeader))
                    {
                        forwardersSet = true;

                        var useHost = false;
                        var useProto = false;
                        var useFor = false;
                        var useBy = false;
                        var forFormat = RequestHeaderForwardedTransform.NodeFormat.None;
                        var byFormat = RequestHeaderForwardedTransform.NodeFormat.None;

                        // for, host, proto, PathBase
                        var tokens = forwardedHeader.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (var token in tokens)
                        {
                            if (string.Equals(token, "For", StringComparison.OrdinalIgnoreCase))
                            {
                                useFor = true;
                                forFormat = RequestHeaderForwardedTransform.NodeFormat.Random; // RFC Default
                            }
                            else if (string.Equals(token, "By", StringComparison.OrdinalIgnoreCase))
                            {
                                useBy = true;
                                byFormat = RequestHeaderForwardedTransform.NodeFormat.Random; // RFC Default
                            }
                            else if (string.Equals(token, "Host", StringComparison.OrdinalIgnoreCase))
                            {
                                useHost = true;
                            }
                            else if (string.Equals(token, "Proto", StringComparison.OrdinalIgnoreCase))
                            {
                                useProto = true;
                            }
                            else
                            {
                                throw new ArgumentException($"Unexpected value for X-Forwarded: {token}. Expected 'for', 'host', 'proto', or 'by'");
                            }
                        }

                        var expected = 1;

                        var append = true;
                        if (rawTransform.TryGetValue("Append", out var appendValue))
                        {
                            expected++;
                            append = string.Equals("true", appendValue, StringComparison.OrdinalIgnoreCase);
                        }

                        if (useFor && rawTransform.TryGetValue("ForFormat", out var forFormatString))
                        {
                            expected++;
                            forFormat = Enum.Parse<RequestHeaderForwardedTransform.NodeFormat>(forFormatString, ignoreCase: true);
                        }

                        if (useBy && rawTransform.TryGetValue("ByFormat", out var byFormatString))
                        {
                            expected++;
                            byFormat = Enum.Parse<RequestHeaderForwardedTransform.NodeFormat>(byFormatString, ignoreCase: true);
                        }

                        CheckTooManyParameters(rawTransform, expected);

                        if (useBy || useFor || useHost || useProto)
                        {
                            requestTransforms.Add(new RequestHeaderForwardedTransform(_randomFactory, forFormat, byFormat, useHost, useProto, append));
                        }
                    }
                    else if (rawTransform.TryGetValue("ClientCert", out var clientCertHeader))
                    {
                        CheckTooManyParameters(rawTransform, expected: 1);
                        requestTransforms.Add(new RequestHeaderClientCertTransform(clientCertHeader));
                    }
                    else if (rawTransform.TryGetValue("HttpMethod", out var fromHttpMethod))
                    {
                        CheckTooManyParameters(rawTransform, expected: 2);
                        if (rawTransform.TryGetValue("Set", out var toHttpMethod))
                        {
                            requestTransforms.Add(new HttpMethodTransform(fromHttpMethod, toHttpMethod));
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}");
                    }
                }
            }

            var transformBuilderContext = new TransformBuilderContext
            {
                Services = _services,
                Route = route,
                CopyRequestHeaders = copyRequestHeaders,
                CopyResponseHeaders = copyResponseHeaders,
                CopyResponseTrailers = copyResponseTrailers,
                UseOriginalHost = useOriginalHost,
                UseDefaultForwarders = !forwardersSet,
                RequestTransforms = requestTransforms,
                ResponseTransforms = responseTransforms,
                ResponseTrailersTransforms = responseTrailersTransforms,
            };

            if (rawTransforms?.Count > 0)
            {
                foreach (var rawTransform in rawTransforms)
                {
                    var handled = false;
                    foreach (var factory in _factories)
                    {
                        if (factory.Build(transformBuilderContext, rawTransform))
                        {
                            handled = true;
                            break;
                        }
                    }

                    if (!handled)
                    {
                        throw new ArgumentException($"Unknown transform: {string.Join(';', rawTransform.Keys)}");
                    }
                }
            }

            // Let the app add any more transforms it wants.
            foreach (var transformFitler in _filters)
            {
                transformFitler.Apply(transformBuilderContext);
            }

            // Suppress the host by default
            if (transformBuilderContext.UseOriginalHost ?? true)
            {
                requestTransforms.Add(new RequestHeaderValueTransform(HeaderNames.Host, string.Empty, append: false));
            }

            // Add default forwarders
            if (transformBuilderContext.UseDefaultForwarders.GetValueOrDefault(true))
            {
                requestTransforms.Add(new RequestHeaderXForwardedProtoTransform(ForwardedHeadersDefaults.XForwardedProtoHeaderName, append: true));
                requestTransforms.Add(new RequestHeaderXForwardedHostTransform(ForwardedHeadersDefaults.XForwardedHostHeaderName, append: true));
                requestTransforms.Add(new RequestHeaderXForwardedForTransform(ForwardedHeadersDefaults.XForwardedForHeaderName, append: true));
                requestTransforms.Add(new RequestHeaderXForwardedPathBaseTransform("X-Forwarded-PathBase", append: true));
            }

            return new StructuredTransformer(
                transformBuilderContext.CopyRequestHeaders,
                transformBuilderContext.CopyResponseHeaders,
                transformBuilderContext.CopyResponseTrailers,
                requestTransforms,
                responseTransforms,
                responseTrailersTransforms);
        }

        private void TryCheckTooManyParameters(Action<Exception> onError, IReadOnlyDictionary<string, string> rawTransform, int expected)
        {
            if (rawTransform.Count > expected)
            {
                onError(new InvalidOperationException("The transform contains more parameters than expected: " + string.Join(';', rawTransform.Keys)));
            }
        }

        private void CheckTooManyParameters(IReadOnlyDictionary<string, string> rawTransform, int expected)
        {
            TryCheckTooManyParameters(ex => throw ex, rawTransform, expected);
        }
    }
}
