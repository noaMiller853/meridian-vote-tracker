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
| | | | |
