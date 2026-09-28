# BookSpace

Multi-tenant resource booking platform. Organizations (tenants) publish bookable resources with
availability rules, and members create one-off or recurring bookings against live availability with a
collision-free guarantee.

See [docs/](docs/README.md) for architecture, tenant isolation, authentication, resource lifecycle,
and availability documentation, and [AI-USAGE.md](AI-USAGE.md) for how AI assistance was used while
building this project.

## Repository layout

```
backend/
  BookSpace.Api             presentation - controllers, middleware, composition root
  BookSpace.Application     use cases - mediator, commands/queries & handlers, pipeline behaviors
  BookSpace.Domain          entities, enums, tenancy contracts - no framework dependencies
  BookSpace.Infrastructure  EF Core DbContext, migrations, external service implementations
frontend/   Angular web client (bookspace-web)
```

## Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/) and npm
- SQL Server (LocalDB on Windows, or a containerized instance) — see [Database setup](#database-setup)

## Running the backend

```bash
cd backend
dotnet restore
dotnet build
dotnet run --project BookSpace.Api
```

The API listens on the URL printed in the console (see `backend/BookSpace.Api/Properties/launchSettings.json`).

## Running the frontend

```bash
cd frontend
npm install
npm start
```

The app is served at `http://localhost:4200` by default.

## Database setup

The backend expects a SQL Server instance reachable via the `ConnectionStrings:BookSpace` value in
`backend/BookSpace.Api/appsettings.Development.json`. On Windows, SQL Server LocalDB is the simplest option:

```bash
sqllocaldb info          # confirms an instance (e.g. MSSQLLocalDB) exists
sqllocaldb start MSSQLLocalDB
```

The default connection string points at `(localdb)\MSSQLLocalDB`. In Development, the API applies
pending migrations and seeds a sample dataset automatically on startup. To apply migrations manually
(e.g. against a containerized SQL Server instead), run from `backend/`:

```bash
dotnet ef database update --project BookSpace.Infrastructure --startup-project BookSpace.Api
```

## Container deployment

The app can also be built and run as a single container (Angular served by ASP.NET from `wwwroot`),
locally via Podman Compose or deployed to Azure Container Apps. See
[docs/container-deployment.md](docs/container-deployment.md).

## Branching

- `master` — always deployable; nothing is committed here directly.
- `dev` — integration branch for completed work packages.
- `feature/<short-description>` — one branch per unit of work, opened as a PR into `dev`.
