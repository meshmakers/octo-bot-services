---
description: The job storage connection is built from a MongoUrl and one client, never from a connection string.
applies_to: src/BotServices/HangfireSetup/**
---

# Hangfire MongoDB connection

## Hangfire MongoDB connection: never the string form (AB#9640)

`HangfireSetup/HangfireMongoConnection.CreateUrl` builds the job storage connection from `OctoSystemConfiguration`
(admin user, `AuthenticationDatabaseName`, TLS, hosts) and `Bot:JobDatabaseName` / `InstancePrefix`. `Program.cs` hands
the driver the `MongoUrl` and one `MongoClient` built from it (`UseMongoStorage(client, databaseName, options)`), never
a string: since MongoDB.Driver 3.14.1 (CSHARP-6205) `ToString()` of `MongoUrlBuilder`, `MongoUrl` and `ConnectionString`
returns a redacted URL (user and password `<redacted>`). Built from that string, the client logged in as the user
`<redacted>`, the server found no SCRAM mechanisms for it, the driver fell back to SCRAM-SHA-1 and a server with
SCRAM-SHA-256 only refused the start (test-2, 2026-11-22). Where an unredacted string is really needed (an external tool),
use `MongoUrl.Url`. No authentication mechanism is forced; the driver negotiates it. Tests:
`Jobs.Tests/Configuration/HangfireMongoConnectionTests` and `Jobs.IntegrationTests/HangfireMongoAuthenticationTests`
(Docker: MongoDB with `authenticationMechanisms=SCRAM-SHA-256` and a SCRAM-SHA-256-only user; the redacted-string
check runs only with a driver that redacts).
