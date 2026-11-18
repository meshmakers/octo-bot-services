namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     The archive of a restore is not a dump this platform produced for the target tenant. The message is the same for
///     an edited archive, an unknown archive and another tenant's dump.
/// </summary>
[Serializable]
public sealed class ArchiveNotRegisteredException : JobFailedException
{
    /// <summary>The declared error code of the refusal.</summary>
    public const string ErrorCode = "ARCHIVE_NOT_REGISTERED";

    /// <summary>The message of every refusal.</summary>
    public const string RefusalMessage =
        ErrorCode + ": the archive is not a dump this platform produced for this tenant. " +
        "Restore an unedited dump of this tenant, or ask a system operator.";

    /// <inheritdoc />
    public ArchiveNotRegisteredException() : base(RefusalMessage)
    {
    }
}
