# DECISIONS

Detail behind every rule: [docs/DECISIONS-DETAIL.md](docs/DECISIONS-DETAIL.md). Tables and constraints: [docs/DATABASE.md](docs/DATABASE.md).

## 1. Key decisions and trade-offs
- **The back end owns the rules:** ASP.NET Core 9, EF Core + Npgsql, bulk `COPY` for large files (plain EF is too slow for a million rows); validation and classification are unit-tested Domain code.
- **Deterministic ingestion, no AI:** one class per row (New, Unchanged, Duplicate, Invalid, Contradiction, Conflict); only a person's decision overwrites approved data.
- **Strict format:** dates `yyyy-mm-dd`, ballots exactly `Y/N/A/X`; the only normalizations (trim and upper-case code and ballot, one space in titles) are counted and reported (ING-13); nothing guessed (`YES` is Invalid).
- **A bad row never spoils the vote:** details come from valid rows only; if they disagree, all valid rows are Contradiction (a majority would be choosing a value).
- **Idempotent re-uploads** (ING-12): same file, same result; no second conflict while one waits, none for a proposal rejected or a value replaced by a decision from the same file (sha256).
- **Concurrency in the database:** a decision is one conditional `UPDATE ... WHERE status='Pending'`; the loser gets 409 naming the decider (CON-05). Stale conflicts need `expectedApprovedValue`, positions `expectedVersion`.
- **One transaction per import:** never half-imported; a late failure means uploading again. Historic file (962,171 lines): about 70-90 s, peak memory about 350 MB.
- **Progress:** a table plus polling, not SignalR; survives a refresh.
- **Front end:** Angular, client generated from the OpenAPI document, one origin (no CORS); errors beside the field, 409 as a structured message; Appendix B not binding.
- **Users:** preset users via `X-User` (section 3), roles enforced in the API (only Admin sets positions and publishes); `X-Correlation-Id` follows a request into logs, jobs and audit.

## 2. Scoring rules (SNP)
Score = 100 × (sum of matching weights) ÷ (sum of counted weights), stored with 6 decimals, shown with one.
- Only `Y` and `N` count. Abstain (`A`) and absent (`X`) count in neither part.
- Not counted: votes where the country was not a member on the vote date, and members with no approved ballot.
- No counted vote: no score (`n/a`) and no rank, not 0.
- Ranking: exact score, ties by country name, positional ranks.
- A vote without a position never enters silently (POS-04): creation answers 409 listing them; if the creator confirms, the snapshot lists them as excluded.
- Every snapshot shows these rules and how each country's score was calculated.

## 3. Database and history
- Approved data (`votes`, `country_votes`, `vote_positions`) keeps its source import and line. Foreign keys `RESTRICT`, CHECK constraints, EF Core migrations.
- **History:** one `audit_events` table (old and new value, actor, source import, correlation id, time) feeds the vote timeline and answers every HIS question. Trade-off: JSON values are less strictly typed than a versions table per entity. Nothing is deleted.
- Every import keeps the file (hash-addressed), who, when, the report and one row per line with class, reason and original value.
- **Frozen snapshots** (SNP-04): per country, one character per vote for the ballot and the outcome, plus positions, weights, scores and rules; nothing is recalculated. Full history: about 2.4 MB, 150 rows, about 6.5 s in the background. Cost: no cheap query like "who voted Y in vote X".
- One published snapshot per quarter (partial unique index); replacing needs an explicit `replace`.

## 4. Azure usage
- **Application Insights (`dotnetcandidate2-appi`): used, verified.** Connected through configuration only (OpenTelemetry, on when the connection string is set), correlation id on requests and logs; verified in the portal (requests and logs, including the import worker, found by one correlation id).
- **Blob Storage (`dotnetcandidate2st`): used, verified against Azure** (an upload stored as a blob, import Succeeded). On by configuration (`docker-compose.blob.yml`); local files stay the default, so one command runs everything. Key access is disabled: Entra auth only. If Azure refuses: a clear 503, nothing imported, no silent fallback to local files (they would split between two stores unnoticed).
- **Azure Database for PostgreSQL (`dotnetcandidate2-psql`): supported, blocked, not verified.** Configuration only (`docker-compose.azure.yml`, Entra token as password). Public access is on, but no firewall rule exists for my IP (diagnosis in DECISIONS-DETAIL §10). The firewall is a management setting and my role is Reader (blob access is a separate data role); I asked for the rule. Meanwhile: PostgreSQL 17 in Docker.
- **Key Vault (`dotnetcandidate2kv`): planned, not connected within the time window.** Intended source of connection strings and tokens via the configuration provider (`--` for `:`), `DefaultAzureCredential`, a managed identity when deployed. Until then secrets come only from `.env` and the terminal, never Git.
- **Not used, on purpose:** Cosmos DB (relational integrity, joins over a million rows). Document Intelligence, Azure OpenAI and Foundry only serve the PDF bonus: designed ([docs/BONUS-PDF-DESIGN.md](docs/BONUS-PDF-DESIGN.md)), not built.

## 5. What I would change before production
- Real authentication (Entra ID, roles as claims) instead of the password-less `X-User`.
- HTTPS; an exact CORS origin if the front end moves; no user names in the 401.
- Large CHECK constraints added `NOT VALID`, then validated.
- One timestamp clock, the database's (two today).
- Blob Storage, Key Vault, central logs and alerts, backups.
- Batched or queued imports if one transaction becomes a problem.

## 6. Next phase: four developers and standards
1. Ingestion and data quality (formats, consistency warnings, performance).
2. Review, history and concurrency (conflicts, positions, timeline).
3. Snapshots and scoring (rules, publishing, reproducibility).
4. Front end, contract and platform (client generation, CI, Azure, observability).

Standards first: the OpenAPI document is the contract and the client is regenerated in CI; one error format (ProblemDetails); reviewed migrations only; every rule tested, with real PostgreSQL integration tests; decisions recorded here; AI-assisted code reviewed like any other.

## 7. Bonus
Designed, not built: the core came first. Design: [docs/BONUS-PDF-DESIGN.md](docs/BONUS-PDF-DESIGN.md). Document Intelligence gives the table, the marks and a confidence per cell; an LLM is called only for what DI cannot resolve. The output is untrusted: Domain rules and review before the CSV pipeline. Quality is measured against hand-labelled sheets (details in the link).
