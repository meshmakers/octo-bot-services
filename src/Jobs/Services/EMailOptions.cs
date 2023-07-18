using System.Diagnostics.CodeAnalysis;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     Settings of SMTP server for E-Mail Services
/// </summary>
[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
public class EMailOptions
{
    /// <summary>
    ///     Host name of SMTP server
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    ///     E-Mail Address the mail is sent from
    /// </summary>
    public string? SenderEMail { get; set; }

    /// <summary>
    ///     User name for authentication to SMTP
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    ///     Password for authentication to SMTP
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    ///     True, when SSL has to be used, otherwise false
    /// </summary>
    public bool EnableSsl { get; set; }

    /// <summary>
    ///     The port of the SMTP server
    /// </summary>
    public int Port { get; set; }
}
