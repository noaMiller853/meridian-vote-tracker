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
3. Apply migrations and start the API: `dotnet run --project src/Meridian.Api`
4. Start the web app: `cd src/web && ng serve`

TODO: replace steps 2 to 4 with a single `docker compose up` once the API and web services are in the compose file.

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
