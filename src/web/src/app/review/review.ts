import { Component, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { ApiError, ConflictDetails, ConflictListItem, PROBLEM, api } from '../api/client';
import { ago, ballot, dateTime } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError } from '../shared';

/** Screens 4 and 5: the review queue (CON-01..CON-06). */
@Component({
  selector: 'app-review',
  imports: [FormsModule, RouterLink, ProblemBanner],
  template: `
    <h1>Review</h1>
    <div class="page-sub">Items waiting for a human decision.</div>

    <div class="grid-2">
      <div class="card" style="margin: 0">
        <input type="search" placeholder="Filter by vote, e.g. MA/2024/101" [ngModel]="filter()" (ngModelChange)="onFilter($event)" aria-label="Filter by vote" />
        <div class="tabs">
          <button [class.on]="tab() === 'Pending'" (click)="setTab('Pending')">Conflicts {{ pendingTotal() }}</button>
          <button [class.on]="tab() === 'Resolved'" (click)="setTab('Resolved')">Resolved {{ resolvedTotal() }}</button>
        </div>
        @for (c of items(); track c.id) {
          <div class="list-item" [class.selected]="c.id === selectedId()" (click)="select(c.id)" tabindex="0" (keydown.enter)="select(c.id)">
            <div class="row" style="justify-content: space-between">
              <strong>{{ c.assemblyVoteId }}</strong>
              <span class="chip chip-{{ c.status === 'Pending' ? 'amber' : c.status === 'Approved' ? 'green' : 'grey' }}">{{ c.status }}</span>
            </div>
            <div class="muted small">{{ summary(c) }}</div>
          </div>
        } @empty {
          <div class="muted">{{ tab() === 'Pending' ? 'Nothing is waiting for a decision.' : 'No resolved conflicts yet.' }}</div>
        }
        @if (listError()) {
          <app-problem [error]="listError()" />
        }
      </div>

      <div class="card" style="margin: 0">
        @if (detail(); as d) {
          <div class="row" style="justify-content: space-between">
            <h2 style="margin: 0">
              {{ d.kind === 'CountryVote' ? 'Country vote conflict' : 'Vote detail conflict' }} · {{ d.vote.assemblyVoteId }}
              @if (d.kind === 'CountryVote') {
                · {{ d.countryName }} ({{ d.countryCode }})
              } @else {
                · {{ d.fieldName }}
              }
            </h2>
            <span class="chip chip-{{ d.status === 'Pending' ? 'amber' : d.status === 'Approved' ? 'green' : 'grey' }}">{{ d.status }}</span>
          </div>
          <div class="muted small">
            {{ d.vote.title }} · Session {{ d.vote.session }} · {{ d.vote.voteDate }} ·
            <a [routerLink]="['/votes', d.vote.voteId]">open the vote</a>
          </div>

          @if (notice(); as n) {
            <div class="alert alert-amber mt">
              <strong>{{ n.title }}</strong>
              <div>{{ n.detail }}</div>
            </div>
          }
          <app-problem [error]="error()" [known]="['note', 'decision', 'expectedApprovedValue']" />

          @if (d.status === 'Pending' && d.current.isStale) {
            <div class="alert alert-amber mt">
              <strong>The approved value changed after this conflict was opened.</strong>
              <div>
                It is now {{ show(d, d.current.value) }} (it was {{ show(d, d.approved.value) }}).
                @if (d.current.changedBy; as by) {
                  Changed by conflict {{ by.conflictId }}, approved by {{ by.decidedBy }} {{ time(by.decidedAt) }}.
                }
                Rejecting is always possible. To approve the incoming value anyway, confirm the current value below.
              </div>
            </div>
          }

          @if (d.decision; as dec) {
            <div class="alert alert-blue mt">
              <strong>{{ dec.decidedBy }} {{ dec.decision.toLowerCase() }} this conflict {{ ago(dec.decidedAt) }}.</strong>
              <div class="small">{{ time(dec.decidedAt) }}</div>
            </div>
          }

          <div class="value-pair">
            @if (d.decision?.decision === 'Approved') {
              <div class="value-card approved">
                <div class="label">APPROVED VALUE (CURRENT)</div>
                <div class="big">{{ show(d, d.proposed.value) }}</div>
                <div class="small"><b>{{ d.proposed.source.fileName }}</b></div>
                <div class="small">accepted by {{ d.decision!.decidedBy }} · {{ time(d.decision!.decidedAt) }}</div>
              </div>
              <div class="value-card previous">
                <div class="label">PREVIOUS VALUE</div>
                <div class="big">{{ show(d, d.decision!.replacedValue ?? d.approved.value) }}</div>
                <div class="small">
                  <b>{{ (d.decision!.replacedSource ?? d.approved.source).fileName }}</b>
                </div>
                <div class="small">replaced by {{ d.decision!.decidedBy }}'s decision · kept in the vote history</div>
              </div>
            } @else {
              <div class="value-card approved">
                <div class="label">APPROVED VALUE</div>
                <div class="big">{{ show(d, d.approved.value) }}</div>
                <div class="small"><b>{{ d.approved.source.fileName }}</b></div>
                <div class="small">uploaded {{ time(d.approved.source.uploadedAt) }} by {{ d.approved.source.uploadedBy }}</div>
                @if (d.approved.source.publishedAt) {
                  <div class="small">published by the Assembly {{ time(d.approved.source.publishedAt) }}</div>
                }
              </div>
              <div class="value-card incoming">
                <div class="label">{{ d.decision ? 'REJECTED VALUE' : 'INCOMING VALUE' }}</div>
                <div class="big">{{ show(d, d.proposed.value) }}</div>
                <div class="small"><b>{{ d.proposed.source.fileName }}</b>@if (d.proposed.line) { · line {{ d.proposed.line }} }</div>
                <div class="small">uploaded {{ time(d.proposed.source.uploadedAt) }} by {{ d.proposed.source.uploadedBy }}</div>
                @if (d.proposed.source.publishedAt) {
                  <div class="small">published by the Assembly {{ time(d.proposed.source.publishedAt) }}</div>
                }
              </div>
            }
          </div>

          @if (d.status === 'Pending') {
            <h3>Impact</h3>
            <ul class="small" style="margin-top: 0">
              <li>The approved value changes only if you accept the incoming value. Rejecting keeps it.</li>
              <li>Snapshots that are already saved or published do not change. New snapshots use the value approved here.</li>
            </ul>

            <label for="note">Comment (optional)</label>
            <textarea id="note" [class.invalid]="noteError()" [(ngModel)]="note" maxlength="1000" placeholder="Why this decision"></textarea>
            @if (noteError(); as m) {
              <div class="field-error">{{ m }}</div>
            }

            <div class="row mt">
              @if (d.current.isStale) {
                <button class="btn btn-green" [disabled]="busy()" (click)="decide('Approve', d.current.value)">Approve anyway (current value is {{ show(d, d.current.value) }})</button>
              } @else {
                <button class="btn btn-green" [disabled]="busy()" (click)="decide('Approve')">Accept incoming value</button>
              }
              <button class="btn btn-danger" [disabled]="busy()" (click)="decide('Reject')">Reject, keep approved</button>
            </div>
            <p class="muted small">Every decision is recorded with who decided, when, and the comment.</p>
          } @else {
            @if (d.decision?.note) {
              <h3>{{ d.decision!.decidedBy }}'s comment</h3>
              <p>“{{ d.decision!.note }}”</p>
            }
            <div class="row mt">
              <button class="btn" disabled>Accept incoming value</button>
              <button class="btn" disabled>Reject, keep approved</button>
              <button class="btn btn-primary" (click)="openNext()" [disabled]="!nextPending()">Open next item</button>
            </div>
            <p class="muted small">Exactly one decision takes effect. Nothing is overwritten silently.</p>
          }
        } @else {
          <div class="muted">Select an item on the left.</div>
          <app-problem [error]="error()" />
        }
      </div>
    </div>
  `,
})
export class Review {
  protected readonly filter = signal('');
  protected readonly tab = signal<'Pending' | 'Resolved'>('Pending');
  protected readonly items = signal<ConflictListItem[]>([]);
  protected readonly pendingTotal = signal(0);
  protected readonly resolvedTotal = signal(0);
  protected readonly selectedId = signal<number | null>(null);
  protected readonly detail = signal<ConflictDetails | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly listError = signal<ApiError | null>(null);
  protected readonly notice = signal<{ title: string; detail: string } | null>(null);
  protected readonly busy = signal(false);
  protected note = '';

  protected readonly noteError = computed(() => this.error()?.fieldError('note') ?? null);
  protected readonly nextPending = computed(() => this.items().some((c) => c.status === 'Pending' && c.id !== this.selectedId()) || this.tab() === 'Resolved');

  protected readonly time = dateTime;
  protected readonly ago = ago;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    void this.reload(true);
  }

  protected show(d: ConflictDetails, value: string): string {
    return d.kind === 'CountryVote' ? ballot(value) : value;
  }

  protected summary(c: ConflictListItem): string {
    const v = (x: string) => (c.kind === 'CountryVote' ? ballot(x) : x);
    return c.kind === 'CountryVote' ? `${c.countryCode} country vote: ${v(c.approvedValue)} → ${v(c.proposedValue)}` : `Vote ${c.fieldName}: changed`;
  }

  protected onFilter(value: string) {
    this.filter.set(value);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.reload(false), 350);
  }

  protected setTab(tab: 'Pending' | 'Resolved') {
    this.tab.set(tab);
    void this.reload(false);
  }

  protected async reload(selectFirst: boolean) {
    this.listError.set(null);
    const vote = this.filter().trim() || undefined;
    try {
      const [pending, approved, rejected] = await Promise.all([
        api.conflicts.list({ status: 'Pending', assemblyVoteId: vote, take: 200 }),
        api.conflicts.list({ status: 'Approved', assemblyVoteId: vote, take: 200 }),
        api.conflicts.list({ status: 'Rejected', assemblyVoteId: vote, take: 200 }),
      ]);
      this.pendingTotal.set(pending.total);
      this.resolvedTotal.set(approved.total + rejected.total);
      const list =
        this.tab() === 'Pending'
          ? pending.items
          : [...approved.items, ...rejected.items].sort((a, b) => (b.decidedAt ?? '').localeCompare(a.decidedAt ?? ''));
      this.items.set(list);
      if (selectFirst && list.length > 0 && this.selectedId() === null) await this.select(list[0].id);
    } catch (e) {
      this.listError.set(asApiError(e));
    }
  }

  protected async select(id: number) {
    this.selectedId.set(id);
    this.error.set(null);
    this.notice.set(null);
    this.note = '';
    try {
      this.detail.set(await api.conflicts.get(id));
    } catch (e) {
      this.detail.set(null);
      this.error.set(asApiError(e));
    }
  }

  protected async decide(decision: 'Approve' | 'Reject', expectedApprovedValue?: string) {
    const id = this.selectedId();
    if (id === null) return;
    this.busy.set(true);
    this.error.set(null);
    this.notice.set(null);
    try {
      const updated = await api.conflicts.decide(id, { decision, note: this.note.trim() || undefined, expectedApprovedValue });
      this.detail.set(updated);
      await this.reload(false);
    } catch (e) {
      const err = asApiError(e);
      const p = err.problem;
      if (err.isConflict && p.conflict) {
        // CON-05: someone else decided first, or the approved value changed. Say so, and show the current state.
        this.detail.set(p.conflict);
        this.notice.set({
          title: p.type === PROBLEM.conflictAlreadyDecided ? 'This conflict was already decided' : (p.title ?? 'The conflict changed'),
          detail: p.detail ?? '',
        });
        await this.reload(false);
      } else {
        this.error.set(err);
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async openNext() {
    const next = this.items().find((c) => c.status === 'Pending' && c.id !== this.selectedId());
    if (next) return this.select(next.id);
    this.tab.set('Pending');
    await this.reload(false);
    const first = this.items()[0];
    if (first) await this.select(first.id);
  }
}
