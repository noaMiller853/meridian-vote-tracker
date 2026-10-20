import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, PROBLEM, TimelineEntry, VoteView, api, currentUserName, isAdmin } from '../api/client';
import { ballot, dateOnly, dateTime } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError } from '../shared';

/** Screen 7: one vote: position and weight (POS-01..03), country votes, and its full history (HIS-04). */
@Component({
  selector: 'app-vote-detail',
  imports: [FormsModule, RouterLink, ProblemBanner],
  template: `
    <p><a routerLink="/votes">← Votes</a></p>
    <app-problem [error]="loadError()" />

    @if (vote(); as v) {
      <div class="row" style="justify-content: space-between">
        <div>
          <h1>{{ v.assemblyVoteId }} · {{ v.title }}</h1>
          <div class="page-sub">Session {{ v.session }} · {{ date(v.voteDate) }} · {{ v.countryVotes.length }} country votes · {{ counts() }}</div>
        </div>
        @if (v.pendingConflicts > 0) {
          <a class="chip chip-amber" routerLink="/review">{{ v.pendingConflicts }} pending conflict{{ v.pendingConflicts === 1 ? '' : 's' }}</a>
        }
      </div>

      <div class="grid-2-even">
        <div>
          <div class="card">
            <h2>Northwind position</h2>
            <label>Desired outcome</label>
            <div class="toggle">
              <button type="button" [class.on]="stance() === 'For'" [disabled]="!admin" (click)="stance.set('For')">YES</button>
              <button type="button" [class.on]="stance() === 'Against'" [disabled]="!admin" (click)="stance.set('Against')">NO</button>
            </div>
            @if (fieldError('stance'); as m) {
              <div class="field-error">{{ m }}</div>
            }

            <label for="weight">Weight (1–5)</label>
            <input id="weight" type="number" [(ngModel)]="weight" [disabled]="!admin" [class.invalid]="fieldError('weight')" style="max-width: 120px" />
            @if (fieldError('weight'); as m) {
              <div class="field-error">{{ m }}</div>
            }
            @if (fieldError('expectedVersion'); as m) {
              <div class="field-error">{{ m }}</div>
            }

            @if (conflictNotice(); as n) {
              <div class="alert alert-amber mt">
                <strong>{{ n.title }}</strong>
                <div>{{ n.detail }}</div>
                <button class="btn btn-small mt" (click)="useCurrent()">Show the current position</button>
              </div>
            }
            <app-problem [error]="error()" [known]="['stance', 'weight', 'expectedVersion']" />
            @if (saved(); as s) {
              <div class="alert alert-green mt">{{ s }}</div>
            }

            <div class="row mt">
              <button class="btn btn-primary" [disabled]="!admin || busy()" (click)="save()">Save position</button>
              <span class="muted small">
                @if (admin) {
                  @if (v.position; as p) {
                    Set by {{ p.setBy }} · {{ time(p.setAt) }} · version {{ p.version }}
                  } @else {
                    This vote has no position yet.
                  }
                } @else {
                  Only admins can change positions ({{ user }} is an analyst).
                }
              </span>
            </div>
          </div>

          <div class="card">
            <h2>Country votes</h2>
            <div class="table-scroll">
              <table>
                <thead><tr><th>Code</th><th>Member state</th><th>Choice</th></tr></thead>
                <tbody>
                  @for (c of v.countryVotes; track c.countryCode) {
                    <tr [class.mark]="c.changedByDecision">
                      <td>{{ c.countryCode }}</td>
                      <td>{{ c.countryName }}</td>
                      <td [title]="c.source.fileName + ' · line ' + c.sourceLine + ' · approved by ' + c.approvedBy">
                        {{ ballot(c.ballot) }}
                        @if (c.changedByDecision) {
                          <span title="Changed by a conflict decision"> ●</span>
                        }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <p class="muted small">● changed by a conflict decision. Hover a choice for its source file and who approved it.</p>
          </div>
        </div>

        <div class="card">
          <h2>History of this vote</h2>
          <ul class="timeline">
            @for (e of timeline(); track $index) {
              <li class="t-{{ tone(e.action) }}">
                <div class="row" style="justify-content: space-between; align-items: baseline">
                  <strong>{{ title(e.action) }}</strong>
                  <span class="muted small">{{ time(e.at) }} · {{ e.actor }}</span>
                </div>
                <div class="small">{{ e.summary }}</div>
                @if (e.import) {
                  <div class="muted small">source: <a [routerLink]="['/imports', e.import.importId]">{{ e.import.fileName }}</a></div>
                }
              </li>
            } @empty {
              <li class="muted">No history.</li>
            }
          </ul>
        </div>
      </div>
    }
  `,
})
export class VoteDetail {
  private readonly id = Number(inject(ActivatedRoute).snapshot.paramMap.get('id'));
  protected readonly admin = isAdmin();
  protected readonly user = currentUserName();
  protected readonly vote = signal<VoteView | null>(null);
  protected readonly timeline = signal<TimelineEntry[]>([]);
  protected readonly loadError = signal<ApiError | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly conflictNotice = signal<{ title: string; detail: string } | null>(null);
  protected readonly saved = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly stance = signal<'For' | 'Against'>('For');
  protected weight: number | null = null;
  private version = 0;
  private current: VoteView['position'] = null;

  protected readonly counts = computed(() => {
    const v = this.vote();
    if (!v) return '';
    const n = (b: string) => v.countryVotes.filter((c) => c.ballot === b).length;
    return `YES ${n('Y')} · NO ${n('N')} · ABSTAIN ${n('A')} · ABSENT ${n('X')}`;
  });

  protected readonly time = dateTime;
  protected readonly date = dateOnly;
  protected readonly ballot = ballot;
  protected readonly fieldError = (f: string) => this.error()?.fieldError(f) ?? null;

  constructor() {
    void this.load();
  }

  private async load() {
    try {
      const [vote, timeline] = await Promise.all([api.votes.get(this.id), api.votes.timeline(this.id)]);
      this.vote.set(vote);
      // The newest event first, as in the mockup.
      this.timeline.set([...timeline.entries].reverse());
      this.applyPosition(vote.position);
    } catch (e) {
      this.loadError.set(asApiError(e));
    }
  }

  private applyPosition(p: VoteView['position']) {
    this.current = p;
    this.version = p?.version ?? 0; // POS: the first position of a vote is sent with expectedVersion 0
    this.stance.set((p?.stance as 'For' | 'Against' | undefined) ?? 'For');
    this.weight = p?.weight ?? null;
  }

  protected async save() {
    this.busy.set(true);
    this.error.set(null);
    this.conflictNotice.set(null);
    this.saved.set(null);
    try {
      const result = await api.votes.setPosition(this.id, { stance: this.stance(), weight: this.weight, expectedVersion: this.version });
      this.applyPosition(result.position);
      this.saved.set(result.changed ? 'Position saved.' : 'No change: this position is already set.');
      await this.load();
    } catch (e) {
      const err = asApiError(e);
      if (err.isConflict && err.problem.type === PROBLEM.positionChanged) {
        // POS-01/03: someone else changed it first. Nothing was overwritten; show what is stored now.
        this.current = err.problem.position ?? null;
        this.conflictNotice.set({ title: 'The position was changed by someone else', detail: err.problem.detail ?? '' });
      } else {
        this.error.set(err);
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected useCurrent() {
    this.applyPosition(this.current);
    this.conflictNotice.set(null);
  }

  protected title(action: string): string {
    return (
      {
        Imported: 'File uploaded',
        VoteCreated: 'Vote created',
        BallotsAdded: 'Country votes added',
        ConflictOpened: 'Conflict created',
        ConflictApproved: 'Conflict accepted',
        ConflictRejected: 'Conflict rejected',
        BallotChanged: 'Country vote changed',
        VoteFieldChanged: 'Vote detail changed',
        DecisionAttemptRejected: 'Decision not applied',
        PositionSet: 'Position set',
        PositionChanged: 'Position changed',
      } as Record<string, string>
    )[action] ?? action;
  }

  protected tone(action: string): string {
    if (action.startsWith('Position')) return 'blue';
    if (action === 'ConflictOpened') return 'amber';
    if (action === 'ConflictApproved' || action === 'VoteCreated' || action === 'BallotsAdded') return 'green';
    if (action === 'BallotChanged' || action === 'VoteFieldChanged') return 'purple';
    if (action === 'DecisionAttemptRejected' || action === 'ConflictRejected') return 'red';
    return 'grey';
  }
}
