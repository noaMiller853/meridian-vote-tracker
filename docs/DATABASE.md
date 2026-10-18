# Database schema

PostgreSQL, accessed with EF Core and Npgsql. Schema managed by migrations. Large imports use bulk `COPY`.

| Area | Table | Key columns / notes |
|---|---|---|
| Reference | `countries` | `country_code` PK, `name` |
| | `country_memberships` | `country_code` FK, `member_from`, `member_to` (null = still a member). Exclusion constraint: no overlapping periods per country |
| | `users` | `id`, `name`, `role` (Analyst / Admin) |
| Ingestion | `background_jobs` | `type`, `status`, `done`, `total`, `error`, `correlation_id`. Used by imports and snapshots |
| | `imports` | file name, storage URI, sha256, `uploaded_by`, `uploaded_at`, counts per classification, report. The seed load is also an import by `system` |
| | `import_rows` | `import_id` FK, `row_number`, raw line, parsed fields, `classification`, `reason`, original value, normalized flag |
| Approved data | `votes` | `assembly_vote_id` UNIQUE, session, vote date, title, first import |
| | `country_votes` | PK (`vote_id`, `country_code`), `ballot` CHECK (Y/N/A/X), source import and source row, approved by / at |
| | `vote_positions` | `vote_id` PK/FK, position (Y/N), `weight` CHECK 1..5 |
| Review | `conflicts` | kind (country vote / vote field), approved value, proposed value, both source imports, `status` (Pending / Approved / Rejected), decided by / at / note |
| History | `audit_events` | entity, entity id, vote id, action, old value, new value (jsonb), actor, import id, correlation id, time. Feeds the vote timeline |
| Snapshots | `snapshots` | name, filter, rules used, status, quarter, published by / at |
| | `snapshot_votes` | frozen copy of position and weight, included flag and reason |
| | `snapshot_scores` | `country_code`, score (nullable), rank (nullable), counted and matched weight |
| | `snapshot_contributions` | `country_code`, `vote_id`, ballot, counted flag, reason, weight, matched |
| Bonus | `pdf_extractions` | only if the bonus is built |

## Relationships
- One country has many membership periods.
- One import has many import rows. Imports and snapshots each point to one background job.
- One vote has many country votes, at most one position, and many conflicts.
- One snapshot has many snapshot votes, scores and contributions.

## Integrity rules enforced by the database
- `ballot` is one of Y, N, A, X. `weight` is between 1 and 5. Position is Y or N.
- `assembly_vote_id` is unique. A country has one ballot per vote (composite primary key).
- Membership periods of one country never overlap (exclusion constraint, needs the `btree_gist` extension).
- `conflicts` is unique on (target, proposed value), so a re-upload never reopens a conflict, including one already decided.
- At most one snapshot is published per quarter (partial unique index).

## Suggested indexes
`import_rows (import_id, row_number)`, `audit_events (vote_id, occurred_at)`, `conflicts (status)`, `country_votes (country_code)`.
