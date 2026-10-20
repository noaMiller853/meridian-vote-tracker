# Meridian Vote Tracker

Home assignment: system for ingesting Assembly voting exports, resolving conflicts, and publishing reproducible Alignment Index snapshots.
Stack: ASP.NET Core, PostgreSQL, Angular, Docker Compose.

## Prerequisites
- Docker Desktop (running). This is all you need to run the system.
- For development without Docker: .NET SDK 9 and Node.js 22.22.3 or newer (or 24.15+), as Angular 22 requires.

## Run
One command starts everything (PostgreSQL, the API and the web client):

```
docker compose up --build
```

| What | Address |
|---|---|
| Web client (Angular, served by nginx) | http://localhost:4200 |
| API | http://localhost:5284 (`/health`) |
| Swagger UI and OpenAPI document (Development) | http://localhost:5284/swagger , http://localhost:5284/openapi/v1.json |

The API applies the migrations and loads the countries and users by itself on start, so a fresh machine needs no manual step. The web client sends `/api` to the API through nginx, so no CORS is needed. Uploaded files are kept in the Docker volume `meridian-uploads`, the database in `meridian-db-data`. `docker compose down -v` deletes both.

Optional: copy `.env.example` to `.env` to change the database name, user, password or port.

### Development without Docker for the API or the web client
```
docker compose up -d db                # only the database
dotnet run --project src/Meridian.Api  # API on http://localhost:5284 (see Database setup)
cd src/web
npm install
npm start                              # http://localhost:4200, /api is proxied to the API
```

### Run against Azure Database for PostgreSQL (not verified)
This path was tried and could not be completed: the Azure server's firewall had no rule for my IP and my account could not add one (DECISIONS.md, section 4). The steps below are what the code supports; the local run above is the one that was tested.
Nothing secret is stored in the repository. Everyone who runs this puts their own values in `.env` (git-ignored; copy `.env.example`):

```
AZURE_PG_HOST=<server name>.postgres.database.azure.com
AZURE_PG_DB=postgres
AZURE_PG_USER=<server admin login>
AZURE_PG_PASSWORD=<password>
```

Then:

```
docker compose -f docker-compose.yml -f docker-compose.azure.yml up --build
```

If the server uses Microsoft Entra authentication (the Connect page of the server shows `PGPASSWORD=$(az account get-access-token ...)`), the user is your Entra account and the password is a token that expires after about an hour. Do not store it in `.env`; set it in the terminal instead (Azure CLI: `az login` once):

```
$env:AZURE_PG_HOST = '<server name>.postgres.database.azure.com'
$env:AZURE_PG_USER = '<name@tenant.onmicrosoft.com>'
$env:AZURE_PG_PASSWORD = az account get-access-token --resource https://ossrdbms-aad.database.windows.net --query accessToken --output tsv
docker compose -f docker-compose.yml -f docker-compose.azure.yml up --build
```

The API applies the migrations and loads the countries and users in that database on start. The Azure server must allow your IP in its firewall (portal > the server > Networking). Without the Azure variables, `docker compose up --build` uses the local database as before.

Without Docker for the API, set the same connection as environment variables in the terminal: `$env:ConnectionStrings__Default = 'Host=...;Port=5432;Database=postgres;Username=...;Password=...;Ssl Mode=Require'`.

## Web client (`src/web`)
Angular 22 with the four screens of the assignment: Imports, Review, Votes, Snapshots. The user switcher at the top sends the `X-User` header.

The API client is not written by hand: `openapi-typescript` generates the types from the API's OpenAPI document and `openapi-fetch` uses them, so the compiler checks every path, parameter and body. After any API change:

```
cd src/web
npm run api      # downloads http://localhost:5284/openapi/v1.json, regenerates src/app/api/schema.d.ts
npm run build    # fails where a screen no longer matches the contract
npm test         # unit tests
```

`npm run build` and `npm test` need Node.js 22.22.3+ (Angular 22). With an older Node, run them in the image the web Dockerfile uses (PowerShell, from `src/web`):

```
docker run --rm -v "${PWD}:/app" -v meridian-web-node-modules:/app/node_modules -w /app node:24-alpine sh -c "npm ci && npx ng build && npx ng test --watch=false"
```

More in `src/web/README.md`.

## Database setup
The API reads the connection string `ConnectionStrings:Default` from configuration and fails at startup if it is missing. It is never in code or in `appsettings*.json`.

1. Restore the local tools (pins `dotnet-ef`): `dotnet tool restore`
2. Start PostgreSQL: `docker compose up -d db` (host port is `POSTGRES_PORT` from `.env`, default 5432). The port is bound to `127.0.0.1` only, and the default password in `docker-compose.yml` and `.env.example` is for local development only.
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
Every `/api` request names the acting user in the `X-User` header (`Dana`, `Omar` or `Lena`; the UI's user switcher sends it). Without a known user the API answers 401. An optional `X-Correlation-Id` header is echoed back and follows the request into the logs and the audit trail. Errors are ProblemDetails (`application/problem+json`); validation errors are listed under the request field name. In Development the OpenAPI document is at `/openapi/v1.json`, and a Swagger UI on top of it is at `/swagger`: press **Authorize**, enter `Dana` as the `X-User` value, then use **POST /api/imports → Try it out** to upload a CSV from the browser. Neither the document nor the UI is served outside Development.

| Method | Path | What it does |
|---|---|---|
| `POST` | `/api/imports` | Upload a CSV export (multipart field `file`, up to 250 MB). Returns 202 with the import id at once; processing runs in the background |
| `GET` | `/api/imports/{id}` | One import: status (Queued, Running, Succeeded, Failed), phase, rows done / total, error, counts, report |
| `GET` | `/api/imports` | Import history, newest first (`?take=`) |
| `GET` | `/api/imports/{id}/report` | Import report: counts per class, conflicts opened / already waiting / already rejected / already superseded, normalizations by field, empty optional fields, votes created or in contradiction, warnings |
| `GET` | `/api/imports/{id}/rows` | Rows with class, reason and original value. Default: flagged rows (Invalid, Duplicate, Contradiction, Conflict); `class=All` or e.g. `class=Invalid,Conflict`; `vote=`; paged with `after=<line>` and `take` (max 500) |
| `GET` | `/api/imports/{id}/votes` | What happened in each vote (`status=`, paged with `after=<vote id>`) |
| `GET` | `/api/imports/{id}/vote?assemblyVoteId=MA/2024/025` | One vote of the import with all its rows |
| `GET` | `/api/conflicts` | The review queue: Pending by default (`status=Approved`, `Rejected` or `All`), `assemblyVoteId=`, `kind=CountryVote` or `VoteField`; paged with `afterId=` and `take` (max 500), with the total |
| `GET` | `/api/conflicts/{id}` | One conflict: approved and new value side by side with the source of each (file, uploaded by, when), the approved value now (`current.isStale`) and the decision |
| `POST` | `/api/conflicts/{id}/decision` | Analyst or Admin. `{"decision": "Approve" or "Reject", "note": "...", "expectedApprovedValue": "..."}`. 409 when the conflict was already decided (who, when, what) or, for Approve, when the approved value changed since it was opened (resend with `expectedApprovedValue` = the current value) |
| `GET` | `/api/votes` | Votes by id: `assemblyVoteId=` (exact), `text=` (title or Assembly id), `session=`, `dateFrom=` / `dateTo=` (inclusive), `hasPosition=true` or `false`; paged with `afterId=` and `take` (max 500), with the total. A vote without a position has `hasPosition: false` and `position: null` (POS-04) |
| `GET` | `/api/votes/{id}` | One vote: details with their source, country votes (source file and line, approved by, `changedByDecision`), position, pending conflicts |
| `GET` | `/api/votes/{id}/timeline` | Everything that happened in the vote, oldest first: imports that contained it, vote created, countries added, conflicts opened and decided, values changed, position changes; with who, when, source file, previous and new value |
| `PUT` | `/api/votes/{id}/position` | Admin only. `{"stance": "For" or "Against", "weight": 1-5, "expectedVersion": 0}`: send `0` for the first position, otherwise `position.version` as read. 409 with the current position if someone changed it since; the same values again return `changed: false` and record nothing |
| `POST` | `/api/snapshots` | Analyst or Admin. `{"name": "...", "dateFrom": "...", "dateTo": "...", "session": 79, "assemblyVoteIds": [...], "excludeVotesWithoutPosition": false}`: filter and list are a union. Returns 202; the calculation runs in the background. Votes without a position give 409 listing them, unless `excludeVotesWithoutPosition` is true (then they are listed as excluded) |
| `GET` | `/api/snapshots` | Snapshots, newest first, with status, phase and progress, quarter, published by / at |
| `GET` | `/api/snapshots/{id}` | Status and progress; then the rules used, the selection, the excluded votes with their reason, and the ranking (score, "N of M", weights, counts per outcome) |
| `GET` | `/api/snapshots/{id}/countries/{code}` | How one country's score was calculated: each included vote with ballot, position, weight, outcome, counted and matched; paged with `afterOrdinal=` and `take` (max 500) |
| `GET` | `/api/snapshots/{id}/votes` | The frozen votes (`included=true` or `false`), paged with `afterVoteId=` |
| `PUT` | `/api/snapshots/{id}/publish` | Admin only. Publishes a Saved snapshot for its quarter (the quarter of its latest included vote). 409 if the quarter has one; `{"replace": true}` publishes this one instead and returns the other to Saved |
| `GET` | `/health` | Liveness, no user needed |

```
curl -H "X-User: Dana" -F "file=@assembly_export_2024_q1.csv" http://localhost:5284/api/imports
curl -H "X-User: Dana" http://localhost:5284/api/imports/2
curl -H "X-User: Dana" "http://localhost:5284/api/imports/2/rows?class=Invalid"
curl -H "X-User: Dana" "http://localhost:5284/api/conflicts?assemblyVoteId=MA/2024/009"
curl -H "X-User: Omar" -H "Content-Type: application/json" -d "{\"decision\":\"Approve\",\"note\":\"Checked the sheet\"}" http://localhost:5284/api/conflicts/1/decision
curl -H "X-User: Dana" "http://localhost:5284/api/votes?hasPosition=false&session=79"
curl -H "X-User: Lena" -X PUT -H "Content-Type: application/json" -d "{\"stance\":\"For\",\"weight\":4,\"expectedVersion\":0}" http://localhost:5284/api/votes/1/position
curl -H "X-User: Dana" http://localhost:5284/api/votes/1/timeline
curl -H "X-User: Dana" -H "Content-Type: application/json" -d "{\"name\":\"2024 Q1\",\"dateFrom\":\"2024-01-01\",\"dateTo\":\"2024-03-31\"}" http://localhost:5284/api/snapshots
curl -H "X-User: Dana" http://localhost:5284/api/snapshots/1/countries/BRA
curl -H "X-User: Lena" -X PUT http://localhost:5284/api/snapshots/1/publish
```

Line numbers in reports are lines in the file (the header is line 1). Recommended order for the provided files: q1, then rev2 (opens 6 ballot and 2 vote-field conflicts), then the historic file (70 to 90 s for 962,171 lines).

Uploaded files are kept in `FileStore:LocalRoot` (Development: `data/uploads` at the repository root, git-ignored), named by their sha256. Import settings: `Imports:PollSeconds`, `Imports:ProgressIntervalMs`, `Imports:CommandTimeoutSeconds`; the same three under `Snapshots:` for the snapshot worker. A snapshot of the full history takes about 7 s.

## Tests
`dotnet test`. Integration tests start a real PostgreSQL through Testcontainers, so Docker must be running. The test that streams the 100 MB historic file looks for `assembly_historic.csv` in `MERIDIAN_DATA_DIR`, then in `data/`, then next to the repository folder, and is skipped when it is not found.

## Seed data
- Countries and membership periods: `seed/member_states.csv` (150 countries, 151 periods; Iceland left and came back, so it has two), loaded on startup.
- Users: Dana (Analyst), Omar (Analyst), Lena (Admin), added on startup. Switch user from the menu at the top of the screen.

## Configuration and secrets
No secrets in the repository. Local values live in `.env` or `dotnet user-secrets`; in Docker and Azure the settings are environment variables (`ConnectionStrings__Default`, `Database__MigrateOnStartup`, `Seed__MemberStatesPath`, `FileStore__LocalRoot`).

Azure: the system runs fully locally and does not use any Azure resource yet. The connection string is the only setting needed to point the API at Azure Database for PostgreSQL. `AZURE_STORAGE_CONNECTION_STRING` and `APPLICATIONINSIGHTS_CONNECTION_STRING` in `.env.example` are placeholders; nothing reads them yet (see DECISIONS.md, section 4).

## Documents
- `DECISIONS.md`: decisions, trade-offs, scoring rules (2 pages)
- `docs/DECISIONS-DETAIL.md`: the long working notes behind it
- `docs/DATABASE.md`: database schema
- `AI_USAGE.md`: AI tools used and what was corrected
- `CLAUDE.md`: project instructions for Claude Code
