#pragma warning disable 1591

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

[Serializable]
public class FilterAttributeNotFoundException : Exception
{
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