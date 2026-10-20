import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { ApiError, CountryBreakdown, CountryRanking, PROBLEM, SnapshotDetails, api, isAdmin } from '../api/client';
import { ballot, dateOnly, dateTime, percent, snapshotTone, stance } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError, createPoller } from '../shared';

/** Screen 9: ranking, the score of every country, and how each score was calculated (SNP-03, SNP-04). */
@Component({
  selector: 'app-snapshot-detail',
  imports: [FormsModule, RouterLink, ProblemBanner],
  template: `
    <p><a routerLink="/snapshots">← Snapshots</a></p>
    <app-problem [error]="error()" />

    @if (details(); as d) {
      <div class="row" style="justify-content: space-between">
        <div>
          <h1>Snapshot · {{ d.snapshot.name }}</h1>
          <div class="page-sub">
            Created by {{ d.snapshot.createdBy }} · {{ time(d.snapshot.createdAt) }}
            @if (d.snapshot.publishedAt) {
              · published by {{ d.snapshot.publishedBy }} {{ time(d.snapshot.publishedAt) }}
            }
            · {{ d.includedVotes }} votes · immutable
            @if (d.snapshot.quarter) {
              · {{ d.snapshot.quarter }}
            }
          </div>
        </div>
        <div class="row">
          <span class="chip chip-{{ tone(d.snapshot.status) }}">{{ d.snapshot.status.toUpperCase() }}</span>
          @if (admin && d.snapshot.status === 'Saved') {
            <button class="btn btn-green" [disabled]="busy()" (click)="publish(false)">Mark as published</button>
          }
        </div>
      </div>

      @if (publishError(); as p) {
        <div class="alert alert-amber">
          <strong>{{ p.problem.title }}</strong>
          <div>{{ p.problem.detail }}</div>
          @if (p.problem.type === quarterTaken) {
            <button class="btn btn-small mt" [disabled]="busy()" (click)="publish(true)">Publish this one instead</button>
          }
        </div>
      }

      @if (d.snapshot.status === 'Calculating') {
        <div class="card">
          <strong>Calculating… {{ d.snapshot.phase ?? d.snapshot.jobStatus }}</strong>
          <div class="bar mt"><i [style.width.%]="d.snapshot.total ? pct() : 4"></i></div>
          <div class="muted small">You can leave this page. The calculation continues in the background.</div>
        </div>
      } @else if (d.snapshot.status === 'Failed') {
        <div class="alert alert-red"><strong>The calculation failed.</strong><div>{{ d.snapshot.error }}</div></div>
      } @else {
        <div class="grid-2-even">
          <div class="card" style="margin: 0">
            <h2>Ranking · {{ d.rankedCountries }} ranked countries</h2>
            <input type="search" [ngModel]="filter()" (ngModelChange)="filter.set($event)" placeholder="Find a country" aria-label="Find a country" />
            <div class="table-scroll mt">
              <table>
                <thead><tr><th>#</th><th>Member state</th><th>Score</th><th></th></tr></thead>
                <tbody>
                  @for (c of rows(); track c.countryCode) {
                    <tr class="clickable" [class.selected]="c.countryCode === country()?.countryCode" (click)="select(c)">
                      <td>{{ c.rank ?? '—' }}</td>
                      <td>{{ c.countryName }} <span class="muted small">{{ c.countryCode }}</span></td>
                      <td>
                        @if (c.score !== null) {
                          <span class="score-bar" [style.width.px]="c.score * 0.9"></span>
                        }
                      </td>
                      <td class="right">{{ c.scoreText }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <p class="muted small">n/a = no countable votes in this snapshot.</p>
          </div>

          <div class="card" style="margin: 0">
            @if (country(); as c) {
              <h2>{{ c.countryName }} · score {{ c.scoreText }} · rank {{ c.rankText }}</h2>
              <div class="muted small">Score = 100 × matching weights ÷ counted weights = 100 × {{ c.matchedWeight }} ÷ {{ c.countedWeight }}</div>
              <app-problem [error]="breakdownError()" />
              <div class="table-scroll mt">
                <table>
                  <thead>
                    <tr><th>Vote</th><th>Position</th><th class="right">Weight</th><th>Ballot</th><th class="right">Counted</th><th class="right">Matching</th></tr>
                  </thead>
                  <tbody>
                    @for (i of items(); track i.ordinal) {
                      <tr>
                        <td [title]="i.title">{{ i.assemblyVoteId }}</td>
                        <td>{{ stance(i.desiredOutcome) }}</td>
                        <td class="right">{{ i.weight }}</td>
                        <td>
                          {{ i.ballot ? ballot(i.ballot) : '—' }}
                          @if (!i.counted) {
                            <div class="muted small">{{ outcome(i.outcome) }}</div>
                          }
                        </td>
                        <td class="right">{{ i.counted ? i.weight : '—' }}</td>
                        <td class="right">{{ i.matched ? i.weight : i.counted ? 0 : '—' }}</td>
                      </tr>
                    }
                    <tr>
                      <td><strong>Total</strong></td><td></td><td></td><td></td>
                      <td class="right"><strong>{{ c.countedWeight }}</strong></td>
                      <td class="right"><strong>{{ c.matchedWeight }}</strong></td>
                    </tr>
                  </tbody>
                </table>
              </div>
              @if (nextOrdinal() !== null) {
                <p><button class="btn btn-small" (click)="moreItems()">Load more votes</button></p>
              }
            } @else {
              <div class="muted">Select a country to see how its score was calculated.</div>
            }

            <div class="alert alert-purple mt">
              <strong>Scoring rules applied</strong>
              <div class="muted small">version {{ d.rules.version }} · these are the rules this snapshot was calculated with</div>
              <ul class="small" style="margin: 6px 0 0">
                @for (r of d.rules.rules; track r) {
                  <li>{{ r }}</li>
                }
              </ul>
            </div>

            <p class="small">
              This snapshot is frozen. Later data corrections, conflict decisions and weight changes do not change it; opening it next year shows exactly the same numbers.
            </p>
            <a class="btn btn-primary" [routerLink]="['/snapshots']" [queryParams]="{ from: d.snapshot.id }" style="display: inline-block">Create new snapshot with current data</a>
          </div>
        </div>

        @if (d.excludedVoteCount > 0) {
          <div class="card mt">
            <h2>Excluded votes ({{ d.excludedVoteCount }})</h2>
            <table>
              <thead><tr><th>Vote</th><th>Date</th><th>Title</th><th>Reason</th></tr></thead>
              <tbody>
                @for (v of d.excludedVotes; track v.assemblyVoteId) {
                  <tr><td>{{ v.assemblyVoteId }}</td><td>{{ date(v.voteDate) }}</td><td>{{ v.title }}</td><td>{{ v.reason }}</td></tr>
                }
              </tbody>
            </table>
            @if (d.excludedVoteCount > d.excludedVotes.length) {
              <p class="muted small">Showing the first {{ d.excludedVotes.length }}.</p>
            }
          </div>
        }
      }
    }
  `,
})
export class SnapshotDetail {
  private readonly id = Number(inject(ActivatedRoute).snapshot.paramMap.get('id'));
  protected readonly admin = isAdmin();
  protected readonly quarterTaken = PROBLEM.quarterPublished;
  protected readonly details = signal<SnapshotDetails | null>(null);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly publishError = signal<ApiError | null>(null);
  protected readonly breakdownError = signal<ApiError | null>(null);
  protected readonly busy = signal(false);
  protected readonly filter = signal('');
  protected readonly country = signal<CountryRanking | null>(null);
  protected readonly items = signal<CountryBreakdown['items']>([]);
  protected readonly nextOrdinal = signal<number | null>(null);

  protected readonly rows = computed(() => {
    const f = this.filter().trim().toLowerCase();
    const all = this.details()?.ranking ?? [];
    return f ? all.filter((c) => c.countryName.toLowerCase().includes(f) || c.countryCode.toLowerCase().includes(f)) : all;
  });

  protected readonly time = dateTime;
  protected readonly date = dateOnly;
  protected readonly tone = snapshotTone;
  protected readonly stance = stance;
  protected readonly ballot = ballot;
  protected pct = () => percent(this.details()!.snapshot.done, this.details()!.snapshot.total);

  private readonly poller = createPoller(
    async () => this.details.set(await api.snapshots.get(this.id)),
    () => !this.details() || this.details()!.snapshot.status === 'Calculating',
  );

  constructor() {
    this.poller.start().catch((e) => this.error.set(asApiError(e)));
  }

  protected outcome(o: string): string {
    return (
      ({ Abstained: 'abstained: not counted', Absent: 'absent: not counted', NotAMember: 'not a member then: not counted', NoBallot: 'no ballot: not counted' } as Record<string, string>)[o] ?? o
    );
  }

  protected async select(c: CountryRanking) {
    this.country.set(c);
    this.items.set([]);
    this.nextOrdinal.set(null);
    this.breakdownError.set(null);
    await this.moreItems(true);
  }

  protected async moreItems(first = false) {
    const c = this.country();
    if (!c) return;
    try {
      const page = await api.snapshots.country(this.id, c.countryCode, first ? -1 : (this.nextOrdinal() ?? -1), 100);
      this.items.update((i) => [...i, ...page.items]);
      this.nextOrdinal.set(page.nextAfterOrdinal);
    } catch (e) {
      this.breakdownError.set(asApiError(e));
    }
  }

  protected async publish(replace: boolean) {
    this.busy.set(true);
    this.publishError.set(null);
    try {
      this.details.set(await api.snapshots.publish(this.id, replace));
    } catch (e) {
      this.publishError.set(asApiError(e));
    } finally {
      this.busy.set(false);
    }
  }
}
