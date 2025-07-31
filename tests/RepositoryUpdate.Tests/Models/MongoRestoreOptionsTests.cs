using FluentAssertions;
using RepositoryUpdate.Models;

namespace RepositoryUpdate.Tests.Models;

public class MongoRestoreOptionsTests
{
    [Fact]
    public void MongoRestoreOptions_DefaultConstructor_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var options = new MongoRestoreOptions { Database = "testdb" };

        // Assert
        options.Database.Should().BeEquivalentTo("testdb");
        options.Collection.Should().BeEquivalentTo("*");
        options.InputDirectory.Should().BeNull();
        options.Archive.Should().BeNull();
        options.Drop.Should().BeFalse();
        options.Gzip.Should().BeFalse();
        options.Verbose.Should().BeFalse();
        options.DryRun.Should().BeFalse();
    }

    [Fact]
    public void MongoRestoreOptions_FromDirectory_WithDatabase_ShouldSetInputDirectoryAndDatabase()
    {
        // Arrange
        var inputPath = "/tmp/backup";
        var database = "testdb";

        // Act
        var options = MongoRestoreOptions.FromDirectory(inputPath, database);

        // Assert
        options.InputDirectory.Should().Be(inputPath);
        options.Database.Should().Be(database);
        options.Archive.Should().BeNull();
        options.Gzip.Should().BeFalse();
    }

    [Fact]
    public void MongoRestoreOptions_FromDirectory_WithoutDatabase_ShouldSetOnlyInputDirectory()
    {
        // Arrange
        var inputPath = "/tmp/backup";

        // Act
        var options = MongoRestoreOptions.FromDirectory(inputPath, "testdb");

        // Assert
        options.InputDirectory.Should().Be(inputPath);
        options.Database.Should().BeEquivalentTo("testdb");
        options.Archive.Should().BeNull();
        options.Gzip.Should().BeFalse();
    }

    [Fact]
    public void MongoRestoreOptions_FromArchive_WithDatabase_ShouldSetArchiveDatabaseAndGzip()
    {
        // Arrange
        var archivePath = "/tmp/backup.gz";
        var database = "testdb";

        // Act
        var options = MongoRestoreOptions.FromArchive(archivePath, database);

        // Assert
        options.Archive.Should().Be(archivePath);
        options.Database.Should().Be(database);
        options.Gzip.Should().BeTrue();
        options.InputDirectory.Should().BeNull();
    }

    [Fact]
    public void MongoRestoreOptions_FromArchive_WithoutDatabase_ShouldSetArchiveAndGzip()
    {
        // Arrange
        var archivePath = "/tmp/backup.gz";

        // Act
        var options = MongoRestoreOptions.FromArchive(archivePath, "testdb");

        // Assert
        options.Archive.Should().Be(archivePath);
        options.Database.Should().BeEquivalentTo("testdb");
        options.Gzip.Should().BeTrue();
        options.InputDirectory.Should().BeNull();
    }

    [Fact]
    public void MongoRestoreOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new MongoRestoreOptions
        {
            Database = "testdb",
            Collection = "testcol",
            InputDirectory = "/tmp/backup",
            Archive = "/tmp/backup.gz",
            Drop = true,
            Gzip = true,
            Verbose = true,
            DryRun = true
        };

        // Assert
        options.Database.Should().Be("testdb");
        options.Collection.Should().Be("testcol");
        options.InputDirectory.Should().Be("/tmp/backup");
        options.Archive.Should().Be("/tmp/backup.gz");
        options.Drop.Should().BeTrue();
        options.Gzip.Should().BeTrue();
        options.Verbose.Should().BeTrue();
        options.DryRun.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MongoRestoreOptions_FromDirectory_WithInvalidInputPath_ShouldStillCreateOptions(string? inputPath)
    {
        // Arrange
        var database = "testdb";

        // Act
        var options = MongoRestoreOptions.FromDirectory(inputPath!, database);

        // Assert
        options.InputDirectory.Should().Be(inputPath);
        options.Database.Should().Be(database);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MongoRestoreOptions_FromArchive_WithInvalidArchivePath_ShouldStillCreateOptions(string? archivePath)
    {
        // Arrange
        var database = "testdb";

        // Act
        var options = MongoRestoreOptions.FromArchive(archivePath!, database);

        // Assert
        options.Archive.Should().Be(archivePath);
        options.Database.Should().Be(database);
        options.Gzip.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("testdb")]
    public void MongoRestoreOptions_FromMethods_WithInvalidDatabase_ShouldStillCreateOptions(string database)
    {
        // Arrange
        var inputPath = "/tmp/backup";
        var archivePath = "/tmp/backup.gz";

        // Act
        var directoryOptions = MongoRestoreOptions.FromDirectory(inputPath, database);
        var archiveOptions = MongoRestoreOptions.FromArchive(archivePath, database);

        // Assert
        directoryOptions.Database.Should().Be(database);
        archiveOptions.Database.Should().Be(database);
    }
}
