# Container deployment

How BookSpace is packaged as a single container image, run locally with Podman, and deployed to Azure
Container Apps. See the top-level [README.md](../README.md) for the non-container (plain `dotnet run` +
`ng serve`) way of running the app - this document is only about the containerized path.

## Concepts, briefly

- **Dockerfile** - a recipe: a sequence of steps ("start from this base image, copy these files, run
  this command, ...") that produces an **image**. This repo's [Dockerfile](../Dockerfile) is
  multi-stage: an Angular build stage, a .NET publish stage, and a final runtime stage that only
  contains the two previous stages' *output*, not their tools or source.
- **Image** - the immutable, built result of running a Dockerfile: a filesystem snapshot plus metadata
  (entrypoint, exposed port, labels). Building the same Dockerfile again produces a new image; nothing
  about "running" it yet.
- **Container** - a running instance of an image - an actual process, isolated in its own filesystem/
  network namespace. You can start several containers from the same image at once; each is independent.

## Why Angular and the API share one image

The frontend's production config (`frontend/src/environments/environment.ts`) already uses
`apiUrl: ''` - every API call is relative to whatever origin served the page. `Program.cs` serves the
Angular production build directly from ASP.NET's `wwwroot` (via `UseStaticFiles`/`MapFallbackToFile`),
so the browser only ever talks to one origin, with no CORS to configure between "the app" and "the
API" because there is no second origin. This also means the production container needs no reverse
proxy in front of it for that purpose - Azure Container Apps' own ingress is the only thing in front of
it (see "Reverse proxy and Azure ingress" below).

## Local prerequisites

- [Podman Desktop](https://podman-desktop.io/) (or the Podman CLI + a running `podman machine`) - this
  was verified against Podman 6.0.2 with `podman machine` in the `Currently running` state, and
  `podman compose` shelling out to Docker Compose v2 (`docker-compose.exe`) as its compose provider.
- Nothing else - the SQL Server container and the app container are both built/pulled by Compose; no
  local .NET SDK, Node, or SQL Server install is required for this path (they're still required for the
  non-container `dotnet run`/`ng serve` workflow in the main README).

## Running locally

```bash
cp .env.example .env
# edit .env: set a real DB_SA_PASSWORD and AUTH_SIGNING_KEY

podman build -t bookspace:local .          # optional - compose also builds this itself, see below
podman compose -f compose.local.yaml up --build -d
```

`.env` is read automatically by `podman compose`/`docker-compose` from the current directory and is
never committed (see `.gitignore`/`.dockerignore`) - `.env.example` documents the variable *names* only,
never a real secret.

Inspecting what's running:

```bash
podman compose -f compose.local.yaml ps
podman compose -f compose.local.yaml logs -f bookspace-app
podman images
podman volume ls
```

Test it:

```bash
curl http://localhost:8080/health           # liveness - always 200 once the process is up
curl http://localhost:8080/health/db        # readiness - 200 once bookspace-db is reachable
```

Then open `http://localhost:8080` in a browser - this is the Angular app, served by the same container/
port as the API. `ASPNETCORE_ENVIRONMENT=Development` is set for the local app container specifically
(see `compose.local.yaml`), so the existing dev-only migrate+seed path runs automatically on first
start, exactly like `dotnet run` does locally - the seeded accounts and password documented in the main
[README.md](../README.md)/[authentication.md](authentication.md) work here too. A direct refresh on a
deep link (e.g. `http://localhost:8080/login`) returns the Angular app, not a 404 - this is what
`MapFallbackToFile` in `Program.cs` exists for.

Stopping:

```bash
podman compose -f compose.local.yaml down       # stops containers, KEEPS the bookspace-sql-data volume
```

Destroying the local database volume is a separate, deliberately explicit, destructive command:

```bash
podman compose -f compose.local.yaml down -v    # DESTROYS bookspace-sql-data - all local data is gone
```

## The multi-stage Dockerfile

1. **`frontend-build`** (`node:22-alpine`) - `npm ci` against the existing `package-lock.json`, then
   `npm run build` (production by default - see `angular.json`'s `defaultConfiguration`). Output lands
   at `dist/bookspace-web/browser` (the actual directory the `@angular/build:application` builder
   produces - verified locally rather than assumed, since some Angular builders nest browser output
   under a `browser/` subfolder and some don't).
2. **`backend-build`** (`mcr.microsoft.com/dotnet/sdk:10.0`) - `.csproj` files are copied and restored
   *before* the rest of the source, so `dotnet restore`'s layer is cached across rebuilds that only
   change `.cs` files. `dotnet publish -c Release` targets `BookSpace.Api.csproj` specifically, which
   pulls in `BookSpace.Application`/`BookSpace.Infrastructure`/`BookSpace.Domain` as project references
   but never touches the test projects (they're not referenced by `BookSpace.Api.csproj` at all).
3. **`final`** (`mcr.microsoft.com/dotnet/aspnet:10.0` - the ASP.NET *runtime* image, not the SDK) - only
   `COPY --from=` the previous two stages' *outputs* (the publish folder, the Angular browser build into
   `wwwroot`). No SDK, no Node, no source, no test projects ever exist in this final image. Runs as the
   base image's built-in non-root `$APP_UID` user; listens on `8080` via `ASPNETCORE_URLS=http://+:8080`;
   entrypoint is `dotnet BookSpace.Api.dll`. Carries an
   `org.opencontainers.image.source` label pointing at this repository.

## How Angular ends up in ASP.NET's wwwroot

Purely a Dockerfile `COPY --from=frontend-build /src/frontend/dist/bookspace-web/browser ./wwwroot` in
the final stage - nothing Angular-specific happens in .NET code beyond `Program.cs`'s ordinary
`UseDefaultFiles()`/`UseStaticFiles()`/`MapFallbackToFile("index.html")`. Locally (outside a container),
`backend/BookSpace.Api/wwwroot` doesn't exist - that's expected and unchanged: local development still
runs the Angular dev server (`ng serve`, port 4200) and the API (`dotnet run`, port 5185) as two
separate processes, exactly as documented in the main README.

## Local migrations and seeding vs. production migrations

- **Local / `dotnet run` / the local Compose app container** (`ASPNETCORE_ENVIRONMENT=Development`):
  unchanged - `Program.cs` calls `MigrateAndSeedDevelopmentDatabaseAsync()`, which applies pending EF
  Core migrations and then runs `DevelopmentSeeder` (the known `admin@acme.test` / `Passw0rd!`-style
  accounts). This never runs outside Development, and never will - see the next section.
- **Production** (or any non-Development environment): migrations are opt-in via
  `Database__ApplyMigrationsOnStartup=true` (`DatabaseOptions`, `MigrateProductionDatabaseAsync`), which
  calls `Database.MigrateAsync()` and **never** calls `DevelopmentSeeder` - the two paths are separate
  methods, not the same method with an environment check inside it. Left unset (the default), a
  production container starts without touching schema at all - useful for a redeploy where you already
  know the schema is current, or where you'd rather apply migrations as a separate, explicit step.
  **`dotnet ef database update`** remains available too, for anyone who prefers applying migrations from
  outside the running container (see the main README's "Database setup" section).

  Because two replicas both running `Database.MigrateAsync()` at the same moment against the same
  database is not guarded against anywhere in this codebase, **keep the Container App's `maxReplicas` at
  1** for as long as `Database__ApplyMigrationsOnStartup=true` stays enabled. This is a real constraint
  of this demo's startup-migration approach, not a general recommendation - scale past 1 replica only
  after either turning the flag off (apply migrations out-of-band before scaling) or adding real
  migration-coordination (out of scope here).

## Production bootstrap (first tenant + first TenantAdmin)

No existing mechanism creates a tenant or an admin user outside of `DevelopmentSeeder` (Development-only)
and this new bootstrap path - `ITenantRepository` is deliberately read-only/narrow (see its own comment:
"Full Tenant CRUD is out of scope"), and there is no `POST /tenants`-style endpoint anywhere in the API.

`ProductionBootstrapper` (in `BookSpace.Infrastructure/Persistence`) is the production counterpart to
`DevelopmentSeeder`: it creates exactly one `Tenant` and exactly one `User` holding the `TenantAdmin`
role, using the same domain entities, the same `IPasswordHasher`, and the same tenant-isolation design
(`IgnoreQueryFilters()` for the one pre-tenant-context lookup it needs, the same sanctioned exception
`UserRepository.FindByEmailAsync` already uses). It is:

- **Off by default** - only runs when `Bootstrap__Enabled=true`.
- **Idempotent** - if a user with the configured `Bootstrap__AdminEmail` already exists (anywhere - a
  global, cross-tenant check, since email is globally unique), it logs that it's skipping and does
  nothing further. A restart or redeploy with the same configuration never creates a second tenant/admin.
- **Validated** - if enabled but any of `TenantName`, `DefaultTimeZoneId`, `AdminFirstName`,
  `AdminLastName`, `AdminEmail`, `AdminPassword` is missing, it throws (failing startup loudly, logged
  via `Log.Fatal` in `Program.cs`'s outer catch) rather than proceeding with a partial/guessed value.
- **Sequenced after migrations** - `Program.cs` calls `BootstrapProductionAdminAsync()` only after the
  Development or production migration step above has already run.

Configuration (environment variables, `Bootstrap__` prefix):

```text
Bootstrap__Enabled=true
Bootstrap__TenantName=...
Bootstrap__DefaultTimeZoneId=Europe/Sarajevo
Bootstrap__AdminFirstName=...
Bootstrap__AdminLastName=...
Bootstrap__AdminEmail=...
Bootstrap__AdminPassword=...
```

**After the first successful deployment**, set `Bootstrap__Enabled=false` and remove the
`Bootstrap__AdminPassword` secret from Azure (see the secrets list below) - the idempotency check means
leaving it `true` is not by itself dangerous, but there's no reason to keep a plaintext admin-password
secret provisioned once it's no longer needed.

## Reverse proxy and Azure ingress

Azure Container Apps terminates HTTPS at its own ingress and forwards plain HTTP to the container. Set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in the Container App's configuration so `Program.cs` applies
`X-Forwarded-For`/`X-Forwarded-Proto` (via `UseForwardedHeaders`, the very first middleware in the
pipeline) - without this, the app would see every request as plain HTTP and `UseHttpsRedirection` would
redirect an already-HTTPS request, forever. `KnownNetworks`/`KnownProxies` are cleared when this is
enabled: Container Apps' ingress is the only network path that can reach the container (it is never
exposed directly to the internet), so its address isn't a fixed value that could be listed instead, and
trusting it is not the same risk it would be for a container reachable directly from arbitrary clients.
This flag defaults to (and must stay) unset/false for local Compose and any non-Container-Apps
deployment reachable directly.

## Container logging

`Logging__WriteStructuredFile` (`appsettings`/env var) now defaults to `false` **everywhere**, including
Production - a non-root container user has no write access to `/app/logs` unless something is explicitly
turned on to use it, and Azure Container Apps already collects stdout/stderr, which the console sink is
never disabled. Set `Logging__WriteStructuredFile=true` explicitly for a non-container deployment that
wants the compact-JSON file store back (the directory is still created and owned by the non-root user in
the image, so turning this on doesn't fail with a permissions error). Correlation IDs and the console
sink are unchanged either way.

## Health endpoints

- `GET /health` - liveness. Never touches the database; 200 once the process is up. Anonymous.
- `GET /health/db` - readiness. Awaits a real `Database.CanConnectAsync()`; 200 `{"status":"connected"}`
  when reachable, 503 `{"status":"unreachable"}` otherwise. Never echoes the configured database name.
  Anonymous.

## Azure SQL connection resilience

`UseSqlServer` is configured with `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 10s)` - a
serverless Azure SQL database can be paused and need a moment to resume, so the first connection after
idle can transiently fail; this retries that specific class of failure automatically rather than
surfacing it as a hard error on the very first request after a quiet period. It's bounded, not infinite -
a genuinely misconfigured or down database still fails, just not on the first transient blip. The same
retry policy is also what gives `compose.local.yaml`'s `bookspace-app` resilience against
`bookspace-db` still starting up, independent of whether `depends_on.condition: service_healthy` is
honored by a given Compose provider/version.

## Azure environment variables and secrets

Non-secret configuration (Container App environment variables):

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
Database__ApplyMigrationsOnStartup=true        # true only until the first successful deploy's schema is in place; see maxReplicas note above
Auth__Issuer=BookSpace
Auth__Audience=BookSpace
Auth__AccessTokenMinutes=15
Auth__RefreshTokenDays=14
Cors__AllowedOrigins__0=https://<your-container-app-fqdn>
Bootstrap__Enabled=true                        # false after the first successful deploy
Bootstrap__TenantName=...
Bootstrap__DefaultTimeZoneId=...
Bootstrap__AdminFirstName=...
Bootstrap__AdminLastName=...
Bootstrap__AdminEmail=...
```

Must be Azure **secrets** (never plain environment variables, never committed anywhere):

```text
ConnectionStrings__BookSpace        # Azure SQL Database connection string
Auth__SigningKey                    # JWT signing key
Bootstrap__AdminPassword            # only while Bootstrap__Enabled=true; remove after first deploy
```

- `ASPNETCORE_ENVIRONMENT=Development` must never be used in Azure - it enables
  `MigrateAndSeedDevelopmentDatabaseAsync()`, which would seed the well-known `Passw0rd!` test accounts
  into a real database.
- `DevelopmentSeeder` is never called from any non-Development code path - there is no configuration
  flag that can turn it on outside Development.
- The SQL Server container in `compose.local.yaml` is **local-only**. Azure uses **Azure SQL Database**
  (serverless or provisioned, your choice) - nothing in this repository runs SQL Server itself in Azure.

## Verification performed

See the final report in the pull request/commit history for exactly what was run and its results, and
what could not be verified in this environment (if anything) with the exact commands to run locally.
