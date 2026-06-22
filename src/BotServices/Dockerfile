FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

ARG OCTO_PRIVATE_NUGET_SERVICE
ARG OCTO_PRIVATE_NUGET_CERTIFICATE
ARG OCTO_VERSION
ARG OCTO_CORE_LIB_VERSION
ARG OCTO_COMM_VERSION

COPY ${OCTO_PRIVATE_NUGET_CERTIFICATE} /usr/local/share/ca-certificates/nugetca.crt
RUN chmod 644 /usr/local/share/ca-certificates/nugetca.crt && update-ca-certificates

WORKDIR /src
COPY . .
ENV NUGET_XMLDOC_MODE=none
WORKDIR "/src/src/BotServices"
RUN dotnet build "BotServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION} -p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION} -p:OctoCommVersion=${OCTO_COMM_VERSION}

FROM build AS publish
RUN dotnet publish "BotServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} -p:OctoVersion=${OCTO_VERSION} -p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION} -p:OctoCommVersion=${OCTO_COMM_VERSION} -o /app

FROM base AS final

# Install necessary packages mongosh, mongorestore, mongodump
RUN apt-get update && \
    apt-get install -y wget curl unzip ca-certificates gnupg && \
    wget -qO- https://www.mongodb.org/static/pgp/server-8.0.asc | tee /etc/apt/trusted.gpg.d/server-8.0.asc && \
    echo "deb [ arch=amd64,arm64 ] https://repo.mongodb.org/apt/ubuntu noble/mongodb-org/8.0 multiverse" | tee /etc/apt/sources.list.d/mongodb-org-8.0.list && \
    apt-get update && \
    apt-get install -y wget curl unzip mongodb-mongosh mongodb-database-tools

WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "Meshmakers.Octo.Backend.BotServices.dll"]