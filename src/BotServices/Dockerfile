FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY . .
WORKDIR "/src/src/BotServices"
RUN dotnet build "BotServices.csproj" -c Release

FROM build AS publish
RUN dotnet publish "BotServices.csproj" -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.BotServices.dll"]