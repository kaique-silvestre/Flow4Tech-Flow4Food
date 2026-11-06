FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/ backend/
# One image, two entrypoints: the API (default) and the deploy-time migrator (dotnet Flow4Tech.Migrator.dll).
RUN dotnet publish backend/src/Api/Flow4Tech.Api.csproj -c Release -o /out \
 && dotnet publish backend/src/Infrastructure/Migrator/Flow4Tech.Infrastructure.Migrator.csproj -c Release -o /out

# Local seed for the browser tests (docker compose --profile e2e): a separate target, so it never ships in the image that is deployed.
FROM build AS e2e-seed-build
RUN dotnet publish backend/tools/E2eSeed/Flow4Tech.E2eSeed.csproj -c Release -o /seed

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS e2e-seed
WORKDIR /seed
COPY --from=e2e-seed-build /seed .
ENTRYPOINT ["dotnet", "Flow4Tech.E2eSeed.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0
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
