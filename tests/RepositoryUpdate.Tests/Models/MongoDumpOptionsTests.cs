using FluentAssertions;
using RepositoryUpdate.Models;

namespace RepositoryUpdate.Tests.Models;

public class MongoDumpOptionsTests
{
    [Fact]
    public void MongoDumpOptions_DefaultConstructor_ShouldHaveDefaultValues()
    {
        // Arrange & Act
        var options = new MongoDumpOptions
        {
            Database = "testdb"
        };

        // Assert
        options.Database.Should().BeEquivalentTo("testdb");
        options.Collection.Should().BeNull();
        options.OutputDirectory.Should().BeNull();
        options.Archive.Should().BeNull();
        options.Gzip.Should().BeFalse();
        options.Pretty.Should().BeFalse();
        options.Verbose.Should().BeFalse();
    }

    [Fact]
    public void MongoDumpOptions_ForDatabase_ShouldSetDatabaseAndOutputDirectory()
    {
        // Arrange
        var database = "testdb";
        var outputPath = "/tmp/backup";

        // Act
        var options = MongoDumpOptions.ForDatabase(database, outputPath);

        // Assert
        options.Database.Should().Be(database);
        options.OutputDirectory.Should().Be(outputPath);
        options.Archive.Should().BeNull();
        options.Gzip.Should().BeFalse();
    }

    [Fact]
    public void MongoDumpOptions_ForArchive_ShouldSetDatabaseArchiveAndGzip()
    {
        // Arrange
        var database = "testdb";
        var archivePath = "/tmp/backup.gz";

        // Act
        var options = MongoDumpOptions.ForArchive(database, archivePath);

        // Assert
        options.Database.Should().Be(database);
        options.Archive.Should().Be(archivePath);
        options.Gzip.Should().BeTrue();
        options.OutputDirectory.Should().BeNull();
    }

    [Fact]
    public void MongoDumpOptions_CanSetAllProperties()
    {
        // Arrange & Act
        var options = new MongoDumpOptions
        {
            Database = "testdb",
            Collection = "testcol",
            OutputDirectory = "/tmp/backup",
            Archive = "/tmp/backup.gz",
            Gzip = true,
            Pretty = true,
            Verbose = true
        };

        // Assert
        options.Database.Should().Be("testdb");
        options.Collection.Should().Be("testcol");
        options.OutputDirectory.Should().Be("/tmp/backup");
        options.Archive.Should().Be("/tmp/backup.gz");
        options.Gzip.Should().BeTrue();
        options.Pretty.Should().BeTrue();
        options.Verbose.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MongoDumpOptions_ForDatabase_WithInvalidDatabase_ShouldStillCreateOptions(string? database)
    {
        // Arrange
        var outputPath = "/tmp/backup";

        // Act
        var options = MongoDumpOptions.ForDatabase(database!, outputPath);

        // Assert
        options.Database.Should().Be(database);
        options.OutputDirectory.Should().Be(outputPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void MongoDumpOptions_ForArchive_WithInvalidArchivePath_ShouldStillCreateOptions(string? archivePath)
    {
        // Arrange
        var database = "testdb";

        // Act
        var options = MongoDumpOptions.ForArchive(database, archivePath!);

        // Assert
        options.Database.Should().Be(database);
        options.Archive.Should().Be(archivePath);
        options.Gzip.Should().BeTrue();
    }
}
