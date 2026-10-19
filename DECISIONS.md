# DECISIONS (working draft)

Status legend: **Decided** = agreed. **Proposed** = my recommendation, to confirm. **Open** = needs an answer.
This draft is longer than the 2-page limit on purpose. It is trimmed at the end (detail lives in `docs/DATABASE.md`).

## 1. Ingestion rules — ING-04 to ING-13
- **Decided.** Columns are read by header name, not by position. The real files have exactly the Appendix A header, so this is a safety measure only (Excel in a right-to-left locale displays the columns reversed, which can look like a different order). A missing or unknown required header fails the whole file as a file-level error. A bad row never fails the file (ING-07).
- **Decided.** `country_code` is normalized by trimming whitespace and upper-casing (`" fra "` -> `FRA`, real example: `deu` in q1). A harmless formatting difference must not fail the pipeline. The normalized code is used for duplicate/contradiction detection and classification.
- **Decided.** `ballot` gets the same treatment: trim and upper-case (`y` -> `Y`), then it must be exactly one of `Y`, `N`, `A`, `X` (ING-04). Anything else is invalid, including `Yes`, `YES`, `?`, `0` and an empty value (all present in the real files). Nothing is guessed: `YES` is not turned into `Y`. Source check: the assignment lists Y, N, A, X as the only valid values (ING-04, section 2, Appendix A) and never mentions words like `YES`; the mockups show YES/NO/ABSTAIN/ABSENT only as screen labels for these codes. Rejecting the words is therefore my strict reading of ING-04 and ING-06, not a sentence in the assignment, and it is consistent with ING-13 (the only fixes are trim and case). Confirmed by the owner.
- **Decided.** `resolution_title` is trimmed and every run of internal whitespace becomes one space. Nothing else in a title changes: no case change, no removal of text such as `(draft)`. Real example: q1 line 3592 (MA/2024/025) has `Nuclear  Non-Proliferation…` with two spaces; it becomes the same title as the other rows of the vote. The historic file has no such titles.
- **Decided.** Normalization is never silent (ING-13): each import report shows how many rows were normalized, with the field, the original and the normalized value. The three normalizations above (country code, ballot, title whitespace) are the only ones. A normalization is reported only when the normalized value is accepted: `Yes` becomes `YES`, which is still not a ballot, so the row is reported as Invalid with the original `Yes`, not as normalized.
- **Decided.** Nothing else is normalized. Any other bad value (unknown code, ballot outside Y/N/A/X, bad date, session that is not a positive whole number, wrong column count, empty required field, non-member on the vote date) makes the row **Invalid**. A row gets every problem listed, not only the first. A wrong column count is the only exception: the fields cannot be trusted, so it is the only reason given (real: q1 line 3139 `AGO;N`). Membership is checked on the vote date in the row itself, and only when the code is known and the date is valid.
- **Decided. Required and optional fields:** `assembly_vote_id`, `session`, `vote_date`, `resolution_title`, `country_code` and `ballot` are required. `country_name` (descriptive only, ING-11) and `published_at` (file-level, stored on `imports`) are optional, but the import report counts the rows where they are empty. In the real files every row with the right number of columns has both: 0 empty `country_name` and 0 empty `published_at` in q1 (5,896 rows), rev2 (5,960) and historic (962,171). The only rows without them have the wrong number of columns and are Invalid for that reason: q1 line 3139 (5 columns) and 11 historic rows with 7 columns (empty name, no `published_at` column; first one: line 134746).
- **Decided. Vote details are checked separately from the country part of a row.** A row's vote details (session, vote_date, title) count for its vote when they are valid on their own, even if the row is Invalid because of its country part (unknown code, non-member on the vote date, bad ballot). A row whose own vote details are invalid (for example a bad date) or that has the wrong number of columns contributes nothing. The vote record is created when at least one row has valid vote details and all contributing rows agree, **even when none of its country rows is ingested**; no `country_votes` row is ever created for a rejected row. Spec 7.2: vote 103 (FRA Y and N, both Contradiction) and vote 104 (FRN unknown, NZL not a member, both Invalid) are created with their details after the first upload, so the second upload adds FRA to them as New ("completion of a country missing in the vote"). For an existing vote, details from such rows are compared with the approved details like any others and can open a vote-field conflict.
- **Decided. Contradiction in vote details — our interpretation:** after normalization, if the rows that contribute vote details (see above) still disagree on session, date or title, **all** valid rows of that vote are Contradiction and only that vote is not ingested; rows that are Invalid stay Invalid. The spec says the system "does not choose a value" (ING-06) but does not say what happens to the other rows of the vote; choosing the majority would be choosing a value. The report lists each variant with its line numbers. Real example: q1 MA/2024/025, line 3593 has the title `… (draft)`; all 149 rows of the vote are Contradiction. rev2 has the vote with one title, so it arrives then as New.
- **Decided. Duplicate:** a row identical to an earlier row of the same file after normalization (same vote, details, country code and ballot; the country name is ignored, ING-11). The first one is classified, the copies are Duplicate (real: q1 MA/2024/007, three rows). If the same country has different ballots in one vote, every row of it is Contradiction, copies included (real: q1 MA/2024/012 FRA, lines 1695 Y and 1793 N).
- **Decided. What happens to an Invalid row (ING-06, ING-07, ING-14):** it is not stored in approved data, it is counted in the Invalid counter, and it is listed in the report under "Rows that were not stored" with row number, vote, country, value, outcome and reason (original value kept). All other rows of the file, including other rows of the same vote, are processed normally. The import finishes as "Completed with issues". An Invalid row never takes part in the per-country checks, and takes part in the vote-details check only through valid vote details of its own (see above), so a single bad date cannot create a fake contradiction.
- **Decided.** Country **names** are never validated or normalized. A different name for the same code is not a conflict (ING-11) and is ignored: `countries.name` comes from the seed file, and the name as received stays in `import_rows`. The spec only says "not a conflict"; updating the name is not required, and silently overwriting reference data from a data file seemed wrong.
- **Decided (strict).** Dates must be exactly `yyyy-mm-dd` (ING-04 and Appendix A; the mockups show ISO dates too). Anything else is Invalid: `dd/mm/yyyy`, `dd-mm-yyyy`, month names, impossible dates such as 2024-02-30. Real example: q1 has 6 rows with `21/02/2024` (all in vote MA/2024/027, whose other 143 rows are ISO). They are reported as Invalid. rev2 contains the same 6 ballots in ISO, so they arrive later as New (ING-10) and the final approved data is the same as with a lenient rule. Rejected: accepting day-first formats and normalizing them (allowed by ING-13, but a deliberate deviation from the ING-04 wording, and a day/month guess is the one normalization that can change meaning). An earlier draft wrongly said the spec asks for `dd-mm-yyyy`; that format appears only in the illustrative table in section 2 of the assignment.

## 2. Country identity and membership (Decided)
- `country_code` is the key. A country is a member on date D if some membership period has `member_from <= D` and (`member_to` is empty or `D <= member_to`). Both ends are inclusive.
- A country that left and came back is **normal data**, not an anomaly: the assignment says it appears in two rows. In the real file Iceland (`ISL`) has 1995-01-01 to 2019-12-31 and 2022-01-01 to open. A vote in the gap (2020-01-01 to 2021-12-31) is not counted for ISL, and a row for ISL on such a date is Invalid ("not a member on <date>").
- Only **overlapping** periods of the same country, or `member_to` before `member_from`, are errors in the seed file.
- Real seed file: 151 rows, 150 countries, 149 rows with empty `member_to`.
- The membership table is loaded into memory once per import, not queried per row.

## 3. Reference data: countries and users (Decided)
- The system loads `seed/member_states.csv` into `countries` and `country_memberships` at startup, so `docker compose up` works on a fresh machine without a manual step. Users (Dana, Omar, Lena, `system`) are seeded too.
- The load is idempotent: it records the file hash as an import by `system` (it appears in the import history as `member_states.csv (seed)`, as in the mockups) and skips if already loaded. Otherwise it adds only missing rows and never deletes or overwrites. Overlapping periods in the file fail loudly.
- **Decided (built in step 2):** startup order is migrate (when `Database:MigrateOnStartup` is true; otherwise pending migrations stop startup), seed users, seed member states, in one transaction under an advisory lock, and a failure stops the process. It runs in `IHostedLifecycleService.StartingAsync`, before the web server accepts requests (code between `Build()` and `Run()` in `Program.cs` would be skipped by `WebApplicationFactory` in tests). A period in a changed file that overlaps one already stored stops startup too: reference data is never overwritten automatically. `imports.kind` (`Votes` or `MemberStates`) tells the seed import apart, so the history label is derived and not stored as text. One `audit_events` row per country, period and user added. If there are no countries after startup the API refuses to start: with no reference data every row of every import would be "unknown country".
- Rejected: uploading countries through the UI (the validation of every other import depends on them, and the spec wants base data with compose); countries as SQL inside a migration (hard to read and update).

## 4. History and audit trail (Decided)
- One general `audit_events` table (entity, id, action, old value, new value, actor, source import, correlation id, time) instead of a versions table per entity. It feeds the vote timeline and answers every HIS question with one query. Trade-off: JSON values are less strictly typed.
- Decision attempts that were not applied (a second analyst hitting an already resolved conflict) are logged too, because the vote timeline in the mockups shows them.

## 5. Conflicts and concurrency (Decided)
- A decision is one conditional `UPDATE ... WHERE status = 'Pending'`, and the approved value must still equal the value the conflict was opened against. The update, the approved-value change and the audit event happen in one transaction.
- The second analyst gets HTTP 409 with a structured error (who decided, when, what), and the UI shows the current state, not a generic error (CON-05).
- Uniqueness is a **partial** unique index on (target, proposed value) `WHERE status = 'Pending'`. A decided conflict must not block a later real change of the same value (Y -> A -> Y -> A). Vote-field targets: `session`, `vote_date`, `title`.
- Re-upload after a rejection: the database no longer blocks it, so ingestion handles it. A row is skipped when the same proposed value was already rejected from a file with the same `sha256`. A different file proposing it again opens a new conflict.
- **Decided. Re-upload while a conflict is pending:** a row that matches an already pending conflict keeps the classification **Conflict**, exactly as on the first upload, so the class of a row never depends on how many times the file was uploaded. No second conflict is opened (ING-12). The report shows the Conflict rows and a separate line "new conflicts opened: 0 (N already waiting for a decision)". Rejected: counting such rows as Unchanged (the assignment defines Unchanged as identical to the approved data) and a separate "Already pending" class.
- Two pending conflicts on the same target with different proposed values can coexist. Deciding one makes the other stale (409 and refresh).
- A vote missing from a new file is never deleted or changed (ING-09). Countries missing from an existing vote are added as New, not as conflicts (ING-10).

## 6. Scoring rules (Decided) — SNP, section 7.6
- Only `Y` and `N` count. Abstain (`A`) and absent (`X`) are not counted, neither in numerator nor denominator.
- A vote in which the country was not a member on the vote date is not counted.
- A country with no counted votes gets score `null` ("n/a"), not 0, and no rank.
- A vote without a position is never silently included. Creating a snapshot is blocked until the vote gets a position or is removed from the selection (as in the mockup).
- The snapshot stores a frozen copy of position, weight and ballots at creation time, plus the rules used.
- **Ranking order (Decided):** by exact score, highest first; equal scores are ordered alphabetically by country name. Ranks are positional (1, 2, 3, ...), not shared, and shown as "N of M" where M is the number of countries that have a score. This matches the mockup (Brazil 38 and Ghana 39 with the same displayed score).

## 7. Import pipeline and background work (Proposed)
- Stream the file, parse and validate each row in pure code (unit-testable), bulk `COPY` the rows into `import_rows`, then run consistency checks and classification as set-based SQL grouped by (vote, country), which works when rows of one vote are not consecutive.
- Classification per vote; the batch query will be shown and confirmed in the step 4 plan. The grouping key is the vote id, and the country code is the key inside a vote.
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

## 10. Azure usage
- **Decided. Database:** local PostgreSQL in Docker is the default for development, tests and `docker compose up`. At the end, the whole system is run once against Azure Database for PostgreSQL by changing only the connection string, and that run is documented in the README. The Azure resources exist only during the assignment window, so a reviewer must still be able to run everything locally.
- **Proposed. Blob Storage:** original uploaded files (HIS-01), behind an `IFileStore` interface (local folder or Azurite by default, Blob when configured).
- **Proposed. Application Insights:** logs and the correlation id from the UI through the API.
- **Proposed. Key Vault:** only if time allows. Secrets stay in `.env` / user-secrets, never in Git.
- **Decided. Cosmos DB: not used.** The core need is relational integrity (constraints, transactions, joins over a million rows). A document store adds nothing here.
- **Azure OpenAI, Foundry, Document Intelligence:** only if the PDF bonus is built.
- The app connects to Azure only through configuration, after the core works locally.

## 11. API and frontend (Proposed)
- Roles are enforced in the API (positions and publishing are Admin only), not just in the UI. One error format (ProblemDetails), documented in OpenAPI.
- The Angular client is generated from the OpenAPI contract, never handwritten.
- The mockups are guidance, not requirements; the integration between front and back is what is evaluated. Minimum: the four screens Imports, Review, Votes, Snapshots.

## Open
- Consistency checks on real data: year inside the vote id vs the vote date, and session number vs year (the sample looks like year minus 1945). Proposal: warnings in the report only, after checking they hold for all rows.
- PDF bonus: decide at the end of day 2 based on progress.
- Confirm the proposals in sections 7, 10 and 11 when they are built.

## To write before submitting
- If the bonus is done: how extraction quality would be measured.
- What I would change before production.
- How I would split the next phase between four developers, and which standards I would set first.
