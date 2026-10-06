using System.Net;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;

namespace Meshmakers.Octo.Backend.BotServices.Controllers;

/// <summary>
///     A <c>400</c> answer whose <see cref="ApiErrorDto.StatusDescription" /> carries a machine-readable error
///     code instead of the generic status name, e.g. <c>ConfirmationRequired</c> (AB#5544, contract §12).
/// </summary>
public class CodedBadRequestErrorDto : ApiErrorDto
{
    /// <summary>
    ///     Error code of a secret sweep that changes data without <c>confirm=true</c>.
    /// </summary>
    public const string ConfirmationRequired = "ConfirmationRequired";

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="code">The error code</param>
    /// <param name="message">A value-free message</param>
    public CodedBadRequestErrorDto(string code, string message)
        : base(HttpStatusCode.BadRequest, code, message)
    {
    }
}
