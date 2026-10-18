# DECISIONS (working draft)

Status legend: **Decided** = agreed. **Proposed** = my recommendation, to confirm. **Open** = needs an answer.
This draft is longer than the 2-page limit on purpose. It is trimmed at the end (detail lives in `docs/DATABASE.md`).

## 1. Ingestion rules — ING-04 to ING-13
- **Decided.** Columns are read by header name, not by position (the real file's column order differs from Appendix A). A missing or unknown required header fails the whole file as a file-level error. A bad row never fails the file (ING-07).
- **Decided.** `country_code` is normalized by trimming whitespace and upper-casing (`" fra "` -> `FRA`). A harmless formatting difference must not fail the pipeline. The normalized code is used for duplicate/contradiction detection and classification.
- **Decided.** Normalization is never silent (ING-13): each import report shows how many rows were normalized, with the original and the normalized value.
- **Decided.** Nothing else is normalized. Any other bad value (unknown code, `ballot` outside Y/N/A/X, bad date, wrong column count, non-member on the vote date) makes the row **Invalid**, reported with row number, reason and original value.
- **Proposed.** The same trim/upper-case rule applies to `ballot` (`y` -> `Y`).
- **Proposed.** Country **names** are never validated or normalized. A different name for the same code is not a conflict (ING-11) and is simply ignored: `countries.name` comes from the seed file, and the name as received stays in `import_rows`. The spec only says "not a conflict"; updating the name is not required, and silently overwriting reference data from a data file seemed wrong.
- **Proposed.** Dates: accept ISO `yyyy-mm-dd` only and reject anything else. The spec says `dd-mm-yyyy` (ING-04) but Appendix A, the mockups and the real file use ISO. Guessing `03-04-2024` (day-month or month-day) would be exactly the silent fix ING-13 forbids.

## 2. Country identity and membership (Decided)
- `country_code` is the key. A country is a member on date D if some membership period has `member_from <= D` and (`member_to` is empty or `D <= member_to`). A country that left and returned has two periods.
- The membership table is loaded into memory once per import, not queried per row.

## 3. Reference data: countries and users (Decided)
- The system loads `seed/member_states.csv` into `countries` and `country_memberships` at startup, so `docker compose up` works on a fresh machine without a manual step. Users (Dana, Omar, Lena, `system`) are seeded too.
- The load is idempotent: it records the file hash as an import by `system` and skips if already loaded. Otherwise it adds only missing rows and never deletes or overwrites. Overlapping periods in the file fail loudly.
- Rejected: uploading countries through the UI (the validation of every other import depends on them, and the spec wants base data with compose); countries as SQL inside a migration (hard to read and update).

## 4. History and audit trail (Decided)
- One general `audit_events` table (entity, id, action, old value, new value, actor, source import, correlation id, time) instead of a versions table per entity. It feeds the vote timeline and answers every HIS question with one query. Trade-off: JSON values are less strictly typed.
- Decision attempts that were not applied (a second analyst hitting an already resolved conflict) are logged too, because the vote timeline in the mockups shows them.

## 5. Conflicts and concurrency (Decided)
- A decision is one conditional `UPDATE ... WHERE status = 'Pending'`, and the approved value must still equal the value the conflict was opened against. The update, the approved-value change and the audit event happen in one transaction.
- The second analyst gets HTTP 409 with a structured error (who decided, when, what), and the UI shows the current state, not a generic error (CON-05).
- Uniqueness is a **partial** unique index on (target, proposed value) `WHERE status = 'Pending'`. A decided conflict must not block a later real change of the same value (Y -> A -> Y -> A). Vote-field targets: `session`, `vote_date`, `title`.
- Re-upload after a rejection: the database no longer blocks it, so ingestion handles it. A row is skipped when the same proposed value was already rejected from a file with the same `sha256`. A different file proposing it again opens a new conflict.
- Two pending conflicts on the same target with different proposed values can coexist. Deciding one makes the other stale (409 and refresh).
- A vote missing from a new file is never deleted or changed (ING-09). Countries missing from an existing vote are added as New, not as conflicts (ING-10).

## 6. Scoring rules (Decided) — SNP, section 7.6
- Only `Y` and `N` count. Abstain (`A`) and absent (`X`) are not counted, neither in numerator nor denominator.
- A vote in which the country was not a member on the vote date is not counted.
- A country with no counted votes gets score `null` ("n/a"), not 0, and no rank.
- A vote without a position is never silently included. Creating a snapshot is blocked until the vote gets a position or is removed from the selection (as in the mockup).
- The snapshot stores a frozen copy of position, weight and ballots at creation time, plus the rules used.

## 7. Import pipeline and background work (Proposed)
- Stream the file, parse and validate each row in pure code (unit-testable), bulk `COPY` the rows into `import_rows`, then run consistency checks and classification as set-based SQL grouped by (vote, country), which works when rows of one vote are not consecutive.
- New rows and conflicts are inserted in one transaction with `ON CONFLICT DO NOTHING`, so a failure leaves no half import and a repeat upload adds nothing (ING-12).
- One import worker at a time removes races between overlapping uploads.
- Progress lives in a `background_jobs` table in PostgreSQL, processed by a `BackgroundService`. The UI polls every 1-2 seconds. SignalR was not chosen: persistent connections add complexity, and the database stays the source of truth anyway. It is a later upgrade.
- EF Core + Npgsql for the domain and migrations; bulk `COPY` for large files, because plain EF would be slow and memory-hungry on a million rows (**Decided**).

## 8. No AI/RAG in CSV ingestion (Decided)
- Ingestion must be deterministic, explainable per row and idempotent. Rules in code satisfy that; a language model does not.
- AI is considered only for the optional PDF bonus, and its output is treated as untrusted input.

## 9. Database design (Decided, built in step 1)
- Approved data lives in `votes`, `country_votes` and `vote_positions`. Every approved row keeps its source import and row.
- `published_at` is stored per file on `imports` (shown in the import report and conflict cards); per-row values stay in `import_rows`.
- The position column is `desired_outcome` (avoids the SQL keyword).
- Snapshot status: Calculating / Saved / Failed / Published. `quarter` is set when publishing. Un-publishing returns the snapshot to Saved with an audit event, so a second snapshot for a quarter needs an explicit un-publish.
- Enum-like columns are `text` with CHECK constraints; every foreign key is `RESTRICT`, so nothing approved is ever deleted.
- Snapshots keep frozen copies (votes with position and weight, scores, per-vote contributions), so later corrections never change a saved snapshot. Trade-off: a full-history snapshot stores about a million contribution rows.
- Full table list, relationships and constraints: `docs/DATABASE.md`.

## 10. Azure usage (Proposed)
- **Local first:** PostgreSQL in Docker for development and tests; the app connects to Azure only through configuration, after the core works locally (start of day 3).
- **Blob Storage:** original uploaded files (HIS-01), behind an `IFileStore` interface (local folder or Azurite by default, Blob when configured).
- **Application Insights:** logs and the correlation id from the UI through the API.
- **Key Vault:** only if time allows. Secrets stay in `.env` / user-secrets, never in Git.
- **Cosmos DB: not used.** The core need is relational integrity (constraints, transactions, joins over a million rows). A document store adds nothing here.
- **Azure OpenAI, Foundry, Document Intelligence:** only if the PDF bonus is built.

## 11. API and frontend (Proposed)
- Roles are enforced in the API (positions and publishing are Admin only), not just in the UI. One error format (ProblemDetails), documented in OpenAPI.
- The Angular client is generated from the OpenAPI contract, never handwritten.

## Open
- Date format: confirm "ISO only" after checking the real files for other formats.
- Confirm the proposals in sections 1 and 7.
- Consistency checks on real data: year inside the vote id vs the vote date, and session number vs year (the sample looks like year minus 1945). Proposal: warnings in the report only, after checking they hold for all rows.
- PDF bonus: decide at the end of day 2 based on progress.

## To write before submitting
- If the bonus is done: how extraction quality would be measured.
- What I would change before production.
- How I would split the next phase between four developers, and which standards I would set first.
