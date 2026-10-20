import { Component, input } from '@angular/core';
import type { ApiError } from './api/client';

/** An error from the API shown as a message (title, detail, correlation id for the logs), never as a bare status code. */
@Component({
  selector: 'app-problem',
  template: `
    @if (error(); as e) {
      <!-- A 400 whose messages all belong to fields on the screen is shown next to those fields only. -->
      @if (!e.problem.errors || e.unmatched(known()).length > 0) {
      <div class="alert" [class.alert-red]="!e.isConflict" [class.alert-amber]="e.isConflict" role="alert">
        <strong>{{ e.problem.title }}</strong>
        @if (e.problem.detail) {
          <div>{{ e.problem.detail }}</div>
        }
        @for (m of e.unmatched(known()); track m) {
          <div>{{ m }}</div>
        }
        @if (e.problem.correlationId) {
          <div class="muted small">Reference {{ e.problem.correlationId }}</div>
        }
      </div>
      }
    }
  `,
})
export class ProblemBanner {
  readonly error = input<ApiError | null>(null);
  /** Fields the screen shows messages for; their errors are not repeated in the banner. */
  readonly known = input<string[]>([]);
}
