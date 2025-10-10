# Technology Stack: octo-bot-services

## Framework & Runtime

- **Target Framework**: .NET 9.0 (`net9.0`)
- **Language**: C# (Latest major version)
- **Runtime**: ASP.NET Core for web hosting

## Database & Storage

- **Primary Database**: MongoDB
- **ORM**: Entity Framework Core (with MongoDB provider)
- **Background Job Storage**: Hangfire.Mongo

## Background Jobs & Scheduling

- **Job Framework**: Hangfire
- **Job Storage**: MongoDB
- **Job Types**: Recurring jobs, delayed jobs, fire-and-forget jobs

## Authentication & Authorization

- **JWT Bearer Tokens**: For API authentication
- **OpenID Connect**: OAuth2 introspection via `IdentityModel.AspNetCore.OAuth2Introspection`
- **Identity Integration**: Connects to octo-identity-services

## Logging & Monitoring

- **Logging Framework**: NLog
- **Configuration**: `nlog.config` in BotServices project
- **Dashboard**: Hangfire Dashboard for job monitoring

## Testing

- **Test Framework**: xUnit
- **Mocking**: Moq and FakeItEasy
- **Assertions**: FluentAssertions
- **Code Coverage**: Coverlet (`dotnet test --collect:"XPlat Code Coverage"`)

## Build & Packaging

- **Build System**: MSBuild with .NET SDK
- **Package Manager**: NuGet
- **Configuration Modes**: Debug, DebugL (local), Release
- **Containerization**: Docker (Dockerfile in BotServices)

## Scripting & Tooling

- **TypeScript**: For MongoDB script development with type checking
- **Node.js & npm**: For TypeScript compilation and package management

## API Documentation

- **Swagger/OpenAPI**: Auto-generated API documentation
- **Swagger UI**: Available at runtime for API testing

## CI/CD

- **Pipeline**: Azure DevOps Pipelines
- **Build Agent Pool**: meshmakers-ci-agents
- **Deployment**: Docker image builds and pushes

## Dependencies on OctoMesh Packages

All using version `$(OctoVersion)` (999.0.0 for DebugL):
- `Meshmakers.Octo.Services.Swagger`
- `Meshmakers.Octo.Services.Notifications`
- `Meshmakers.Octo.Services.Infrastructure`
- `Meshmakers.Octo.Services.Observability`
- `Meshmakers.Octo.Services.Contracts`
- `Meshmakers.Octo.Runtime.Engine.MongoDb`

## External Package Sources

- **Private NuGet**: `https://nuget.mm.cloud/v3/index.json`
- **Public NuGet**: `https://api.nuget.org/v3/index.json`
- **Local Feed** (DebugL only): `../nuget`
