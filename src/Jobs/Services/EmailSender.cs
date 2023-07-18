using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared.Services;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     Implementation of E-Mail sender
/// </summary>
public class EMailSender : IEMailSender
{
    private readonly SmtpClient? _client;
    private readonly EMailOptions _eMailOptions;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="eMailOptions">Settings of the SMTP server</param>
    public EMailSender(IOptions<EMailOptions> eMailOptions)
    {
        _eMailOptions = eMailOptions.Value;

        if (!string.IsNullOrWhiteSpace(eMailOptions.Value.Host) && !string.IsNullOrWhiteSpace(_eMailOptions.UserName)
                                                                && !string.IsNullOrWhiteSpace(_eMailOptions.Password))
        {
            _client = new SmtpClient(_eMailOptions.Host, _eMailOptions.Port)
            {
                Credentials = new NetworkCredential(_eMailOptions.UserName, _eMailOptions.Password),
                EnableSsl = _eMailOptions.EnableSsl
            };
        }
    }

    /// <inheritdoc />
    public async Task SendEmailAsync(string emailAddress, string? subject, string? htmlMessage)
    {
        ArgumentValidation.ValidateString(nameof(emailAddress), emailAddress);

        if (_client == null)
        {
            throw new NotificationSendFailedException(
                "No SMTP settings has been defined, no E-Mail notifications are possible");
        }

        if (string.IsNullOrWhiteSpace(_eMailOptions.SenderEMail))
        {
            throw new NotificationSendFailedException(
                "No sender E-Mail setting has been defined, no E-Mail notifications are possible");
        }

        try
        {
            await _client.SendMailAsync(
                new MailMessage(_eMailOptions.SenderEMail, emailAddress, subject, htmlMessage) { IsBodyHtml = true }
            );
        }
        catch (Exception e)
        {
            throw new NotificationSendFailedException("Sending E-Mail failed.", e);
        }
    }
}
