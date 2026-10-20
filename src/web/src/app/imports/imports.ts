import { Component, computed, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiError, ImportSummary, api } from '../api/client';
import { dateTime, importActive, importChip, importRows, percent } from '../format';
import { ProblemBanner } from '../problem-banner';
import { asApiError, createPoller } from '../shared';

/** Screen 1: upload, live progress, history of every import (ING-01, ING-02, ING-15, HIS-01). */
@Component({
  selector: 'app-imports',
  imports: [RouterLink, ProblemBanner],
  template: `
    <h1>Imports</h1>
    <div class="page-sub">Upload Assembly CSV exports. Processing runs in the background.</div>

    <app-problem [error]="error()" />

    <div class="grid-2-even">
      <div
        class="dropzone"
        [class.over]="over()"
        (click)="picker.click()"
        (dragover)="$event.preventDefault(); over.set(true)"
        (dragleave)="over.set(false)"
        (drop)="$event.preventDefault(); over.set(false); pick($event.dataTransfer?.files)"
        role="button"
        tabindex="0"
        (keydown.enter)="picker.click()"
      >
        <strong>{{ uploading() ? 'Uploading…' : 'Drop a CSV export here' }}</strong>
        <div class="muted small">or click to browse · .csv</div>
        <input #picker type="file" accept=".csv,text/csv" hidden (change)="pick(picker.files); picker.value = ''" />
      </div>

      <div class="card" style="margin: 0">
        <div class="muted small">Currently processing</div>
        @if (running(); as r) {
          <div style="font-weight: 600; margin: 4px 0 8px">{{ r.fileName }}</div>
          <div class="bar"><i [style.width.%]="r.total ? pct(r) : 4"></i></div>
          <div class="row small muted" style="justify-content: space-between; margin-top: 4px">
            <span>
              @if (r.total) {
                {{ r.done.toLocaleString('en-US') }} of {{ r.total.toLocaleString('en-US') }} rows · {{ pct(r) }}%
              } @else {
                {{ r.phase ?? r.status }}… {{ r.done.toLocaleString('en-US') }} rows so far
              }
              @if (r.phase && r.total) {
                · {{ r.phase }}
              }
            </span>
            <span>started {{ time(r.uploadedAt) }} by {{ r.uploadedBy }}</span>
          </div>
          @if (running_more() > 0) {
            <div class="muted small">+ {{ running_more() }} more waiting</div>
          }
        } @else {
          <div class="muted" style="margin-top: 8px">Nothing is being processed.</div>
        }
      </div>
    </div>

    <div class="card mt">
      <h2>Import history</h2>
      <table>
        <thead>
          <tr>
            <th>File</th>
            <th>Uploaded by</th>
            <th>Uploaded at</th>
            <th class="right">Rows</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (i of imports(); track i.id) {
            <tr>
              <td>{{ i.label }}</td>
              <td>{{ i.uploadedBy }}</td>
              <td>{{ time(i.uploadedAt) }}</td>
              <td class="right">{{ rows(i) }}</td>
              <td>
                <span class="chip chip-{{ chip(i).tone }}" [title]="i.error ?? ''">{{ chip(i).text }}</span>
                @if (importActive(i) && i.total) {
                  <div class="bar narrow"><i [style.width.%]="pct(i)"></i></div>
                }
              </td>
              <td class="right"><a [routerLink]="['/imports', i.id]">Report</a></td>
            </tr>
          } @empty {
            <tr>
              <td colspan="6" class="muted">No imports yet.</td>
            </tr>
          }
        </tbody>
      </table>
      <p class="muted small">
        Every upload is kept: the file, who uploaded it, when, its report and the outcome of every row. Progress survives a page refresh. Reports can be opened at any time.
      </p>
    </div>
  `,
})
export class Imports {
  protected readonly imports = signal<ImportSummary[]>([]);
  protected readonly error = signal<ApiError | null>(null);
  protected readonly uploading = signal(false);
  protected readonly over = signal(false);
  private readonly active = computed(() => this.imports().filter(importActive));
  protected readonly running = computed(() => this.active().find((i) => i.status === 'Running') ?? this.active()[0] ?? null);
  protected readonly running_more = computed(() => Math.max(0, this.active().length - 1));

  protected readonly importActive = importActive;
  protected readonly chip = importChip;
  protected readonly rows = importRows;
  protected readonly time = dateTime;
  protected pct = (i: ImportSummary) => percent(i.done, i.total);

  // Progress is read from the server every second while something runs, so a refresh shows the same state (ING-02).
  private readonly poller = createPoller(
    async () => this.imports.set(await api.imports.list(50)),
    () => this.active().length > 0,
  );

  constructor() {
    void this.poller.start().catch(() => undefined);
  }

  protected async pick(files: FileList | null | undefined) {
    const file = files?.[0];
    if (!file) return;
    this.error.set(null);
    this.uploading.set(true);
    try {
      await api.imports.upload(file); // 202: the file is processed in the background
      await this.poller.start();
    } catch (e) {
      this.error.set(asApiError(e));
    } finally {
      this.uploading.set(false);
    }
  }
}
