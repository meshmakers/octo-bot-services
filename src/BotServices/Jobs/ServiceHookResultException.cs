using System;
using System.Net;
using System.Runtime.Serialization;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.Jobs;

[Serializable]
public class ServiceHookResultException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ServiceHookResultException(HttpStatusCode httpStatusCode)
        : this(null, httpStatusCode, null)
    {
    }

    public ServiceHookResultException(string message, HttpStatusCode httpStatusCode)
        : this(message, httpStatusCode, null)
    {
    }

    public ServiceHookResultException(string message, HttpStatusCode httpStatusCode, Exception inner) : base(
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
