# DECISIONS

Detail behind every rule: [docs/DECISIONS-DETAIL.md](docs/DECISIONS-DETAIL.md). Tables and constraints: [docs/DATABASE.md](docs/DATABASE.md).

## 1. Key decisions and trade-offs
- **The back end owns the rules.** ASP.NET Core 9, EF Core + Npgsql, bulk `COPY` for large files (plain EF is too slow for about a million rows). Validation and classification are pure Domain code with unit tests.
- **Deterministic ingestion, no AI.** Every row gets one class (New, Unchanged, Duplicate, Invalid, Contradiction, Conflict). Approved data is never overwritten by a process, only by a person's decision.
- **Strict format.** Dates `yyyy-mm-dd`, ballots exactly `Y/N/A/X`. The only normalizations (trim and upper-case code and ballot, one space in titles) are counted and reported (ING-13). Nothing is guessed (`YES` is Invalid).
- **A bad row never spoils the vote.** Vote details come from valid rows only. When they disagree, all valid rows of that vote are rejected as Contradiction: taking the majority would be choosing a value.
- **Idempotent re-uploads** (ING-12): same file, same result; no new conflict while one waits, and none for a conflict already rejected, or a value already replaced by a decision, from the same file (sha256).
- **Concurrency in the database.** A decision is one conditional `UPDATE ... WHERE status='Pending'`; the loser gets 409 with who decided (CON-05). Stale conflicts need `expectedApprovedValue`; positions need `expectedVersion`.
- **One transaction per import**: never a half-imported file. Cost: a late failure means uploading again. Historic file (962,171 lines): about 70-90 s, peak memory about 350 MB.
- **Progress is a table plus polling**, not SignalR: it survives a page refresh.
- **Front end:** Angular with a client generated from the OpenAPI document, one origin (no CORS); validation errors beside the field, 409 as a structured message. Appendix B screens are not binding.
- **Users:** `X-User` with preset users (section 3); roles enforced in the API (only Admin sets positions and publishes). `X-Correlation-Id` follows a request into logs, jobs and audit.

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
- **Azure Database for PostgreSQL: tried, not verified.** The code reaches it through configuration only (`docker-compose.azure.yml`, connection string from `.env`, Entra token as the password). The server has no firewall rule for my IP, and my Reader role cannot add one; I asked for it.
- **Application Insights (`dotnetcandidate2-appi`):** connected through configuration only (OpenTelemetry, on when the connection string is set), correlation id on requests and logs; verified in the portal (requests and logs, including the import worker, found by one correlation id).
- **Otherwise local:** PostgreSQL 17 in Docker, uploads in a volume by default, no secrets in Git.
- **Blob Storage (`dotnetcandidate2st`):** through configuration only, Entra auth; verified against Azure (an upload stored as a blob, import Succeeded).
- **Not implemented:** Key Vault.
- **Not used, on purpose:** Cosmos DB (relational integrity, joins over a million rows). OpenAI, Foundry and Document Intelligence only serve the PDF bonus, not built.

## 5. What I would change before production
- Real authentication (Entra ID, roles as claims); `X-User` has no password.
- HTTPS; an exact CORS origin if the front end moves; no user names in the 401.
- Large CHECK constraints added `NOT VALID`, then validated.
- All timestamps from the database clock (two clocks today).
- Blob Storage, Key Vault, central logs and alerts, backups.
- Imports in batches or on a queue if one transaction becomes a problem.

## 6. Next phase: four developers and standards
1. Ingestion and data quality (formats, consistency warnings, performance).
2. Review, history and concurrency (conflicts, positions, timeline).
3. Snapshots and scoring (rules, publishing, reproducibility).
4. Front end, contract and platform (client generation, CI, Azure, observability).

Standards first: the OpenAPI document is the contract and the client is regenerated in CI; one error format (ProblemDetails); reviewed migrations only; every rule tested, with real PostgreSQL integration tests; decisions recorded here; AI-assisted code reviewed like any other.

## 7. Bonus
Not built; the core came first. Extraction output would be untrusted input, measured against hand-checked sheets: country-level accuracy, and sums matching the printed totals.
