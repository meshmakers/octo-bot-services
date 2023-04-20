using System;
using System.Runtime.Serialization;

#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.Jobs;

[Serializable]
public class ServiceHookException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public ServiceHookException()
    {
    }

    public ServiceHookException(string message) : base(message)
    {
    }

    public ServiceHookException(string message, Exception inner) : base(message, inner)
    {
    }

    protected ServiceHookException(
        SerializationInfo info,
        StreamingContext context) : base(info, context)
    {
    }
}
