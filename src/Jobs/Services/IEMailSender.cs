using System.Threading.Tasks;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     E-Mail sender interface
/// </summary>
public interface IEMailSender
{
    /// <summary>
    ///     Sends an E-Mail with an HTML body
    /// </summary>
    /// <param name="emailAddress">Receiver of E-Mail</param>
    /// <param name="subject">Subject of E-Mail</param>
    /// <param name="htmlMessage">Body of E-Mail</param>
    /// <returns></returns>
    Task SendEmailAsync(string emailAddress, string? subject, string? htmlMessage);
}
