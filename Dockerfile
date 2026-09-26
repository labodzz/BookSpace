# syntax=docker/dockerfile:1
#
# Builds one production image: an ASP.NET Core API that also serves the Angular production build from
# its own wwwroot (see Program.cs's UseStaticFiles/MapFallbackToFile wiring and
# docs/container-deployment.md). Three stages - Angular build, .NET publish, runtime - so the final
# image contains neither Node, the .NET SDK, source code, nor the test projects.
#
# Build from the repository root:
#   podman build -t bookspace:local .

# ---- Stage 1: Angular production build -----------------------------------------------------------
FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend

# package.json + the existing lock file only, so this layer (and npm ci) is cached across rebuilds
# that don't touch dependencies.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

# ---- Stage 2: .NET publish -------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

# Project files only, for the same restore-layer-caching reason as npm ci above.
COPY backend/BookSpace.Domain/BookSpace.Domain.csproj backend/BookSpace.Domain/
COPY backend/BookSpace.Application/BookSpace.Application.csproj backend/BookSpace.Application/
COPY backend/BookSpace.Infrastructure/BookSpace.Infrastructure.csproj backend/BookSpace.Infrastructure/
COPY backend/BookSpace.Api/BookSpace.Api.csproj backend/BookSpace.Api/
RUN dotnet restore backend/BookSpace.Api/BookSpace.Api.csproj

COPY backend/BookSpace.Domain/ backend/BookSpace.Domain/
COPY backend/BookSpace.Application/ backend/BookSpace.Application/
COPY backend/BookSpace.Infrastructure/ backend/BookSpace.Infrastructure/
COPY backend/BookSpace.Api/ backend/BookSpace.Api/
RUN dotnet publish backend/BookSpace.Api/BookSpace.Api.csproj -c Release -o /app/publish --no-restore

# ---- Stage 3: runtime -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
LABEL org.opencontainers.image.source="https://github.com/labodzz/BookSpace"

WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=backend-build /app/publish .
COPY --from=frontend-build /src/frontend/dist/bookspace-web/browser ./wwwroot

# Non-root by default - the base image already defines a non-root "app" user/group at $APP_UID (see
# https://github.com/dotnet/dotnet-docker/blob/main/documentation/README.non-root-user.md). /app/logs
# only needs to exist/be writable for the opt-in Logging__WriteStructuredFile=true case (see Program.cs
# and appsettings) - the container's default (false) never writes there, but the directory is created
# and owned by that user anyway so turning the flag on doesn't fail on a missing/root-owned directory.
RUN mkdir -p /app/logs && chown -R $APP_UID:$APP_UID /app/logs
USER $APP_UID

ENTRYPOINT ["dotnet", "BookSpace.Api.dll"]
