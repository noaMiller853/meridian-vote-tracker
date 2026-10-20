import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError, PROBLEM, SnapshotSummary, api, isAdmin } from '../api/client';
import { dateTime, parseIds, percent, snapshotTone } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError, createPoller } from '../shared';

type Mode = 'range' | 'session' | 'list';
interface Missing {
  assemblyVoteId: string;
  title: string;
}

/** Screen 8: create a snapshot, see all snapshots with their status, publish one per quarter (SNP-01, 02, 05, POS-04). */
@Component({
  selector: 'app-snapshots',
  imports: [FormsModule, RouterLink, ProblemBanner],
  template: `
    <h1>Snapshots</h1>
    <div class="page-sub">Saved calculations of the Alignment Index. A saved snapshot never changes.</div>

    <div class="grid-2">
      <div class="card" style="margin: 0">
        <h2>Create snapshot</h2>

        <label for="name">Name</label>
        <input id="name" type="text" [(ngModel)]="name" [class.invalid]="fieldError('name')" placeholder="e.g. 2024-Q1 (after corrections)" />
        @if (fieldError('name'); as m) {
          <div class="field-error">{{ m }}</div>
        }

        <label>Select votes</label>
        <div class="radios">
          <label><input type="radio" name="mode" [checked]="mode() === 'range'" (change)="setMode('range')" /> Date range</label>
          <label><input type="radio" name="mode" [checked]="mode() === 'session'" (change)="setMode('session')" /> Session</label>
          <label><input type="radio" name="mode" [checked]="mode() === 'list'" (change)="setMode('list')" /> List</label>
        </div>

        @if (mode() === 'range') {
          <div class="inline-fields">
            <div>
              <label for="from">From</label>
              <input id="from" type="date" [(ngModel)]="dateFrom" (ngModelChange)="refresh()" [class.invalid]="fieldError('dateFrom')" />
            </div>
            <div>
              <label for="to">To</label>
              <input id="to" type="date" [(ngModel)]="dateTo" (ngModelChange)="refresh()" [class.invalid]="fieldError('dateTo')" />
            </div>
          </div>
          @if (fieldError('dateFrom'); as m) {
            <div class="field-error">{{ m }}</div>
          }
          @if (fieldError('dateTo'); as m) {
            <div class="field-error">{{ m }}</div>
          }
        }
        @if (mode() === 'session') {
          <label for="session">Session number</label>
          <input id="session" type="number" min="0" [(ngModel)]="session" (ngModelChange)="refresh()" [class.invalid]="fieldError('session')" style="max-width: 140px" />
          @if (fieldError('session'); as m) {
            <div class="field-error">{{ m }}</div>
          }
        }
        @if (mode() === 'list') {
          <label for="ids">Vote IDs (separated by comma, space or new line)</label>
          <textarea id="ids" [(ngModel)]="idsText" (ngModelChange)="refresh()" [class.invalid]="fieldError('assemblyVoteIds')" placeholder="MA/2024/101, MA/2024/102"></textarea>
          @if (fieldError('assemblyVoteIds'); as m) {
            <div class="field-error">{{ m }}</div>
          }
        }

        <h3>{{ selected() }} vote{{ selected() === 1 ? '' : 's' }} selected</h3>

        @if (missing().length > 0) {
          <div class="alert alert-red">
            <strong>{{ missing().length }} selected vote{{ missing().length === 1 ? ' has' : 's have' }} no position</strong>
            @for (m of missing().slice(0, 5); track m.assemblyVoteId) {
              <div class="small">{{ m.assemblyVoteId }} · {{ m.title }}</div>
            }
            @if (missing().length > 5) {
              <div class="small">… and {{ missing().length - 5 }} more</div>
            }
            <div class="small">Set a position (<a routerLink="/votes">Votes</a>), remove it from the selection, or confirm that it is left out.</div>
            <div class="row mt">
              <button class="btn btn-small" type="button" (click)="useOthersAsList()" [disabled]="selected() === missing().length">Select the other {{ selected() - missing().length }} as a list</button>
            </div>
            <label style="display: flex; gap: 6px; align-items: center; font-weight: 400">
              <input type="checkbox" [(ngModel)]="excludeConfirmed" /> Create it without them. They are listed as excluded, with the reason.
            </label>
          </div>
        }

        <app-problem [error]="error()" [known]="['name', 'dateFrom', 'dateTo', 'session', 'assemblyVoteIds']" />
        @if (started(); as s) {
          <div class="alert alert-green">{{ s }}</div>
        }

        <button class="btn btn-primary" [disabled]="!canCreate() || busy()" (click)="create()">Create snapshot</button>
        <p class="muted small">Calculation runs in the background.</p>
      </div>

      <div class="card" style="margin: 0">
        <h2>All snapshots</h2>
        <app-problem [error]="listError()" />
        @if (publishProblem(); as p) {
          <div class="alert alert-amber">
            <strong>{{ p.error.problem.title }}</strong>
            <div>{{ p.error.problem.detail }}</div>
            @if (p.error.problem.type === quarterTaken) {
              <button class="btn btn-small mt" [disabled]="busy()" (click)="publish(p.id, true)">Publish this one instead</button>
            }
          </div>
        }
        <div class="table-scroll">
          <table>
            <thead><tr><th>Name</th><th>Quarter</th><th>Created</th><th>Status</th><th></th></tr></thead>
            <tbody>
              @for (s of snapshots(); track s.id) {
                <tr class="clickable" (click)="open(s.id)">
                  <td><a [routerLink]="['/snapshots', s.id]" (click)="$event.stopPropagation()">{{ s.name }}</a></td>
                  <td>{{ s.quarter ?? '—' }}</td>
                  <td class="small">{{ s.createdBy }} · {{ time(s.createdAt) }}</td>
                  <td>
                    <span class="chip chip-{{ tone(s.status) }}">{{ s.status === 'Calculating' ? 'Calculating' : s.status }}</span>
                    @if (s.status === 'Calculating') {
                      <div class="bar narrow"><i [style.width.%]="s.total ? pct(s) : 4"></i></div>
                      <div class="muted small">{{ s.phase ?? s.jobStatus }}</div>
                    }
                    @if (s.status === 'Failed') {
                      <div class="small tone-red">{{ s.error }}</div>
                    }
                  </td>
                  <td class="right">
                    @if (admin && s.status === 'Saved') {
                      <button class="btn btn-small" [disabled]="busy()" (click)="publish(s.id, false); $event.stopPropagation()">Mark as published</button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="muted">No snapshots yet.</td></tr>
              }
            </tbody>
          </table>
        </div>
        <p class="muted small">Admins can mark one snapshot per quarter as Published.</p>
      </div>
    </div>
  `,
})
export class Snapshots {
  private readonly router = inject(Router);
  protected readonly admin = isAdmin();
  protected readonly quarterTaken = PROBLEM.quarterPublished;

  protected readonly snapshots = signal<SnapshotSummary[]>([]);
  protected readonly listError = signal<ApiError | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly publishProblem = signal<{ id: number; error: ApiError } | null>(null);
  protected readonly started = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly mode = signal<Mode>('range');
  protected name = '';
  protected dateFrom = '';
  protected dateTo = '';
  protected session: number | null = null;
  protected idsText = '';
  protected excludeConfirmed = false;

  protected readonly selected = signal(0);
  protected readonly missing = signal<Missing[]>([]);
  private readonly selectedWithPosition = signal<string[]>([]);
  protected readonly fieldError = (f: string) => this.error()?.fieldError(f) ?? null;
  protected readonly canCreate = computed(() => this.name.trim().length > 0 && this.selected() > 0 && (this.missing().length === 0 || this.excludeConfirmed));

  protected readonly time = dateTime;
  protected readonly tone = snapshotTone;
  protected pct = (s: SnapshotSummary) => percent(s.done, s.total);
  private timer: ReturnType<typeof setTimeout> | undefined;

  // Status and progress of running calculations are read from the server every second (SNP-02): the user is never blocked.
  private readonly poller = createPoller(
    async () => this.snapshots.set(await api.snapshots.list(50)),
    () => this.snapshots().some((s) => s.status === 'Calculating'),
  );

  constructor() {
    this.poller.start().catch((e) => this.listError.set(asApiError(e)));
    const from = Number(inject(ActivatedRoute).snapshot.queryParamMap.get('from'));
    if (from) void this.prefill(from);
  }

  protected open(id: number) {
    void this.router.navigate(['/snapshots', id]);
  }

  /** "Create new snapshot with current data": the same selection again, calculated from today's approved data. */
  private async prefill(id: number) {
    try {
      const d = await api.snapshots.get(id);
      this.name = `${d.snapshot.name} (current data)`;
      const s = d.selection;
      if (s.assemblyVoteIds.length > 0) {
        this.mode.set('list');
        this.idsText = s.assemblyVoteIds.join('\n');
      } else if (s.session !== null) {
        this.mode.set('session');
        this.session = s.session;
      } else {
        this.mode.set('range');
        this.dateFrom = s.dateFrom ?? '';
        this.dateTo = s.dateTo ?? '';
      }
      this.excludeConfirmed = d.excludeVotesWithoutPosition;
      await this.compute();
    } catch (e) {
      this.error.set(asApiError(e));
    }
  }

  protected setMode(mode: Mode) {
    this.mode.set(mode);
    this.error.set(null);
    this.refresh();
  }

  protected refresh() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.compute(), 350);
  }

  /** How many votes the selection matches, and which of them have no position (POS-04), before anything is created. */
  private async compute() {
    this.started.set(null);
    try {
      const mode = this.mode();
      if (mode === 'list') {
        const ids = parseIds(this.idsText).slice(0, 200);
        const found = (await Promise.all(ids.map((id) => api.votes.list({ assemblyVoteId: id, take: 1 })))).flatMap((p) => p.items);
        this.selected.set(ids.length ? found.length : 0);
        this.missing.set(found.filter((v) => !v.hasPosition).map((v) => ({ assemblyVoteId: v.assemblyVoteId, title: v.title })));
        this.selectedWithPosition.set(found.filter((v) => v.hasPosition).map((v) => v.assemblyVoteId));
        return;
      }
      const q = mode === 'range' ? { dateFrom: this.dateFrom || undefined, dateTo: this.dateTo || undefined } : { session: this.session ?? undefined };
      if (Object.values(q).every((v) => v === undefined)) {
        this.selected.set(0);
        this.missing.set([]);
        return;
      }
      const [all, without, withPos] = await Promise.all([
        api.votes.list({ ...q, take: 1 }),
        api.votes.list({ ...q, hasPosition: false, take: 500 }),
        api.votes.list({ ...q, hasPosition: true, take: 500 }),
      ]);
      this.selected.set(all.total);
      this.missing.set(without.items.map((v) => ({ assemblyVoteId: v.assemblyVoteId, title: v.title })));
      this.selectedWithPosition.set(withPos.items.map((v) => v.assemblyVoteId));
    } catch (e) {
      this.error.set(asApiError(e));
    }
  }

  /** Remove the votes without a position from the selection: keep only the other votes, as a list. */
  protected useOthersAsList() {
    this.idsText = this.selectedWithPosition().join('\n');
    this.mode.set('list');
    this.excludeConfirmed = false;
    void this.compute();
  }

  protected async create() {
    this.busy.set(true);
    this.error.set(null);
    this.started.set(null);
    const mode = this.mode();
    try {
      const created = await api.snapshots.create({
        name: this.name.trim(),
        dateFrom: mode === 'range' ? this.dateFrom || undefined : undefined,
        dateTo: mode === 'range' ? this.dateTo || undefined : undefined,
        session: mode === 'session' ? (this.session ?? undefined) : undefined,
        assemblyVoteIds: mode === 'list' ? parseIds(this.idsText) : undefined,
        excludeVotesWithoutPosition: this.excludeConfirmed,
      });
      this.started.set(`Snapshot ${created.snapshotId} started with ${created.selectedVotes} votes. It is calculated in the background.`);
      this.name = '';
      await this.poller.start();
    } catch (e) {
      const err = asApiError(e);
      if (err.isConflict && err.problem.type === PROBLEM.votesWithoutPosition) {
        // The server found votes without a position that the screen did not know about: show them, ask to confirm.
        const ids = err.problem.votesWithoutPosition?.assemblyVoteIds ?? [];
        this.missing.set(ids.map((id) => ({ assemblyVoteId: id, title: '' })));
      }
      this.error.set(err);
    } finally {
      this.busy.set(false);
    }
  }

  protected async publish(id: number, replace: boolean) {
    this.busy.set(true);
    this.publishProblem.set(null);
    try {
      await api.snapshots.publish(id, replace);
      await this.poller.start();
      this.snapshots.set(await api.snapshots.list(50));
    } catch (e) {
      this.publishProblem.set({ id, error: asApiError(e) });
    } finally {
      this.busy.set(false);
    }
  }
}
