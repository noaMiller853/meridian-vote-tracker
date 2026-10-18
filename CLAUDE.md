# Meridian Vote Tracker

Home assignment (Senior .NET full-stack lead). Requirement IDs (ING-08, CON-05, SNP-04...) refer to the assignment spec. Decisions and trade-offs live in DECISIONS.md.

## Stack and layout
ASP.NET Core (.NET 8+), EF Core + Npgsql, PostgreSQL (Docker), Angular, OpenAPI, xUnit + Testcontainers.

- `src/Meridian.Api`: controllers, OpenAPI, error handling
- `src/Meridian.Domain`: pure business rules, no DB access
- `src/Meridian.Infrastructure`: EF Core, migrations, background workers, file storage
- `src/web`: Angular app
- `tests/Meridian.Tests`: unit and integration tests

## Commands (update as they come to exist)
- `docker compose up` : whole system
- `dotnet build` / `dotnet test`
- `dotnet tool restore`, then `dotnet ef database update --project src/Meridian.Infrastructure --startup-project src/Meridian.Api` (connection string from user-secrets or `ConnectionStrings__Default`)
- New migration: `dotnet ef migrations add <Name> --project src/Meridian.Infrastructure --startup-project src/Meridian.Api --output-dir Persistence/Migrations`
- `ng serve` (inside `src/web`)

## Domain rules that must never be broken
- Approved data is never overwritten automatically (ING-08). Only a human decision on a conflict changes it.
- A vote missing from a new file is not deleted or changed (ING-09). Countries missing from an existing vote are added as New, not as conflicts (ING-10).
- Country identity is `country_code`. Names are descriptive only (ING-11).
- Re-uploading the same or an overlapping file creates no duplicates and does not reopen a pending conflict (ING-12).
- A bad row never fails the file (ING-07). Problems are reported with row number, reason and original value.
- Only normalization allowed: trim and upper-case `country_code`. It is always shown in the import report (ING-13).
- Each row gets exactly one classification: New, Unchanged, Duplicate, Invalid, Contradiction or Conflict.
- Deciding a conflict is an atomic `UPDATE ... WHERE status = 'Pending'`. The second decider gets HTTP 409 with who, when and what was decided. Decision, approved-value update and audit event happen in one transaction.
- Snapshots are frozen copies and never change (SNP-04). Only Y and N count in scoring; see DECISIONS.md for the full rules.
- Every change writes an `audit_events` row: who, when, old value, new value, source.
- Roles are enforced in the API, not only in the UI.

## Conventions
- Business rules are pure functions in Domain with unit tests.
- Large imports: stream the file and bulk `COPY`. Never load the whole file into memory or insert row by row with EF.
- One error format across the API (ProblemDetails). A correlation ID flows from the UI through the API into every log line.
- No secrets or connection strings in code. Use user-secrets or `.env` (gitignored).
- The Angular API client is generated from the OpenAPI contract, never handwritten.
- Local first. Azure is connected only through configuration (for example behind an `IFileStore` interface).

## Workflow
- Plan first and wait for approval before writing code. Work in small steps with one meaningful commit each.
- Add tests together with each rule.
- If the spec is ambiguous, ask instead of guessing, then record the decision in DECISIONS.md.
- When something Claude produced is rejected or corrected, add a line to USAGE_AI.md.
