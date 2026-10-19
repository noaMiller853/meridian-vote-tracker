# Database schema

PostgreSQL, accessed with EF Core and Npgsql. Schema managed by migrations (`src/Meridian.Infrastructure/Persistence/Migrations`), snake_case names. Large imports use bulk `COPY`.

| Area | Table | Key columns / notes |
|---|---|---|
| Reference | `countries` | `country_code` PK (three upper-case letters), `name` |
| | `country_memberships` | `country_code` FK, `member_from`, `member_to` (null = still a member). Exclusion constraint: no overlapping periods per country |
| | `users` | `id`, `name` (unique), `role` (Analyst / Admin). `system` is seeded by the first migration; Dana, Omar and Lena at startup |
| Ingestion | `background_jobs` | `type` (Import / Snapshot), `status` (Queued / Running / Succeeded / Failed), `phase` (Reading / Classifying / Writing while Running; `total` is null while Reading), `done`, `total`, `error`, `correlation_id`, created / started / finished at. Used by imports and snapshots; `imports.background_job_id` points to the job of an upload (the seed import has none) |
| | `imports` | `kind` (Votes / MemberStates; the seed file shows as `member_states.csv (seed)`), file name, storage URI, sha256 (indexed with kind), `uploaded_by`, `uploaded_at`, `published_at` (nullable, file level; shown in the import report and conflict cards), counts per classification, report. The seed load is also an import by `system` |
| | `import_rows` | PK (`import_id`, `row_number` = line in the file, header = line 1), raw line, the fields as found (text, including the per-row `published_at`), normalized `country_code` and the original one, `field_count`, `classification`, `reason`, original value, `problems` and `normalizations` (jsonb), `conflict_action` (Open / AlreadyPending / AlreadyRejected / AlreadySuperseded), normalized flag |
| | `import_votes` | PK (`import_id`, `assembly_vote_id`): what happened to each vote in an import. `vote_id` (nullable), `status` (New / Existing / Contradiction / NoValidDetails), `vote_created`, counts per class, conflicts opened / already pending / already rejected / already superseded, vote-field changes (jsonb), contradiction reason |
| Approved data | `votes` | `assembly_vote_id` UNIQUE, session, vote date, title, first import, `session_source_import_id`, `vote_date_source_import_id`, `title_source_import_id` (the import that set each detail: the first import, or the proposing import of an approved vote-field conflict; the approved source shown on a conflict on that field) |
| | `country_votes` | PK (`vote_id`, `country_code`), `ballot` CHECK (Y/N/A/X), source import and source row, approved by / at |
| | `vote_positions` | `vote_id` PK/FK, `desired_outcome` (Y = For, N = Against), `weight` CHECK 1..5, `set_by` / `set_at`, `version` (1 when first set, +1 on every change; CHECK >= 1; the client sends it back as `expectedVersion`) |
| Review | `conflicts` | `kind` (CountryVote / VoteField), `country_code` (CountryVote) or `field_name` (`session`, `vote_date`, `title`; VoteField), approved value, proposed value, both source imports, `status` (Pending / Approved / Rejected), decided by / at / note, and on Approve `replaced_value` and `replaced_source_import_id` (what the approval replaced; set exactly when Approved) |
| History | `audit_events` | entity, entity id, vote id, action, old value, new value (jsonb), actor, import id, correlation id, time. Feeds the vote timeline |
| Snapshots | `snapshots` | name, filter, rules used, `status` (Calculating / Saved / Failed / Published), `quarter` (nullable until published), published by / at. Un-publishing returns to Saved and writes an audit event |
| | `snapshot_votes` | frozen copy of `desired_outcome` and weight, included flag and reason |
| | `snapshot_scores` | `country_code`, score (nullable), rank (nullable), counted and matched weight |
| | `snapshot_contributions` | `country_code`, `vote_id`, ballot, counted flag, reason, weight, matched |
| Bonus | `pdf_extractions` | only if the bonus is built |

## Relationships
- One country has many membership periods.
- One import has many import rows. Imports and snapshots each point to one background job.
- One vote has many country votes, at most one position, and many conflicts.
- One snapshot has many snapshot votes, scores and contributions. A contribution refers to a vote of the same snapshot.
- All foreign keys are `ON DELETE RESTRICT`: approved data, imports and snapshots are never deleted.

## Integrity rules enforced by the database
- `ballot` is one of Y, N, A, X. `weight` is between 1 and 5. `desired_outcome` is Y or N.
- `assembly_vote_id` is unique. A country has one ballot per vote (composite primary key).
- Membership periods of one country never overlap (exclusion constraint, needs the `btree_gist` extension).
- At most one **Pending** conflict per (target, proposed value): partial unique index on (`vote_id`, `kind`, `country_code`, `field_name`, `proposed_value`) `NULLS NOT DISTINCT WHERE status = 'Pending'`. A re-upload never duplicates a pending conflict, and decided conflicts do not block a later change of the same value (Y -> A -> Y -> A).
- A conflict targets either a country (CountryVote) or a field (VoteField), never both. Decided by / at are set exactly when the status is not Pending; the replaced value and its source exactly when it is Approved.
- At most one snapshot is published per quarter (partial unique index). A Published snapshot must have a quarter.

## Indexes
`import_rows (import_id, row_number)` (primary key), `import_rows (import_id, classification, row_number)` and `(import_id, assembly_vote_id, row_number)` for the paged report, `audit_events (vote_id, occurred_at)`, `conflicts (status)`, `country_votes (country_code)`, plus one index per foreign key.
