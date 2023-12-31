#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

[Serializable]
public class FilterAttributeNotFoundException : Exception
{
    //
    // For guidelines regarding the creation of new exception types, see
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/cpgenref/html/cpconerrorraisinghandlingguidelines.asp
    // and
    //    http://msdn.microsoft.com/library/default.asp?url=/library/en-us/dncscol/html/csharp07192001.asp
    //

    public FilterAttributeNotFoundException()
    {
    }

    public FilterAttributeNotFoundException(string message) : base(message)
    {
    }

    public FilterAttributeNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }
}
