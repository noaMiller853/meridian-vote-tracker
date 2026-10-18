# Meridian Vote Tracker

Home assignment: system for ingesting Assembly voting exports, resolving conflicts, and publishing reproducible Alignment Index snapshots.
Stack: ASP.NET Core, PostgreSQL, Angular, Docker Compose.

> Draft. Sections marked TODO are completed as the system is built.

## Prerequisites
- .NET SDK 8 or later
- Node.js and Angular CLI
- Docker Desktop (running)

## Run
1. Optional: copy `.env.example` to `.env`.
2. Start the database: `docker compose up -d db`
3. Apply migrations (see Database setup) and start the API: `dotnet run --project src/Meridian.Api`
4. Start the web app: `cd src/web && ng serve`

TODO: replace steps 2 to 4 with a single `docker compose up` once the API and web services are in the compose file.

## Database setup
The API reads the connection string `ConnectionStrings:Default` from configuration and fails at startup if it is missing. It is never in code or in `appsettings*.json`.

1. Restore the local tools (pins `dotnet-ef`): `dotnet tool restore`
2. Start PostgreSQL: `docker compose up -d db` (host port is `POSTGRES_PORT` from `.env`, default 5432)
3. Store the connection string for local runs in user-secrets (kept outside the repository). Use the port and credentials from your `.env`:
   ```
   dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=5432;Database=meridian;Username=meridian;Password=<password>" --project src/Meridian.Api
   ```
   ASP.NET Core does not read `.env` itself. In Docker, CI or Azure, set the environment variable `ConnectionStrings__Default` instead.
4. Apply the migrations:
   ```
   dotnet ef database update --project src/Meridian.Infrastructure --startup-project src/Meridian.Api
   ```

Add a migration after a model change:
```
dotnet ef migrations add <Name> --project src/Meridian.Infrastructure --startup-project src/Meridian.Api --output-dir Persistence/Migrations
```

## Tests
`dotnet test`. Integration tests start a real PostgreSQL through Testcontainers, so Docker must be running.

## Seed data
- Countries and membership periods: `seed/member_states.csv`, loaded on startup.
- Users: Dana (Analyst), Omar (Analyst), Lena (Admin). Switch user from the menu at the top of the screen.

## Configuration and secrets
No secrets in the repository. Local values live in `.env` or `dotnet user-secrets`. Azure resources are connected through configuration only (see `.env.example`).

TODO: list the Azure settings that are used.

## Documents
- `DECISIONS.md`: decisions, trade-offs, scoring rules
- `docs/DATABASE.md`: database schema
- `USAGE_AI.md`: AI tools used and what was corrected
- `CLAUDE.md`: project instructions for Claude Code
