import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { Snapshots } from './snapshots';

type VoteItem = Awaited<ReturnType<typeof api.votes.list>>['items'][number];

/** A selection of `total` votes, `withoutPosition` of them without a position (the real case: 41 votes, 39 without). */
function mockSelection(total: number, withoutPosition: number) {
  const vote = (i: number, hasPosition: boolean) =>
    ({ id: i, assemblyVoteId: `MA/2024/${String(i).padStart(3, '0')}`, title: `Vote ${i}`, hasPosition }) as unknown as VoteItem;
  const without = Array.from({ length: withoutPosition }, (_, i) => vote(i + 1, false));
  const withPos = Array.from({ length: total - withoutPosition }, (_, i) => vote(withoutPosition + i + 1, true));
  vi.spyOn(api.votes, 'list').mockImplementation(async (o: { hasPosition?: boolean } = {}) => {
    const items = o.hasPosition === false ? without : o.hasPosition === true ? withPos : [...without, ...withPos].slice(0, 1);
    const count = o.hasPosition === false ? without.length : o.hasPosition === true ? withPos.length : total;
    return { items, total: count, nextAfterId: null } as Awaited<ReturnType<typeof api.votes.list>>;
  });
}

describe('Snapshots: the Create button follows the form (signals, not plain fields)', () => {
  beforeEach(() => {
    vi.spyOn(api.snapshots, 'list').mockResolvedValue([]);
    TestBed.configureTestingModule({ imports: [Snapshots], providers: [provideRouter([])] });
  });

  afterEach(() => vi.restoreAllMocks());

  async function render() {
    const fixture = TestBed.createComponent(Snapshots);
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    const settle = async () => {
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const type = async (selector: string, value: string) => {
      const input = el.querySelector<HTMLInputElement>(selector)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      await settle();
    };
    // The selection is counted 350 ms after the last change (debounced), then the votes are fetched.
    const select = async (from: string) => {
      await type('#from', from);
      await new Promise((r) => setTimeout(r, 400));
      await settle();
    };
    const createButton = () => [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Create snapshot'))!;
    const confirmExclusion = async () => {
      el.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
      await settle();
    };
    return { el, type, select, createButton, confirmExclusion };
  }

  it('enables the button when the exclusion of votes without a position is confirmed', async () => {
    mockSelection(41, 39);
    const page = await render();

    await page.type('#name', '2024 Q1');
    await page.select('2024-01-01');

    expect(page.el.textContent).toContain('39 selected votes have no position');
    expect(page.createButton().disabled).toBe(true);

    await page.confirmExclusion();

    expect(page.createButton().disabled).toBe(false);
  });

  it('enables the button when the name is typed after the selection was computed', async () => {
    mockSelection(41, 39);
    const page = await render();

    await page.select('2024-01-01');
    await page.confirmExclusion();
    expect(page.createButton().disabled).toBe(true); // no name yet

    await page.type('#name', '2024 Q1');

    expect(page.createButton().disabled).toBe(false);
  });

  it('enables the button for a name and a selection where every vote has a position', async () => {
    mockSelection(5, 0);
    const page = await render();

    await page.select('2024-01-01');
    expect(page.createButton().disabled).toBe(true);

    await page.type('#name', 'All positioned');

    expect(page.createButton().disabled).toBe(false);
  });
});
