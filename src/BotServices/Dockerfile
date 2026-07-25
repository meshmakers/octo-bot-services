FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Build on the native build platform (IL is portable) so multi-arch builds don't
# emulate the SDK; the runtime arch is selected by the `base`/`final` stages.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

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
RUN dotnet build "BotServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}}

FROM build AS publish
RUN dotnet publish "BotServices.csproj" -c Release -p:OctoNugetPrivateServer=${OCTO_PRIVATE_NUGET_SERVICE} ${OCTO_VERSION:+-p:OctoVersion=${OCTO_VERSION}} ${OCTO_CORE_LIB_VERSION:+-p:OctoCoreLibVersion=${OCTO_CORE_LIB_VERSION}} ${OCTO_COMM_VERSION:+-p:OctoCommVersion=${OCTO_COMM_VERSION}} -o /app

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
# Launch dotnet through a shell so LD_PRELOAD-based injectors (Dash0 OpenTelemetry
# auto-instrumentation) can intercept the execve and activate the CLR profiler.
# With dotnet as PID 1 there is no child exec to intercept and the service emits
# no traces (AB#4347). `exec` keeps dotnet as the signal-receiving process.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet Meshmakers.Octo.Backend.BotServices.dll"]