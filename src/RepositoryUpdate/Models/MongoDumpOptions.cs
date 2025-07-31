namespace RepositoryUpdate.Models;

public class MongoDumpOptions
{
    // Database/Collection
    public required string Database { get; set; }
    public string? Collection { get; set; }

    // Output
    public string? OutputDirectory { get; set; }
    public string? Archive { get; set; }

    // Options
    public bool Gzip { get; set; }
    public bool Pretty { get; set; }
    public bool Verbose { get; set; }

    public static MongoDumpOptions ForDatabase(string database, string outputPath)
    {
        return new MongoDumpOptions
        {
            Database = database,
            OutputDirectory = outputPath
        };
    }

    public static MongoDumpOptions ForArchive(string database, string archivePath)
    {
        return new MongoDumpOptions
        {
            Database = database,
            Archive = archivePath,
            Gzip = true
        };
    }
}