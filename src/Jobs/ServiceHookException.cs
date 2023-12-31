#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.Jobs;

[Serializable]
public class ServiceHookException : JobFailedException
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

    public ServiceHookException(string message, Exception? inner) : base(message, inner)
    {
    }
}
