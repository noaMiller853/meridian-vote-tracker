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
3. Start the API (applies migrations and seed data in Development, see Database setup): `dotnet run --project src/Meridian.Api`
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
4. Start the API: `dotnet run --project src/Meridian.Api`. In Development it applies migrations and loads the seed data by itself (see Startup). Without that, apply the migrations by hand:
   ```
   dotnet ef database update --project src/Meridian.Infrastructure --startup-project src/Meridian.Api
   ```

### Startup
Before the API accepts requests it:
1. applies pending migrations when `Database:MigrateOnStartup` is `true` (Development and docker compose). When it is `false` and migrations are pending, the API refuses to start;
2. adds the users Dana, Omar and Lena if they are missing (`system` comes from the first migration);
3. loads `Seed:MemberStatesPath` (Development: `../../seed/member_states.csv`, relative to `src/Meridian.Api`) into `countries` and `country_memberships`.

The seed load is idempotent. The file's sha256 is recorded as an import by `system` (shown as `member_states.csv (seed)`), and the same file is never loaded twice. A changed file only adds missing countries and periods; nothing is deleted or overwritten. Overlapping periods, `member_to` before `member_from`, or a period that clashes with one already stored stop the API with a message that names the lines.

If `Seed:MemberStatesPath` is empty the load is skipped, but the API refuses to start while the database has no countries. In environment variables the settings are `Database__MigrateOnStartup` and `Seed__MemberStatesPath`.

Add a migration after a model change:
```
dotnet ef migrations add <Name> --project src/Meridian.Infrastructure --startup-project src/Meridian.Api --output-dir Persistence/Migrations
```

## API
Every `/api` request names the acting user in the `X-User` header (`Dana`, `Omar` or `Lena`; the UI's user switcher sends it). Without a known user the API answers 401. An optional `X-Correlation-Id` header is echoed back and follows the request into the logs and the audit trail. Errors are ProblemDetails. In Development the OpenAPI document is at `/openapi/v1.json`.

| Method | Path | What it does |
|---|---|---|
| `POST` | `/api/imports` | Upload a CSV export (multipart field `file`, up to 250 MB). Returns 202 with the import id at once; processing runs in the background |
| `GET` | `/api/imports/{id}` | One import: status (Queued, Running, Succeeded, Failed), phase, rows done / total, error, counts, report |
| `GET` | `/api/imports` | Import history, newest first (`?take=`) |
| `GET` | `/health` | Liveness, no user needed |

```
curl -H "X-User: Dana" -F "file=@assembly_export_2024_q1.csv" http://localhost:5284/api/imports
curl -H "X-User: Dana" http://localhost:5284/api/imports/2
```

Uploaded files are kept in `FileStore:LocalRoot` (Development: `data/uploads` at the repository root, git-ignored), named by their sha256. Import settings: `Imports:PollSeconds`, `Imports:ProgressIntervalMs`, `Imports:CommandTimeoutSeconds`.

## Tests
`dotnet test`. Integration tests start a real PostgreSQL through Testcontainers, so Docker must be running. The test that streams the 100 MB historic file looks for `assembly_historic.csv` in `MERIDIAN_DATA_DIR`, then in `data/`, then next to the repository folder, and is skipped when it is not found.

## Seed data
- Countries and membership periods: `seed/member_states.csv` (150 countries, 151 periods; Iceland left and came back, so it has two), loaded on startup.
- Users: Dana (Analyst), Omar (Analyst), Lena (Admin), added on startup. Switch user from the menu at the top of the screen.

## Configuration and secrets
No secrets in the repository. Local values live in `.env` or `dotnet user-secrets`. Azure resources are connected through configuration only (see `.env.example`).

TODO: list the Azure settings that are used.

## Documents
- `DECISIONS.md`: decisions, trade-offs, scoring rules
- `docs/DATABASE.md`: database schema
- `AI_USAGE.md`: AI tools used and what was corrected
- `CLAUDE.md`: project instructions for Claude Code
