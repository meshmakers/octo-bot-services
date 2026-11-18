namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>Whether a restore admits only an archive registered for the target tenant.</summary>
public enum ArchiveRegistrationCheck
{
    /// <summary>A tenant identity started the restore: only a dump registered for the target tenant restores.</summary>
    Required = 0,

    /// <summary>A system operator started the restore: any archive restores.</summary>
    NotRequired = 1
}
