# DECISIONS (working draft)

Status legend: **Decided** = agreed. **Proposed** = to confirm. **Open** = needs an answer.

## 1. Ingestion: minimal, reported normalization (Decided) — ING-13
- `country_code` is normalized by trimming whitespace and upper-casing (`" fra "` -> `FRA`). A harmless formatting difference must not fail the pipeline.
- Nothing else is normalized. Any other bad value (unknown code, `ballot` outside Y/N/A/X, bad date, wrong column count) makes the row **Invalid**, reported with row number, reason and original value.
- Normalization is never silent: each import report shows how many rows were normalized, with the original and the normalized value. The normalized code is what is used for duplicate/contradiction detection and classification.
- `ballot`: to be confirmed whether the same trim/upper-case rule applies (proposal: yes).
- Country **names** are never validated or normalized. Identity is the code (ING-11). The name is descriptive only, and a different name for the same code is not a conflict.

## 2. Country identity and membership (Decided)
- `country_code` is the key. A country is a member on date D if some membership period has `member_from <= D` and (`member_to` is empty or `D <= member_to`).
- The membership table is loaded into memory once per import, not queried per row.

## 3. History and audit trail (Decided)
- One general `audit_events` table (entity, id, action, old value, new value, actor, source import, correlation id, time) next to the approved-data tables, instead of a versions table per entity.
- Decision attempts that were not applied (a second analyst hitting an already resolved conflict) are logged too, because the vote timeline in the mockups shows them.

## 4. Scoring rules (Decided) — SNP, section 7.6
- Only `Y` and `N` count. Abstain (`A`) and absent (`X`) are not counted, neither in numerator nor denominator.
- A vote in which the country was not a member on the vote date is not counted.
- A country with no counted votes gets score `null` ("n/a"), not 0, and no rank.
- A vote without a position is never silently included. Creating a snapshot is blocked until the vote gets a position or is removed from the selection (as in the mockup).
- The snapshot stores a frozen copy of position, weight and ballots at creation time.

## 5. No AI/RAG in CSV ingestion (Proposed)
- Ingestion must be deterministic, explainable per row and idempotent. Rules in code satisfy that; a language model does not.
- AI is considered only for the optional PDF bonus, and its output is treated as untrusted input.

## 6. Database and history design (Proposed)
- PostgreSQL with EF Core and Npgsql, schema managed by migrations, bulk `COPY` for large files.
- Approved data lives in `votes`, `country_votes` and `vote_positions`. Every approved row keeps its source import and row.
- Conflicts have their own table. A decision is one conditional `UPDATE ... WHERE status = 'Pending'`, so only one decision is accepted. A partial unique index on (target, proposed value) `WHERE status = 'Pending'` prevents a duplicate open conflict. It is partial on purpose: a decided conflict must not block a later real change of the same value (Y -> A -> Y -> A). Allowed vote-field targets: `session`, `vote_date`, `title`.
- `published_at` is stored per file on `imports` (shown in the import report and conflict cards); per-row values stay in `import_rows`.
- The position column is `desired_outcome` (avoids the SQL keyword `position`).
- Snapshot status: Calculating / Saved / Failed / Published. `quarter` is set when publishing (CHECK: a Published snapshot has a quarter). Un-publishing returns the snapshot to Saved and writes an audit event.
- Snapshots keep frozen copies (votes with position and weight, scores, per-vote contributions), so later corrections never change a saved snapshot.
- Full table list, relationships and constraints: `docs/DATABASE.md`.

## Open
- Date format: the spec says `dd-mm-yyyy` (ING-04) but Appendix A and the real file use `yyyy-mm-dd`. Proposal: accept ISO only, reject anything else.
- Rejected conflict and re-upload of the same corrected file: proposal is to keep it closed.
- Publishing a second snapshot for the same quarter: requires un-publishing the first (back to Saved, audited); enforced by a partial unique index.
- PDF bonus: decide at the end of day 2 based on progress.

## To write before submitting
- Which Azure resources were used and why, and why the others were not.
- If the bonus is done: how extraction quality would be measured.
- What I would change before production.
- How I would split the next phase between four developers, and which standards I would set first.
