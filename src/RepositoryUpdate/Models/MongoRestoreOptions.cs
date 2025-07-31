namespace RepositoryUpdate.Models;

public class MongoRestoreOptions
{
    // Database/Collection
    public required string Database { get; set; }
    public string Collection { get; set; } = "*";
    
    // Input
    public string? InputDirectory { get; set; }
    public string? Archive { get; set; }

    // Options
    public bool Drop { get; set; }
    public bool Gzip { get; set; }
    public bool Verbose { get; set; }
    public bool DryRun { get; set; }
    public bool OplogReplay { get; set; }
    public bool RestoreDbUsersAndRoles { get; set; }
    public int? NumParallelCollections { get; set; }

    public static MongoRestoreOptions FromDirectory(string inputPath, string database)
    {
        return new MongoRestoreOptions
        {
            InputDirectory = inputPath,
            Database = database
        };
    }

    public static MongoRestoreOptions FromArchive(string archivePath, string database)
    {
        return new MongoRestoreOptions
        {
            Archive = archivePath,
            Database = database,
            Gzip = true
        };
    }
}