using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.Configuration;

/// <summary>
///     Binds the CrateDB connection for the bot's direct stream-data access (AB#4230) from
///     <see cref="OctoBotServicesOptions"/> (<c>Bot:StreamDataHost</c> / <c>StreamDataUser</c> /
///     <c>StreamDataPassword</c>). Mirror of the asset-repo <c>ConfigureStreamDataConfiguration</c>;
///     supplies the connection string the <c>AddCrateDbStreamDataRepository</c> wiring needs.
/// </summary>
public class BotConfigureStreamDataConfiguration : IConfigureNamedOptions<StreamDataConfiguration>
{
    private readonly IOptions<OctoBotServicesOptions> _options;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public BotConfigureStreamDataConfiguration(IOptions<OctoBotServicesOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public void Configure(StreamDataConfiguration options)
    {
        Configure(Options.DefaultName, options);
    }

    /// <inheritdoc />
    public void Configure(string? name, StreamDataConfiguration options)
    {
        var o = _options.Value;
        options.ConnectionStringFromConfiguration(
            o.StreamDataHost,
            o.StreamDataUser,
            o.StreamDataPassword);
    }
}
