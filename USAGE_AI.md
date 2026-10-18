# AI usage

## Tools
- **Claude (chat, claude.ai):** requirement analysis, planning, drafting DECISIONS.md and CLAUDE.md.
- **Claude Code:** implementation. TODO: describe what it was used for.

## Where I rejected or corrected AI output
Add a row whenever something is rejected, fixed or changed. It is hard to reconstruct at the end.

| Stage | What the AI proposed or did | What I did | Why |
|---|---|---|---|
| Planning | Misread my instruction and proposed treating country codes with spaces or lower case as invalid rows | Corrected it: trim and upper-case the code, and show the normalization in the import report | Such trivial differences should not fail the pipeline, and ING-13 only requires that it is reported |
| Planning | Proposed a standalone data-profiling script before writing code | Dropped it and started from the schema | The import report already gives the same information for any file |
| Planning | Put the full table list inside DECISIONS.md | Moved it to docs/DATABASE.md | DECISIONS.md is limited to about two pages |
| Planning | Proposed ignoring the whole data_candidate folder in Git | Ignore only the 100 MB historic file | docker compose needs the member states file to seed the database |
| Schema | Proposed a full unique index on conflicts (target, proposed value) | Made it partial: `WHERE status = 'Pending'` | A decided conflict must not block a later real change (Y -> A -> Y -> A) |
| Schema | Proposed `published_at` on `votes` | Put it on `imports` (file level); per-row values stay in `import_rows` | It describes the export file, not the vote |
| Schema | Proposed snapshot statuses Draft / Published / Unpublished | Calculating / Saved / Failed / Published; un-publish returns to Saved with an audit event; quarter nullable until published | Matches the background calculation and avoids an extra state |
| Schema | Named the position column `position` | Renamed to `desired_outcome` | Avoid the SQL keyword |
| Schema | Proposed the constraint tests in the same commit as the migration | Separate second commit | One meaningful change per commit |
| | | | |
