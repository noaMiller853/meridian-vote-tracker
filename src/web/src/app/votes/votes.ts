import { Component, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, VoteSummary, api } from '../api/client';
import { dateOnly, stance } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError } from '../shared';

/** Screen 6: the votes with the institute's position and weight (POS-04: no position is shown clearly). */
@Component({
  selector: 'app-votes',
  imports: [FormsModule, RouterLink, ProblemBanner],
  template: `
    <h1>Votes</h1>
    <div class="page-sub">All votes in the system, with Northwind's position and weight.</div>

    <div class="card">
      <div class="inline-fields">
        <div>
          <label for="session">Session</label>
          <input id="session" type="number" min="0" [(ngModel)]="session" (ngModelChange)="changed()" />
          @if (fieldError('session'); as m) {
            <div class="field-error">{{ m }}</div>
          }
        </div>
        <div>
          <label for="from">From</label>
          <input id="from" type="date" [(ngModel)]="dateFrom" (ngModelChange)="changed()" [class.invalid]="fieldError('dateFrom')" />
          @if (fieldError('dateFrom'); as m) {
            <div class="field-error">{{ m }}</div>
          }
        </div>
        <div>
          <label for="to">To</label>
          <input id="to" type="date" [(ngModel)]="dateTo" (ngModelChange)="changed()" [class.invalid]="fieldError('dateTo')" />
        </div>
        <div class="grow" style="min-width: 200px">
          <label for="text">Search title or vote ID</label>
          <input id="text" type="search" [(ngModel)]="text" (ngModelChange)="changed()" placeholder="Search title or vote ID…" />
        </div>
        <div>
          <label style="display: flex; gap: 6px; align-items: center; font-weight: 400">
            <input type="checkbox" [(ngModel)]="withoutOnly" (ngModelChange)="changed()" /> Without position only
          </label>
        </div>
      </div>
    </div>

    <app-problem [error]="error()" [known]="['dateFrom', 'dateTo', 'session']" />

    @if (missing() > 0) {
      <div class="alert alert-red">
        <strong>{{ missing() }} vote{{ missing() === 1 ? ' has' : 's have' }} no position.</strong>
        <div>Votes without a position are never silently included in a snapshot.</div>
      </div>
    }

    <div class="card">
      <div class="table-scroll">
        <table>
          <thead>
            <tr>
              <th>Vote</th><th>Date</th><th>Title</th><th class="right">Country votes</th><th>Position</th><th class="right">Weight</th><th>Open conflicts</th>
            </tr>
          </thead>
          <tbody>
            @for (v of votes(); track v.id) {
              <tr class="clickable" [class.warn]="!v.hasPosition" (click)="open(v.id)">
                <td><a [routerLink]="['/votes', v.id]" (click)="$event.stopPropagation()">{{ v.assemblyVoteId }}</a></td>
                <td>{{ date(v.voteDate) }}</td>
                <td>{{ v.title }}</td>
                <td class="right">{{ v.countryVotes }}</td>
                <td>
                  @if (v.position; as p) {
                    {{ stance(p.stance) }}
                  } @else {
                    <span class="chip chip-red">No position</span>
                  }
                </td>
                <td class="right">{{ v.position?.weight ?? '—' }}</td>
                <td>
                  @if (v.pendingConflicts > 0) {
                    <span class="chip chip-amber">{{ v.pendingConflicts }} pending</span>
                  } @else {
                    —
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="muted">{{ loading() ? 'Loading…' : 'No votes match.' }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <p class="muted small">
        {{ votes().length }} of {{ total() }} shown. Click a vote to see its country votes, set its position (admin only) and view its full history.
        @if (next() !== null) {
          <button class="btn btn-small" (click)="load(true)">Load more</button>
        }
      </p>
    </div>
  `,
})
export class Votes {
  protected readonly votes = signal<VoteSummary[]>([]);
  protected readonly total = signal(0);
  protected readonly missing = signal(0);
  protected readonly next = signal<number | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly loading = signal(true);
  protected readonly fieldError = (f: string) => this.error()?.fieldError(f) ?? null;
  protected readonly date = dateOnly;
  protected readonly stance = stance;

  protected session: number | null = null;
  protected dateFrom = '';
  protected dateTo = '';
  protected text = '';
  protected withoutOnly = false;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(private readonly router: Router) {
    void this.load(false);
  }

  protected open(id: number) {
    void this.router.navigate(['/votes', id]);
  }

  protected changed() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.load(false), 350);
  }

  private query() {
    return {
      session: this.session ?? undefined,
      dateFrom: this.dateFrom || undefined,
      dateTo: this.dateTo || undefined,
      text: this.text.trim() || undefined,
    };
  }

  protected async load(more: boolean) {
    this.error.set(null);
    this.loading.set(true);
    try {
      const q = this.query();
      const page = await api.votes.list({ ...q, hasPosition: this.withoutOnly ? false : undefined, afterId: more ? (this.next() ?? 0) : 0, take: 100 });
      this.votes.set(more ? [...this.votes(), ...page.items] : page.items);
      this.total.set(page.total);
      this.next.set(page.nextAfterId);
      // POS-04: how many of the matching votes have no position, whatever page is shown.
      this.missing.set(this.withoutOnly ? page.total : (await api.votes.list({ ...q, hasPosition: false, take: 1 })).total);
    } catch (e) {
      this.error.set(asApiError(e));
    } finally {
      this.loading.set(false);
    }
  }
}
