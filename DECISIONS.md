# DECISIONS

Detailed notes behind every rule: [docs/DECISIONS-DETAIL.md](docs/DECISIONS-DETAIL.md). Tables and constraints: [docs/DATABASE.md](docs/DATABASE.md).

## 1. Key decisions and trade-offs
- **The back end owns the rules.** ASP.NET Core 9, EF Core + Npgsql for the domain and migrations, bulk `COPY` for large files (plain EF is too slow and memory-hungry for about a million rows). Validation and classification are pure Domain code with unit tests, not SQL.
- **Ingestion is deterministic, no AI.** Every row has one class (New, Unchanged, Duplicate, Invalid, Contradiction, Conflict) that can be explained and repeated. An approved value is never overwritten by a process; only a person's decision changes it.
- **Strict reading of the format.** Dates must be `yyyy-mm-dd` (Appendix A), ballots exactly `Y/N/A/X`. The only normalizations are trim and upper-case for country code and ballot, and one space in titles. Each is counted and shown in the report (ING-13). Nothing is guessed (`YES` is Invalid).
- **A bad row never spoils the vote.** Vote details come from valid rows only, so an invalid row cannot create a Contradiction. Contradiction: all valid rows of that vote are rejected, because choosing a majority would be choosing a value.
- **Re-uploads are idempotent** (ING-12): same file, same result, no new conflict while one waits. A conflict already rejected, or a value already replaced by a decision, from the same file (same sha256) is not opened again.
- **Concurrency by the database.** A conflict decision is one conditional `UPDATE ... WHERE status='Pending'` in one transaction; the loser gets 409 with who decided and the current state (CON-05). A stale conflict needs an explicit `expectedApprovedValue`. Positions use a version number (`expectedVersion`), so two admins cannot overwrite each other.
- **One transaction per import.** No half-imported file. Cost: a failure late in the historic file loses that work and the file is uploaded again. Measured: about 70-90 s for 962,171 lines, peak memory about 350 MB. Fallback: one transaction per batch of votes.
- **Progress is a table plus polling** (every second), not SignalR. The database is the source of truth and progress survives a page refresh.
- **Front end.** Angular, standalone components and signals. The API client is `openapi-fetch` typed by `openapi-typescript` from the OpenAPI document (`npm run api`), so the compiler checks the contract. The browser talks to one origin (dev proxy, nginx in Docker), so no CORS. Validation errors (400) show beside the field, 409 as a structured message. The screens in Appendix B are not binding; where they differ, the requirements decide.
- **Users.** `X-User` header with preset users (section 3 of the assignment). Roles are enforced in the API: only Admin sets positions and publishes. `X-Correlation-Id` goes from the UI through the API into logs, jobs and audit events.

## 2. Scoring rules (SNP)
Score = 100 × (sum of matching weights) ÷ (sum of counted weights), stored with 6 decimals, shown with one.
- Only `Y` and `N` are counted. Abstain (`A`) and absent (`X`) count neither in the numerator nor in the denominator.
- A vote where the country was not a member on the vote date is not counted. A member with no approved ballot is not counted.
- A country with no counted vote gets no score (`n/a`) and no rank, not 0.
- Ranking: highest exact score first, ties by country name, positional ranks.
- A vote without a position never enters silently (POS-04): creating the snapshot answers 409 listing them; the creator can confirm excluding them, and the snapshot lists them as excluded.
- Every snapshot detail shows these rules and the formula for each country.

## 3. Database and history
- Approved data: `votes`, `country_votes`, `vote_positions`. Each approved value keeps its source import and line. Foreign keys are `RESTRICT`, enum columns are `text` with CHECK constraints, schema by EF Core migrations.
- **History (HIS):** one `audit_events` table (entity, action, old and new value, actor, source import, correlation id, time) feeds the vote timeline and answers every HIS question with one query. Trade-off: JSON values are less strictly typed than a versions table per entity. Replaced values are kept (on the conflict and in the audit trail); nothing is deleted.
- Every import is stored: the file (hash-addressed), who, when, the report, and one `import_rows` row per line with class, reason and original value.
- **Snapshots are frozen** (SNP-04): per country, two strings with one character per vote (the ballots and the outcome), plus positions, weights, scores and the rules used. Nothing is recalculated on read. A full-history snapshot is about 2.4 MB, written as 150 rows, and calculated in the background in about 6.5 s. Cost: no cheap index query like "who voted Y in vote X in this snapshot".
- One published snapshot per quarter is enforced by a partial unique index; replacing needs an explicit `replace`.

## 4. Azure usage
- **Used now:** nothing connected yet. Development, tests and `docker compose up` run on PostgreSQL 17 in Docker, with the uploaded files in a volume. The application reads all connection settings from configuration, with no secrets in Git.
- **Planned for the Azure run:** Azure Database for PostgreSQL, by changing only the connection string; Application Insights for logs and the correlation id.
- **Not implemented:** Blob Storage. Files go through `IFileStore`; only the local implementation exists, and Blob would be a second one selected by configuration. Key Vault: next phase.
- **Not used, on purpose:** Cosmos DB (the need is relational integrity and joins over a million rows). OpenAI, Foundry and Document Intelligence are only needed for the PDF bonus.

## 5. What I would change before production
- Real authentication (for example Entra ID) with roles as claims; the `X-User` header has no password.
- HTTPS and an exact CORS origin if the front end is served from another origin; the 401 answer should not list user names.
- Add large CHECK constraints `NOT VALID`, then `VALIDATE` in a separate step.
- Take all timestamps from the database clock (today two clocks are used).
- Blob Storage for uploads, Key Vault for secrets, central logs and alerts, backups.
- Import in batches or on a queue if the one-transaction cost becomes a problem.

## 6. Next phase: four developers and standards
1. Ingestion and data quality (new file formats, consistency warnings, performance).
2. Review, history and concurrency (conflicts, positions, timeline).
3. Snapshots and scoring (rules, publishing, reproducibility).
4. Front end, contract and platform (client generation, CI, Azure, observability).
Standards first: the OpenAPI document is the contract and the client is regenerated in CI; one error format (ProblemDetails); migrations only, reviewed; every rule has a test, with a real PostgreSQL integration test; decisions go in this file and AI-assisted code is reviewed like any other.

## 7. Bonus
The PDF extraction was not built; the core came first. If built, extraction output would be untrusted input, and quality would be measured against hand-checked sheets (country-level accuracy, and sums matching the printed totals).
