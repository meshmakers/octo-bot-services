using System;
using System.Net;
using System.Runtime.Serialization;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.Jobs;

[Serializable]
public class ServiceHookResultException : Exception
{
    public ServiceHookResultException(HttpStatusCode httpStatusCode)
        : this(null, httpStatusCode)
    {
    }

    public ServiceHookResultException(string? message, HttpStatusCode httpStatusCode, Exception? inner = null) : base(
        string.IsNullOrEmpty(message)
            ? $"The service returned result '{httpStatusCode}'"
            : $"{httpStatusCode}: {message}", inner)
    {
        HttpStatusCode = httpStatusCode;
    }

    protected ServiceHookResultException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }

    public HttpStatusCode HttpStatusCode { get; }
}
