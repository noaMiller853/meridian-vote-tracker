# Bonus: vote extraction from scanned sheets (design, not built)

Status: **designed, not built.** The core came first. This document is the design for PDF-01..PDF-06 (assignment section 8), written so it can be built on the existing pipeline without changing its rules.

## 1. What the sheets look like
Checked on `MA_2011_042.pdf` and `MA_2011_042_corrigendum.pdf` (the six sheets are 2007-2014):
- Scans with no text layer: every page is one embedded JPEG. Two pages per sheet.
- Every page repeats the header: Vote No. (`MA/2011/042`), Session (`66`), Date of vote written out (`4 October 2011`), Resolution, and "Page 1 of 2".
- The table is printed twice side by side per page, with the columns **Code, Member state, YES, NO, ABST.** A vote is a hand-drawn X in one of the three ballot cells. **Every row prints the three-letter country code.**
- A row with no mark is "not voting". The last page ends with the printed totals, for example "Recorded totals: Yes: 70 No: 55 Abstentions: 7 Not voting: 16" (148 countries), and "Certified correct".
- The corrigendum carries a rotated **CORRIGENDUM stamp across the header row of the right-hand table**, over "Member state", "YES" and "NO". On page 1, at least **KEN changes from YES to ABST.**
- The historic CSV starts on 2015-01-07, so none of the six votes exists in the approved data.

## 2. Flow
```
upload PDF ──> IFileStore ──> background job ──> Document Intelligence (layout)
                                                    │
                       deterministic parsing: header, columns, rows, marks, totals
                                                    │
                     only what is still unresolved ──> LLM (strict JSON schema)
                                                    │
                    Domain validation (rules below) ──> Review: edit / approve / reject
                                                    │ approved
                       rows as from a CSV ──> RowValidator + VoteClassifier (New, Unchanged, Conflict ...)
```

## 3. PDF-01: upload and status
- `POST /api/pdf-extractions` (multipart, Analyst or Admin) answers 202 at once, like `POST /api/imports`. The file goes through the existing `IFileStore` (Blob Storage `dotnetcandidate2st` when configured, else local), content-addressed. `SaveAsync` gains an extension so a PDF is stored as `{sha256}.pdf`. Size limit, `application/pdf` and the `%PDF` signature are checked before anything else.
- A `background_jobs` row of a new type `PdfExtraction` (phases `Analyzing`, `Resolving`, `Validating`) is processed by a `PdfExtractionWorker`, built like the snapshot worker: advisory lock, `FOR UPDATE SKIP LOCKED`, progress on its own connection, a job left Running after a restart becomes Failed.
- The screen polls `GET /api/pdf-extractions/{id}` every second, as for imports, so status survives a refresh.

## 4. PDF-02: Document Intelligence, then an LLM only where needed
**Document Intelligence first.** `prebuilt-layout` on `dotnetcandidate2-di` returns, per page, the lines (for the header and the totals), the tables with their cells and bounding regions, and selection marks, each with a confidence. That gives structure and a **measurable confidence per cell**, which is what the review screen and the quality metrics need.

**Deterministic parsing of the DI result** (pure Domain code, unit-tested on recorded DI output):
- Header fields by regex on the lines (`Vote No.:`, `Session:`, `Date of vote:`, `Resolution:`), the same on every page. The date is parsed exactly as `d MMMM yyyy` (invariant culture) and reported as a normalization to `yyyy-mm-dd`.
- Columns by their header text (`Code`, `Member state`, `YES`, `NO`, `ABST.`). If a header row cannot be read, as in the stamped right-hand table of the corrigendum, the column positions are taken from the other table on the same page, which has the same layout.
- A ballot cell counts as marked when DI returns a selected selection mark or an X-like glyph in it; its confidence is that element's confidence. Whether `prebuilt-layout` reports the hand-drawn X as a selection mark or as text must be checked on the labelled sheets. If neither is reliable, the next step is a custom DI model trained on the labelled sheets.
- Totals by regex on the last page.

**The LLM is a conditional second step (decided from the real sheets).** Every row already prints the ISO code, so mapping country names to codes is not needed in the normal case. When everything above is clear, DI alone decides and the LLM is not called. The LLM is called only for what DI and the deterministic parsing could not resolve:
- a header row the parser cannot read even with the other table's layout (the CORRIGENDUM stamp is the real example);
- a code cell that cannot be read, or whose text is not a known code;
- a printed code that disagrees with the printed name.

It receives only that fragment of the DI result (the cells, their text and positions) plus the list of known countries, never the instructions of the page. It must answer in a strict JSON schema whose country code is an **enum of the known codes**, so it can choose but cannot invent. Why the combination: DI gives structure and a measurable confidence; an LLM handles messy headers and names; an LLM alone gives no reliable per-cell confidence. Every value the LLM resolved is flagged for review, whatever the model says.

**Provider.** Behind one interface (`IExtractionResolver`). Default: **Azure OpenAI with structured outputs** (`json_schema`, `strict: true`): the service enforces the schema. Alternative: **Claude through Foundry** with a tool input schema; there the answer is also checked against the schema locally. Endpoints and deployment names come from configuration; authentication through the existing `AzureCredentialFactory` (a token per resource, else `DefaultAzureCredential`).

**Correlation id (section 9).** The id of the upload request is stored on the job, is in the worker's log scope, and is sent as `x-ms-client-request-id` on every Document Intelligence and Azure OpenAI call. Application Insights records those calls as dependencies of the job, so one query shows the request, the job, the AI calls and their results. The DI operation id is stored on the extraction for support.

## 5. PDF-03: the output is untrusted
A pure Domain `ExtractionValidator` with unit tests checks everything before the extraction reaches review:

| Rule | Result |
|---|---|
| The result matches the expected schema (header, rows, totals) | error |
| Vote id `MA/yyyy/nnn`; date a real date; session a positive whole number; the header identical on every page; all pages present ("Page n of N") | error |
| Every code is a known country | error |
| The printed code matches the printed name (seed name or a known alias, for example "Türkiye", "Viet Nam") | flag |
| Every country is a member on the vote date; a member of that date missing from the sheet is listed, not invented | error / flag |
| Each country appears once | error |
| At most one mark per row; a blank row is X (absent, "not voting") | error |
| Computed totals equal the printed totals (Yes, No, Abstentions, Not voting) | error |
| Confidence below the threshold, or a value resolved by the LLM | flag |

**Threshold: 0.80 as a starting value**, calibrated on the hand-labelled sheets (section 9 of this document): the value where flags still catch the real errors with the fewest unnecessary flags. Errors block approval until an analyst fixes them; flags are highlighted.

## 6. PDF-04: review
- The extraction appears in the Review inbox next to conflicts, with its status and number of errors and flags.
- The detail shows the scanned page beside the extracted table; the cell under the cursor and every flagged cell are highlighted on the page using DI's bounding regions. The analyst can edit a code or a ballot, then approve or reject with a note.
- Edits use a version check like positions (`expectedVersion`, 409 on a change in between). Every action is an `audit_events` row: `ExtractionEdited` (old and new value), `ExtractionApproved`, `ExtractionRejected`, with who, when and the correlation id.

## 7. PDF-05: nothing approved without review
- Approval does not write approved data. It turns the reviewed extraction into the same rows a CSV gives (vote id, session, ISO date, title, code, printed name, ballot) and runs them as an import (`imports.kind = 'Pdf'`, the stored PDF as its file) through the same `RowValidator` and `VoteClassifier`: New, Unchanged, Duplicate, Invalid, Contradiction or Conflict, with the same import report and the same rule that only a person's decision changes an approved value.
- **Natural test:** approve `MA_2011_042.pdf`, and MA/2011/042 is created as New. Then approve `MA_2011_042_corrigendum.pdf`: unchanged countries are Unchanged and the corrections open conflicts against the original, at least KEN YES → ABST. Approving the corrigendum again opens nothing (already pending).

## 8. PDF-06: failures are shown, nothing waits forever
- On 429, 503, 408 or a network error: retry with exponential backoff and jitter (2 s, 4 s, 8 s), honouring `Retry-After`, at most 3 attempts per call.
- Timeouts: polling a DI analysis 2 minutes, one LLM call 60 seconds, the whole job 10 minutes.
- Then the job is Failed with a readable message, for example "Document Intelligence did not answer after 3 attempts (503). Try again later." The details go to the server log only.
- A Retry button starts a new job on the same stored file. A job left Running after a restart becomes Failed. No job waits forever.

## 9. Measuring extraction quality
- **Ground truth:** the 6 sheets hand-labelled (header, every country's ballot, the printed totals), checked against the printed totals, stored as JSON next to the tests.
- **Metrics, per sheet and overall, stored per run with the DI model and LLM deployment used:**
  - country-level accuracy: ballots equal to the ground truth ÷ countries on the sheet;
  - the share of sheets whose computed totals match the printed totals;
  - the share of rows the analyst edited before approving;
  - whether the low-confidence flags catch the real errors: errors that were flagged in advance (recall) against flags that were not needed (precision).
- The threshold is set from the last metric. Six sheets (about 900 rows) give an indication, not a statistic; the same set is rerun after any change of model or parser.

## 10. Data model and API (sketch)
- `pdf_extractions`: the stored file, the job, status (`Queued`, `Running`, `NeedsReview`, `Approved`, `Rejected`, `Failed`), the header as extracted, the printed totals, the DI model and LLM deployment, the threshold used, the DI operation id, the resulting import, decided by and at, version.
- `pdf_extraction_rows`: page, table, row, code, printed name, ballot, confidence per field, source (DI, LLM or analyst), flags and bounding region (jsonb).
- The raw DI result is stored as a JSON file in the file store, so an extraction can be re-parsed without calling DI again.
- Endpoints: `POST /api/pdf-extractions`, `GET /api/pdf-extractions` and `/{id}`, `GET /{id}/file`, `PUT /{id}/rows/{rowId}` (with `expectedVersion`), `POST /{id}/approve`, `POST /{id}/reject`, `POST /{id}/retry`.

## 11. Security
- The PDF and everything extracted from it are data, never instructions. The LLM has no tools, sees only fragments of the DI result, must answer in the schema, and its answer is validated again; it never writes approved data.
- No key in code: endpoints from configuration, Entra authentication through `AzureCredentialFactory`.

## 12. Effort and order
About 4 to 5 developer days: DI client and parser with recorded DI output (1.5), Domain validator (0.5), worker, retries and timeouts (1), review screen with page highlights (1), approval into the import pipeline (0.5), labelling and the quality run (0.5). The labelled sheets come first: they decide whether `prebuilt-layout` reads the marks well enough and set the threshold.
