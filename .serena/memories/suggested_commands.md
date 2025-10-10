# Suggested Commands: octo-bot-services

## Critical: Always Use DebugL for Local Development

⚠️ **IMPORTANT**: Always use `--configuration DebugL` for local development builds. This sets the version to `999.0.0` and uses the local NuGet feed at `../nuget`.

## Building

```bash
# Local development build (REQUIRED for local work)
dotnet build Octo.Bots.sln --configuration DebugL

# Production build
dotnet build Octo.Bots.sln --configuration Release

# Clean build
dotnet clean Octo.Bots.sln
dotnet build Octo.Bots.sln --configuration DebugL
```

## Testing

```bash
# Run all tests (local configuration)
dotnet test Octo.Bots.sln --configuration DebugL

# Run only unit tests (exclude integration tests)
dotnet test --filter "Category!=Integration" --configuration DebugL

# Run only integration tests (requires MongoDB tools: mongosh, mongodump, mongorestore)
dotnet test --filter "Category=Integration" --configuration DebugL

# Run a specific test
dotnet test --filter "FullyQualifiedName~ClassName.MethodName" --configuration DebugL

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed" --configuration DebugL

# Generate code coverage
dotnet test --collect:"XPlat Code Coverage" --configuration DebugL

# Run tests for a specific project
dotnet test tests/RepositoryUpdate.Tests/RepositoryUpdate.Tests.csproj --configuration DebugL
```

## Running the Application

```bash
# Run the BotServices application
dotnet run --project src/BotServices/BotServices.csproj --configuration DebugL

# Or navigate to the project directory
cd src/BotServices
dotnet run --configuration DebugL
```

## TypeScript Scripts (RepositoryUpdate)

```bash
# Navigate to RepositoryUpdate project
cd src/RepositoryUpdate

# Install npm dependencies
npm install

# Type-check TypeScript files
npm run type-check
```

## NuGet Package Management

```bash
# Restore NuGet packages
dotnet restore Octo.Bots.sln

# Pack a project as NuGet package (e.g., Jobs project)
dotnet pack src/Jobs/Jobs.csproj --configuration DebugL

# List outdated packages
dotnet list package --outdated

# Update a specific package
dotnet add package <PackageName> --version <Version>
```

## Docker Commands

```bash
# Build Docker image
docker build -f src/BotServices/Dockerfile -t octo-bot-services:latest .

# Run Docker container
docker run -p 8080:80 octo-bot-services:latest
```

## Solution Management

```bash
# List all projects in solution
dotnet sln Octo.Bots.sln list

# Add a project to solution
dotnet sln Octo.Bots.sln add <path-to-project.csproj>

# Remove a project from solution
dotnet sln Octo.Bots.sln remove <path-to-project.csproj>
```

## Git Workflow

```bash
# Check status
git status

# Create feature branch
git checkout -b dev/feature-name

# Stage changes
git add .

# Commit changes
git commit -m "AB#<work-item>: <description>"

# Push to remote
git push origin dev/feature-name

# Pull latest changes
git pull origin main
```

## Useful Inspection Commands

```bash
# View project dependencies
dotnet list src/BotServices/BotServices.csproj package

# View project references
dotnet list src/BotServices/BotServices.csproj reference

# Check .NET SDK version
dotnet --version

# List installed SDKs
dotnet --list-sdks

# View build output
dir bin\DebugL  # Windows
```

## MongoDB Tools (for Integration Tests)

```bash
# Check if MongoDB tools are installed
mongosh --version
mongodump --version
mongorestore --version

# If not installed, download from: https://www.mongodb.com/try/download/database-tools
```
