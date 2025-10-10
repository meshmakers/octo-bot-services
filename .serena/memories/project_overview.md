# Project Overview: octo-bot-services

## Purpose

The **octo-bot-services** repository is part of the OctoMesh data mesh platform. It provides:

- **Bot automation services**: Background job processing and automation functionality
- **Hangfire-based job management**: Scheduled and recurring background jobs with MongoDB storage
- **MongoDB repository operations**: Database dump, restore, import/export, and fixup operations
- **Service hook execution**: Callback execution for service integrations
- **Attribute value aggregation**: Data aggregation functionality

This service is a microservice within the larger OctoMesh ecosystem that handles automated background tasks and bot-related functionality.

## Main Components

1. **BotServices**: ASP.NET Core web application hosting the API and Hangfire dashboard
2. **Jobs**: Background job implementations (packaged as `Meshmakers.Octo.Backend.Jobs` NuGet)
3. **RepositoryUpdate**: MongoDB repository management with TypeScript script support
4. **SystemBotCkModel**: System bot construction kit data models
5. **BotServices.Resources**: Localized resource files

## Key Features

- Background job scheduling and execution via Hangfire
- MongoDB database backup/restore operations
- Data model import/export functionality
- OAuth2 introspection + JWT Bearer authentication
- Swagger UI for API documentation
- Hangfire Dashboard for job monitoring
- TypeScript integration for MongoDB scripts with IntelliSense
