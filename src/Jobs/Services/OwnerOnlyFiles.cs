namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     Creates directories and files that only the service user can read (Unix modes 0700 / 0600; plain create on
///     Windows). Used for the scratch directory, where dumps exist in plaintext for a short time.
/// </summary>
internal static class OwnerOnlyFiles
{
    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    ///     Creates <paramref name="path" /> (and its parents) and restricts it to the owner, also when it already
    ///     existed with a wider mode.
    /// </summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyDirectory);
        if (File.GetUnixFileMode(path) != OwnerOnlyDirectory)
        {
            File.SetUnixFileMode(path, OwnerOnlyDirectory);
        }
    }

    /// <summary>
    ///     Creates a new file (fails when it exists) that only the owner can read and write.
    /// </summary>
    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 81920
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        return new FileStream(path, options);
    }
}
