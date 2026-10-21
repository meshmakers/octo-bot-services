using Hangfire.Mongo;
using Hangfire.Mongo.Migration;
using Hangfire.Mongo.Migration.Strategies;
using Hangfire.Mongo.Migration.Strategies.Backup;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.BotServices.HangfireSetup;

/// <summary>
///     Storage options and schema pre-flight for the Hangfire MongoDB job storage (AB#7196).
/// </summary>
/// <remarks>
///     <para>
///         The storage used to run with <c>DropMongoMigrationStrategy</c> + <c>NoneMongoBackupStrategy</c>: whenever the
///         <c>hangfire.schema</c> document was missing or differed from the library's schema, Hangfire.Mongo dropped every
///         Hangfire collection without a backup — including all recurring jobs (the cron triggers of every tenant
///         pipeline). Now the schema is migrated in place with a collection backup, and states that cannot be migrated
///         stop the service with a clear error instead of touching data.
///     </para>
///     <para>
///         Library behaviour that shapes this class (Hangfire.Mongo 1.16.2): with a missing schema document the
///         library assumes the pre-4 layout and replays all legacy migration steps on the data, and its backup only
///         covers the legacy collection names — wrong for a current-layout database whose schema document was merely
///         lost (the AB#7180 restore). <see cref="PrepareSchema" /> therefore recognises that state and restores the
///         schema document before the library looks at it.
///     </para>
/// </remarks>
public static class HangfireMongoStorageSetup
{
    /// <summary>
    ///     Schema assumed for a database that has current-layout Hangfire collections but no schema document. Version 24
    ///     is the oldest version of the current layout whose follow-up migration (24 to 25) is idempotent, so the library
    ///     migrates (and backs up) from there.
    /// </summary>
    internal const MongoSchema InferredSchema = MongoSchema.Version24;

    /// <summary>Storage options: migrate in place, back up every Hangfire collection before a migration.</summary>
    public static MongoStorageOptions CreateStorageOptions()
    {
        return new MongoStorageOptions
        {
            MigrationOptions = new MongoMigrationOptions
            {
                MigrationStrategy = new MigrateMongoMigrationStrategy(),
                BackupStrategy = new CollectionMongoBackupStrategy(),
                // A fixed postfix makes the second migration from the same schema fail ("backup already exists").
                BackupPostfix = $"migrationbackup-{DateTime.UtcNow:yyyyMMddHHmmss}"
            }
        };
    }

    /// <summary>
    ///     Checks the schema state of the Hangfire database before the storage is created. Throws
    ///     <see cref="HangfireSchemaException" /> when the state cannot be migrated safely; never drops or rewrites job data.
    /// </summary>
    public static void PrepareSchema(IMongoDatabase database, MongoStorageOptions storageOptions, ILogger? logger = null)
    {
        var databaseName = database.DatabaseNamespace.DatabaseName;
        var prefix = storageOptions.Prefix;
        var required = new MongoMigrationManager(storageOptions, database).RequiredSchemaVersion;

        var schemaCollection = database.GetCollection<BsonDocument>($"{prefix}.schema");
        var schemaDocuments = schemaCollection.Find(new BsonDocument()).Limit(2).ToList();

        if (schemaDocuments.Count == 0)
        {
            var existing = MongoMigrationUtils.ExistingDatabaseCollectionNames(database).ToHashSet();
            if (!existing.Contains($"{prefix}.jobGraph"))
            {
                // Fresh database (or one without Hangfire data): the library creates everything.
                return;
            }

            logger?.LogWarning(
                "Hangfire database '{Database}' has job data but no '{Prefix}.schema' document. Restoring the schema as {Inferred} so that the library migrates in place to {Required} (with a collection backup) instead of dropping the data.",
                databaseName, prefix, (int)InferredSchema, (int)required);
            schemaCollection.InsertOne(new BsonDocument("_id", (int)InferredSchema));
            return;
        }

        if (schemaDocuments.Count > 1)
        {
            throw new HangfireSchemaException(
                $"Hangfire database '{databaseName}' has more than one document in '{prefix}.schema'. Resolve this manually; no data was changed.");
        }

        var id = schemaDocuments[0]["_id"];
        MongoSchema found;
        if (id.IsInt32 && Enum.IsDefined(typeof(MongoSchema), id.AsInt32))
        {
            found = (MongoSchema)id.AsInt32;
        }
        else if (id.IsString && Enum.TryParse(id.AsString, out MongoSchema parsed) && Enum.IsDefined(parsed))
        {
            found = parsed;
        }
        else if (id.IsInt32 && id.AsInt32 > (int)required)
        {
            throw NewerThanSupported(databaseName, id.AsInt32.ToString(), required);
        }
        else
        {
            throw new HangfireSchemaException(
                $"Hangfire database '{databaseName}' has an unknown schema marker '{id}' in '{prefix}.schema' (supported: up to {(int)required}). The bot services stopped; no data was changed. Resolve this manually.");
        }

        if (found > required)
        {
            throw NewerThanSupported(databaseName, ((int)found).ToString(), required);
        }
    }

    private static HangfireSchemaException NewerThanSupported(string databaseName, string found, MongoSchema required)
    {
        return new HangfireSchemaException(
            $"Hangfire database '{databaseName}' has schema version {found}, but this bot services build supports up to {(int)required} (downgrade?). Backwards migration is not supported; the bot services stopped and no data was changed. Run a bot services build with a newer Hangfire.Mongo or restore the Hangfire collections from a backup.");
    }
}

/// <summary>The Hangfire database is in a state that cannot be migrated without risking job data.</summary>
public sealed class HangfireSchemaException : InvalidOperationException
{
    /// <summary>Creates the exception with a message that names the database and the found/expected version.</summary>
    public HangfireSchemaException(string message) : base(message)
    {
    }
}
