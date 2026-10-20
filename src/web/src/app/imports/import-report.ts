import { Component, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, ImportDetails, ImportRowDto, ImportVoteDto, api } from '../api/client';
import { classificationTone, dateTime, importActive, importChip, percent } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError, createPoller } from '../shared';

/** The stored report (ING-14, ING-15). Its summary is free-form JSON in the contract, so it is typed here. */
interface Bucket {
  opened: number;
  alreadyPending: number;
  alreadyRejected: number;
  alreadySuperseded: number;
}
interface ReportSummary {
  lines?: number;
  warnings?: string[];
  votes?: { inFile: number; created: number; existing: number };
  conflicts?: Bucket & { ballot?: Bucket; voteField?: Bucket };
  normalizations?: { rows: number; byField: Record<string, { rows: number; values: { original: string; normalized: string; rows: number }[]; moreValues: number }> };
  emptyOptionalFields?: Record<string, number>;
}

/** Screens 2 and 3: the report of one import. */
@Component({
  selector: 'app-import-report',
  imports: [RouterLink, ProblemBanner],
  template: `
    <p><a routerLink="/imports">← Imports</a></p>
    <app-problem [error]="error()" />

    @if (details(); as d) {
      <h1>Import report · {{ d.import.label }}</h1>
      <div class="page-sub">
        Uploaded by {{ d.import.uploadedBy }} · {{ time(d.import.uploadedAt) }}
        @if (d.import.publishedAt) {
          · published by the Assembly {{ time(d.import.publishedAt) }}
        }
        · <span class="chip chip-{{ chip().tone }}">{{ chip().text }}</span>
      </div>

      @if (d.import.status === 'Failed') {
        <div class="alert alert-red">
          <strong>The import failed and nothing was stored.</strong>
          <div>{{ d.import.error }}</div>
        </div>
      } @else if (active()) {
        <div class="card">
          <strong>{{ d.import.status === 'Queued' ? 'Waiting to start…' : (d.import.phase ?? 'Processing') + '…' }}</strong>
          <div class="bar mt"><i [style.width.%]="d.import.total ? pct() : 4"></i></div>
          <div class="muted small">{{ d.import.done.toLocaleString('en-US') }} @if (d.import.total) { of {{ d.import.total.toLocaleString('en-US') }} } rows</div>
        </div>
      } @else {
        <div class="stats">
          <div class="stat"><b class="tone-green">{{ d.import.counts.new.toLocaleString('en-US') }}</b><span>New rows</span></div>
          <div class="stat"><b>{{ d.import.counts.unchanged.toLocaleString('en-US') }}</b><span>Unchanged rows</span></div>
          <div class="stat"><b>{{ d.import.counts.duplicate.toLocaleString('en-US') }}</b><span>Duplicate rows</span></div>
          <div class="stat"><b [class.tone-red]="d.import.counts.invalid > 0">{{ d.import.counts.invalid.toLocaleString('en-US') }}</b><span>Invalid rows</span></div>
          <div class="stat"><b [class.tone-amber]="d.import.counts.contradiction > 0">{{ d.import.counts.contradiction.toLocaleString('en-US') }}</b><span>Contradiction rows</span></div>
          <div class="stat"><b [class.tone-amber]="d.import.counts.conflict > 0">{{ d.import.counts.conflict.toLocaleString('en-US') }}</b><span>Conflict rows</span></div>
        </div>

        @if (summary().conflicts; as c) {
          @if (c.opened + c.alreadyPending + c.alreadyRejected + c.alreadySuperseded > 0) {
            <div class="alert alert-amber">
              <strong>Approved data was not changed.</strong>
              <div>
                {{ c.opened }} new conflict(s) were sent to <a routerLink="/review">Review</a> · {{ c.alreadyPending }} already waiting for a decision · {{ c.alreadyRejected }} already rejected ·
                {{ c.alreadySuperseded }} already superseded. Nothing is overwritten without a human decision.
              </div>
            </div>
          }
        }
        @for (w of summary().warnings ?? []; track w) {
          <div class="alert alert-amber">{{ w }}</div>
        }

        <div class="card">
          <h2>Per vote</h2>
          @if (summary().votes; as v) {
            <p class="muted small">{{ v.inFile }} votes in the file · {{ v.created }} created · {{ v.existing }} already existed</p>
          }
          <div class="table-scroll">
            <table>
              <thead>
                <tr><th>Vote</th><th>Outcome</th><th>Rows</th><th>Details</th></tr>
              </thead>
              <tbody>
                @for (v of votes(); track v.assemblyVoteId) {
                  <tr>
                    <td>
                      @if (v.voteId) {
                        <a [routerLink]="['/votes', v.voteId]">{{ v.assemblyVoteId }}</a>
                      } @else {
                        {{ v.assemblyVoteId }}
                      }
                    </td>
                    <td>
                      <span class="chip chip-{{ voteTone(v) }}">{{ voteStatus(v) }}</span>
                    </td>
                    <td>{{ v.rows.toLocaleString('en-US') }}</td>
                    <td class="small">
                      {{ voteDetail(v) }}
                      @if (v.contradictionReason) {
                        <div class="muted">{{ v.contradictionReason }}</div>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="muted">No votes.</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (votesNext() !== null) {
            <p><button class="btn btn-small" (click)="moreVotes()">Load more votes</button></p>
          }
        </div>

        <div class="card">
          <h2>Rows that were not stored cleanly ({{ d.import.counts.invalid + d.import.counts.duplicate + d.import.counts.contradiction + d.import.counts.conflict }})</h2>
          <div class="table-scroll">
            <table>
              <thead>
                <tr><th>Row</th><th>Vote</th><th>Country</th><th>Value</th><th>Outcome</th><th>Reason</th></tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.line) {
                  <tr>
                    <td class="mono">{{ r.line }}</td>
                    <td>{{ r.assemblyVoteId ?? '—' }}</td>
                    <td>
                      {{ r.countryCode ?? '—' }}
                      @if (r.originalCountryCode && r.originalCountryCode !== r.countryCode) {
                        <div class="muted small">file: {{ r.originalCountryCode }}</div>
                      }
                    </td>
                    <td [title]="r.rawLine">{{ r.ballot ?? r.originalValue ?? '—' }}</td>
                    <td><span class="chip chip-{{ tone(r.classification) }}">{{ r.classification }}</span></td>
                    <td class="small">
                      {{ r.reason }}
                      @if (r.conflictAction) {
                        <span class="muted"> · {{ r.conflictAction }}</span>
                      }
                      @if (r.originalValue && r.classification === 'Invalid') {
                        <div class="muted mono">original: {{ r.originalValue }}</div>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="6" class="muted">Every row was stored.</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (rowsNext() !== null) {
            <p><button class="btn btn-small" (click)="moreRows()">Load more rows</button></p>
          }
          <p class="muted small">Valid rows were stored. Problems are reported with row number, reason and original value, never silently fixed.</p>
        </div>

        @if (normalized().length > 0 || emptyOptional().length > 0) {
          <div class="card">
            <h2>Normalizations and empty fields</h2>
            @for (n of normalized(); track n.field) {
              <h3>{{ n.field }} · {{ n.rows }} row(s) normalized</h3>
              <ul class="small">
                @for (v of n.values; track v.original) {
                  <li><span class="mono">{{ v.original }}</span> → <span class="mono">{{ v.normalized }}</span> ({{ v.rows }})</li>
                }
                @if (n.moreValues > 0) {
                  <li class="muted">… and {{ n.moreValues }} more value(s)</li>
                }
              </ul>
            }
            @for (e of emptyOptional(); track e.field) {
              <div class="small">{{ e.field }} was empty on {{ e.rows.toLocaleString('en-US') }} row(s).</div>
            }
          </div>
        }
      }
    }
  `,
})
export class ImportReport {
  private readonly id = Number(inject(ActivatedRoute).snapshot.paramMap.get('id'));
  protected readonly details = signal<ImportDetails | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly rows = signal<ImportRowDto[]>([]);
  protected readonly rowsNext = signal<number | null>(null);
  protected readonly votes = signal<ImportVoteDto[]>([]);
  protected readonly votesNext = signal<string | null>(null);

  protected readonly active = computed(() => !!this.details() && importActive(this.details()!.import));
  protected readonly chip = computed(() => importChip(this.details()!.import));
  protected readonly summary = computed<ReportSummary>(() => (this.details()?.report ?? {}) as ReportSummary);
  protected readonly normalized = computed(() =>
    Object.entries(this.summary().normalizations?.byField ?? {}).map(([field, v]) => ({ field, ...v })),
  );
  protected readonly emptyOptional = computed(() =>
    Object.entries(this.summary().emptyOptionalFields ?? {})
      .filter(([, n]) => n > 0)
      .map(([field, rows]) => ({ field, rows })),
  );

  protected readonly time = dateTime;
  protected readonly tone = classificationTone;
  protected pct = () => percent(this.details()!.import.done, this.details()!.import.total);

  private loaded = false;
  private readonly poller = createPoller(
    async () => {
      this.details.set(await api.imports.get(this.id));
      if (!this.active() && !this.loaded) {
        this.loaded = true;
        await Promise.all([this.moreRows(), this.moreVotes()]);
      }
    },
    () => this.active() || !this.details(),
  );

  constructor() {
    this.poller.start().catch((e) => this.error.set(asApiError(e)));
  }

  protected async moreRows() {
    try {
      const page = await api.imports.rows(this.id, { after: this.rowsNext() ?? 0, take: 100 });
      this.rows.update((r) => [...r, ...page.items]);
      this.rowsNext.set(page.nextAfter);
    } catch (e) {
      this.error.set(asApiError(e));
    }
  }

  protected async moreVotes() {
    try {
      const page = await api.imports.votes(this.id, { after: this.votesNext() ?? undefined, take: 100 });
      this.votes.update((v) => [...v, ...page.items]);
      this.votesNext.set(page.nextAfter);
    } catch (e) {
      this.error.set(asApiError(e));
    }
  }

  protected voteStatus(v: ImportVoteDto): string {
    if (v.voteCreated) return 'New vote';
    return v.status === 'Existing' ? 'Existing vote' : v.status;
  }

  protected voteTone(v: ImportVoteDto): string {
    if (v.status === 'NoValidDetails') return 'red';
    if (v.status === 'Contradiction') return 'amber';
    return v.voteCreated ? 'green' : 'grey';
  }

  protected voteDetail(v: ImportVoteDto): string {
    const c = v.counts;
    const parts = [
      c.new && `${c.new} new`,
      c.unchanged && `${c.unchanged} unchanged`,
      c.duplicate && `${c.duplicate} duplicate`,
      c.invalid && `${c.invalid} invalid`,
      c.contradiction && `${c.contradiction} contradiction`,
      c.conflict && `${c.conflict} conflict`,
      v.conflictsOpened && `${v.conflictsOpened} conflict(s) opened`,
      v.conflictsAlreadyPending && `${v.conflictsAlreadyPending} already waiting`,
      v.conflictsAlreadyRejected && `${v.conflictsAlreadyRejected} already rejected`,
      v.conflictsAlreadySuperseded && `${v.conflictsAlreadySuperseded} already superseded`,
    ].filter(Boolean);
    return parts.length ? parts.join(' · ') : 'No action';
  }
}
