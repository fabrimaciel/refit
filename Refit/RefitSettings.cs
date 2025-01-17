﻿using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Refit
{
    public class RefitSettings
    {       

        /// <summary>
        /// Creates a new <see cref="RefitSettings"/> instance with the default parameters
        /// </summary>
        public RefitSettings()
        {
            ContentSerializer = new SystemTextJsonContentSerializer();
            UrlParameterFormatter = new DefaultUrlParameterFormatter();
            FormUrlEncodedParameterFormatter = new DefaultFormUrlEncodedParameterFormatter();
            ExceptionFactory = new DefaultApiExceptionFactory(this).CreateAsync;
        }

        /// <summary>
        /// Creates a new <see cref="RefitSettings"/> instance with the specified parameters
        /// </summary>
        /// <param name="contentSerializer">The <see cref="IHttpContentSerializer"/> instance to use</param>
        /// <param name="urlParameterFormatter">The <see cref="IUrlParameterFormatter"/> instance to use (defaults to <see cref="DefaultUrlParameterFormatter"/>)</param>
        /// <param name="formUrlEncodedParameterFormatter">The <see cref="IFormUrlEncodedParameterFormatter"/> instance to use (defaults to <see cref="DefaultFormUrlEncodedParameterFormatter"/>)</param>
        public RefitSettings(
            IHttpContentSerializer contentSerializer,
            IUrlParameterFormatter? urlParameterFormatter = null,
            IFormUrlEncodedParameterFormatter? formUrlEncodedParameterFormatter = null)
        {
            ContentSerializer = contentSerializer ?? throw new ArgumentNullException(nameof(contentSerializer), "The content serializer can't be null");
            UrlParameterFormatter = urlParameterFormatter ?? new DefaultUrlParameterFormatter();
            FormUrlEncodedParameterFormatter = formUrlEncodedParameterFormatter ?? new DefaultFormUrlEncodedParameterFormatter();
            ExceptionFactory = new DefaultApiExceptionFactory(this).CreateAsync;
        }

        /// <summary>
        /// Supply a function to provide the Authorization header. Does not work if you supply an HttpClient instance.
        /// </summary>
        public Func<Task<string>>? AuthorizationHeaderValueGetter { get; set; }

        /// <summary>
        /// Supply a function to provide the Authorization header. Does not work if you supply an HttpClient instance.
        /// </summary>
        public Func<HttpRequestMessage, Task<string>>? AuthorizationHeaderValueWithParamGetter { get; set; }

        /// <summary>
        /// Supply a custom inner HttpMessageHandler. Does not work if you supply an HttpClient instance.
        /// </summary>
        public Func<HttpMessageHandler>? HttpMessageHandlerFactory { get; set; }

        /// <summary>
        /// Supply a function to provide <see cref="Exception"/> based on <see cref="HttpResponseMessage"/>.
        /// If function returns null - no exception is thrown.
        /// </summary>
        public Func<HttpResponseMessage, Task<Exception?>> ExceptionFactory { get; set; }

        public IHttpContentSerializer ContentSerializer { get; set; }
        public IUrlParameterFormatter UrlParameterFormatter { get; set; }
        public IFormUrlEncodedParameterFormatter FormUrlEncodedParameterFormatter { get; set; }
        public CollectionFormat CollectionFormat { get; set; } = CollectionFormat.RefitParameterFormatter;
        public bool Buffered { get; set; } = false;
    }

    public interface IHttpContentSerializer
    {
        HttpContent ToHttpContent<T>(T item);

        Task<T?> FromHttpContentAsync<T>(HttpContent content, CancellationToken cancellationToken = default);

        /// <summary>
        /// Calculates what the field name should be for the given property. This may be affected by custom attributes the serializer understands
        /// </summary>
        /// <param name="propertyInfo"></param>
        /// <returns></returns>
        string? GetFieldNameForProperty(PropertyInfo propertyInfo);
    }

    public interface IHttpResponseMessageSerializer : IHttpContentSerializer
    {
        Task<T?> FromHttpContentAsync<T>(HttpResponseMessage response, HttpContent content, CancellationToken cancellationToken = default);
    }

    public interface IUrlParameterFormatter
    {
        string? Format(object? value, ICustomAttributeProvider attributeProvider, Type type);
    }

    public interface IFormUrlEncodedParameterFormatter
    {
        string? Format(object? value, string? formatString);
    }

    public class DefaultUrlParameterFormatter : IUrlParameterFormatter
    {
        static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, EnumMemberAttribute?>> EnumMemberCache = new();

        public virtual string? Format(object? parameterValue, ICustomAttributeProvider attributeProvider, Type type)
        {
            if (attributeProvider is null)
                throw new ArgumentNullException(nameof(attributeProvider));

            // See if we have a format
            var formatString = attributeProvider.GetCustomAttributes(typeof(QueryAttribute), true)
                .OfType<QueryAttribute>()
                .FirstOrDefault()?.Format;

            EnumMemberAttribute? enummember = null;
            if (parameterValue != null)
            {
                var parameterType = parameterValue.GetType();
                if (parameterType.IsEnum)
                {
                    var cached = EnumMemberCache.GetOrAdd(parameterType, t => new ConcurrentDictionary<string, EnumMemberAttribute?>());
                    enummember = cached.GetOrAdd(parameterValue.ToString()!, val => parameterType.GetMember(val).First().GetCustomAttribute<EnumMemberAttribute>());
                }
            }

            return parameterValue == null
                       ? null
                       : string.Format(CultureInfo.InvariantCulture,
                                       string.IsNullOrWhiteSpace(formatString)
                                           ? "{0}"
                                           : $"{{0:{formatString}}}",
                                       enummember?.Value ?? parameterValue);
        }
    }

    public class DefaultFormUrlEncodedParameterFormatter : IFormUrlEncodedParameterFormatter
    {
        static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, EnumMemberAttribute?>> EnumMemberCache
            = new();

        public virtual string? Format(object? parameterValue, string? formatString)
        {
            if (parameterValue == null)
                return null;

            var parameterType = parameterValue.GetType();

            EnumMemberAttribute? enummember = null;
            if (parameterType.GetTypeInfo().IsEnum)
            {
                var cached = EnumMemberCache.GetOrAdd(parameterType, t => new ConcurrentDictionary<string, EnumMemberAttribute?>());
                enummember = cached.GetOrAdd(parameterValue.ToString()!, val => parameterType.GetMember(val).First().GetCustomAttribute<EnumMemberAttribute>());
            }

            return string.Format(CultureInfo.InvariantCulture,
                                 string.IsNullOrWhiteSpace(formatString)
                                     ? "{0}"
                                     : $"{{0:{formatString}}}",
                                 enummember?.Value ?? parameterValue);
        }
    }

    public class DefaultApiExceptionFactory
    {
        static readonly Task<Exception?> NullTask = Task.FromResult<Exception?>(null);

        readonly RefitSettings refitSettings;

        public DefaultApiExceptionFactory(RefitSettings refitSettings)
        {
            this.refitSettings = refitSettings;
        }

        public Task<Exception?> CreateAsync(HttpResponseMessage responseMessage)
        {
            if (!responseMessage.IsSuccessStatusCode)
            {
                return CreateExceptionAsync(responseMessage, refitSettings)!;
            }
            else
            {
                return NullTask;
            }
        }

        static async Task<Exception> CreateExceptionAsync(HttpResponseMessage responseMessage, RefitSettings refitSettings)
        {
            var requestMessage = responseMessage.RequestMessage!;
            var method = requestMessage.Method;

            return await ApiException.Create(requestMessage, method, responseMessage, refitSettings).ConfigureAwait(false);
        }
    }
}
