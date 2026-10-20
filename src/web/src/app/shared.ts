import { DestroyRef, inject } from '@angular/core';
import { ApiError } from './api/client';

/**
 * Calls `tick` now and then again every `intervalMs` while `again()` says so. Stops when the component is destroyed.
 * Used for the live progress of imports and snapshots (ING-02, SNP-02): the state is read from the server each time,
 * so a page refresh shows the same progress.
 */
export function createPoller(tick: () => Promise<void>, again: () => boolean, intervalMs = 1000) {
  const destroyed = { value: false };
  inject(DestroyRef).onDestroy(() => (destroyed.value = true));
  let timer: ReturnType<typeof setTimeout> | undefined;

  const run = async () => {
    clearTimeout(timer);
    try {
      await tick();
    } catch {
      /* the screen shows errors of its own actions; a failed poll is retried */
    }
    if (!destroyed.value && again()) timer = setTimeout(run, intervalMs);
  };
  return { start: run, stop: () => clearTimeout(timer) };
}

export function asApiError(e: unknown): ApiError {
  return e instanceof ApiError ? e : new ApiError(0, { title: 'Unexpected error', detail: e instanceof Error ? e.message : String(e) });
}
