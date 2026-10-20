import type { ImportSummary } from './api/client';

/** 2024-04-22 14:02 in the browser's time zone. */
export function dateTime(value: string | null | undefined): string {
  if (!value) return '—';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** 2024-04-22 (a date as the API sends it, without a time zone). */
export function dateOnly(value: string | null | undefined): string {
  return value ? value.slice(0, 10) : '—';
}

export function ago(value: string | null | undefined): string {
  if (!value) return '';
  const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds} seconds ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} minute${minutes === 1 ? '' : 's'} ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} hour${hours === 1 ? '' : 's'} ago`;
  return `${Math.round(hours / 24)} days ago`;
}

const BALLOTS: Record<string, string> = { Y: 'YES', N: 'NO', A: 'ABSTAIN', X: 'ABSENT' };

/** Y, N, A, X as words; any other value (a title, a date) unchanged. */
export function ballot(value: string | null | undefined): string {
  if (!value) return '—';
  return BALLOTS[value] ?? value;
}

export function stance(value: string | null | undefined): string {
  return value === 'For' ? 'YES' : value === 'Against' ? 'NO' : (value ?? '—');
}

export function percent(done: number, total: number | null | undefined): number {
  return total ? Math.min(100, Math.round((done / total) * 100)) : 0;
}

export function importRows(i: ImportSummary): string {
  if (i.total != null) return i.total.toLocaleString('en-US');
  const sum = Object.values(i.counts).reduce((a, b) => a + b, 0) - i.counts.normalized;
  return sum > 0 ? sum.toLocaleString('en-US') : '—';
}

export interface Chip {
  text: string;
  tone: 'blue' | 'green' | 'amber' | 'red' | 'grey' | 'purple';
}

export function importChip(i: ImportSummary): Chip {
  switch (i.status) {
    case 'Queued':
      return { text: 'Queued', tone: 'grey' };
    case 'Running':
      return { text: 'Processing', tone: 'blue' };
    case 'Failed':
      return { text: 'Failed', tone: 'red' };
    default:
      return i.counts.invalid + i.counts.contradiction + i.counts.conflict > 0 ? { text: 'Completed with issues', tone: 'amber' } : { text: 'Completed', tone: 'green' };
  }
}

export const importActive = (i: { status: string }) => i.status === 'Queued' || i.status === 'Running';

export function classificationTone(c: string): Chip['tone'] {
  switch (c) {
    case 'Invalid':
      return 'red';
    case 'Contradiction':
    case 'Conflict':
      return 'amber';
    case 'New':
      return 'green';
    default:
      return 'grey';
  }
}

export function snapshotTone(status: string): Chip['tone'] {
  switch (status) {
    case 'Published':
      return 'green';
    case 'Saved':
      return 'blue';
    case 'Failed':
      return 'red';
    default:
      return 'amber';
  }
}

/** Splits "MA/2024/101, MA/2024/102\nMA/2024/103" into ids. */
export function parseIds(text: string): string[] {
  return [...new Set(text.split(/[\s,;]+/).map((s) => s.trim()).filter(Boolean))];
}
