# Ledger service — ASP.NET Core (health + REST later) + gRPC clients to brain.
# Build context is the REPO ROOT so Grpc.Tools can see the shared protos/ at build time.
# One image, two entrypoints: the API (default) and the migrations runner (compose overrides the command).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore against just the project files first (better layer caching).
COPY services/ledger/Boys.Ledger.sln services/ledger/
COPY services/ledger/src/Boys.Ledger.Domain/*.csproj services/ledger/src/Boys.Ledger.Domain/
COPY services/ledger/src/Boys.Ledger.Contracts/*.csproj services/ledger/src/Boys.Ledger.Contracts/
COPY services/ledger/src/Boys.Ledger.Api/*.csproj services/ledger/src/Boys.Ledger.Api/
COPY services/ledger/src/Boys.Ledger.Migrations/*.csproj services/ledger/src/Boys.Ledger.Migrations/
RUN dotnet restore services/ledger/src/Boys.Ledger.Api/Boys.Ledger.Api.csproj \
 && dotnet restore services/ledger/src/Boys.Ledger.Migrations/Boys.Ledger.Migrations.csproj

# Bring in the protos (needed by the Contracts codegen) and the full source, then publish both apps.
COPY protos/ protos/
COPY services/ledger/ services/ledger/
RUN dotnet publish services/ledger/src/Boys.Ledger.Api/Boys.Ledger.Api.csproj \
      -c Release -o /app/api /p:UseAppHost=false \
 && dotnet publish services/ledger/src/Boys.Ledger.Migrations/Boys.Ledger.Migrations.csproj \
      -c Release -o /app/migrate /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
# curl is used by the compose healthcheck to probe /health/ready.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/api ./api
COPY --from=build /app/migrate ./migrate
COPY services/ledger/migrations ./migrations

ENV ASPNETCORE_HTTP_PORTS=8080
ENV MIGRATIONS_DIR=/app/migrations
EXPOSE 8080

# Default = the API. The one-shot migrate service overrides this with the migrations DLL.
CMD ["dotnet", "api/Boys.Ledger.Api.dll"]
