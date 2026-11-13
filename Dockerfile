FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY backend/ backend/
# One image, two entrypoints: the API (default) and the deploy-time migrator (dotnet Flow4Tech.Migrator.dll).
RUN dotnet publish backend/src/Api/Flow4Tech.Api.csproj -c Release -o /out \
 && dotnet publish backend/src/Infrastructure/Migrator/Flow4Tech.Infrastructure.Migrator.csproj -c Release -o /out

# Local seed for the browser tests (docker compose --profile e2e): a separate target, so it never ships in the image that is deployed.
FROM build AS e2e-seed-build
RUN dotnet publish backend/tools/E2eSeed/Flow4Tech.E2eSeed.csproj -c Release -o /seed

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS e2e-seed
WORKDIR /seed
# Npgsql probes for Kerberos (libgssapi_krb5.so.2), absent from the aspnet runtime image; harmless noise but silenced here, in this local-only stage.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
COPY --from=e2e-seed-build /seed .
ENTRYPOINT ["dotnet", "Flow4Tech.E2eSeed.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e
WORKDIR /app
COPY --from=build /out .
# Versioned SQL migrations travel with the image (MIGRATIONS_DIR defaults to /app/migrations).
COPY backend/migrations/ migrations/
# Configuration by environment variables only: PORT (injected by the platform; 8080 when absent),
# DATABASE_URL or ConnectionStrings__Default (application credential), and for the migrator
# MIGRATION_DATABASE_URL (owner credential) + APP_DB_PASSWORD. See docs/runbooks/deploy.md.
# Documentation only: the host binds to $PORT when the platform injects it, 8080 otherwise.
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Flow4Tech.Api.dll"]
