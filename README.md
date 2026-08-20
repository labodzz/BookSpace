# BookSpace

Multi-tenant resource booking platform. Organizations (tenants) publish bookable resources with
availability rules, and members create one-off or recurring bookings against live availability with a
collision-free guarantee.

See [AI-USAGE.md](AI-USAGE.md) for how AI assistance was used while building this project.

## Repository layout

```
backend/    .NET Web API (BookSpace.Api)
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

The default connection string points at `(localdb)\MSSQLLocalDB`. To use a containerized SQL Server
instead, update the connection string to point at your container and confirm connectivity with:

```bash
dotnet ef database update --project backend/BookSpace.Api
```

(once the data model and migrations exist).

## Branching

- `master` — always deployable; nothing is committed here directly.
- `dev` — integration branch for completed work packages.
- `feature/<short-description>` — one branch per unit of work, opened as a PR into `dev`.
