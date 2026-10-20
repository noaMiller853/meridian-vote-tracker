import createClient from 'openapi-fetch';
import type { components, paths } from './schema';

/**
 * The typed API client. Everything here is derived from the OpenAPI contract:
 *  - `schema.d.ts` is generated from `openapi.json` by `npm run gen:api` (never edited by hand);
 *  - `openapi-fetch` turns those types into a client whose paths, parameters, bodies and responses are checked by the
 *    compiler, so a change in the back end that breaks the front end fails the build.
 */

// ---------------------------------------------------------------- types from the contract

/** Response types as the screens use them: every property present (the server always sends them, null when empty). */
export type Strict<T> = T extends readonly (infer U)[] ? Strict<U>[] : T extends object ? { [K in keyof T]-?: Strict<T[K]> } : T;

type Schemas = components['schemas'];
export type ImportSummary = Strict<Schemas['ImportSummary']>;
export type ImportDetails = Strict<Schemas['ImportDetails']>;
export type ImportRowDto = Strict<Schemas['ImportRowDto']>;
export type ImportVoteDto = Strict<Schemas['ImportVoteDto']>;
export type ConflictDetails = Strict<Schemas['ConflictDetails']>;
export type ConflictListItem = Strict<Schemas['ConflictListItem']>;
export type PositionDto = Strict<Schemas['PositionDto']>;
export type VoteSummary = Strict<Schemas['VoteSummary']>;
export type VoteView = Strict<Schemas['VoteView']>;
export type TimelineEntry = Strict<Schemas['TimelineEntry']>;
export type SnapshotSummary = Strict<Schemas['SnapshotSummary']>;
export type SnapshotDetails = Strict<Schemas['SnapshotDetails']>;
export type CountryRanking = Strict<Schemas['CountryRanking']>;
export type CountryBreakdown = Strict<Schemas['CountryBreakdown']>;
export type VoteContribution = Strict<Schemas['VoteContribution']>;

// ---------------------------------------------------------------- users (spec section 3: simplified identification)

export const USER_KEY = 'meridian.user';
export const USERS = [
  { name: 'Dana', role: 'Analyst' },
  { name: 'Omar', role: 'Analyst' },
  { name: 'Lena', role: 'Admin' },
] as const;

export function currentUserName(): string {
  try {
    const stored = localStorage.getItem(USER_KEY);
    if (stored && USERS.some((u) => u.name === stored)) return stored;
  } catch {
    /* storage can be unavailable */
  }
  return USERS[0].name;
}

export function currentRole(): string {
  return USERS.find((u) => u.name === currentUserName())?.role ?? 'Analyst';
}

export const isAdmin = () => currentRole() === 'Admin';

// ---------------------------------------------------------------- the client

function correlationId(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

export const client = createClient<paths>({ baseUrl: '' });

// Every request names the acting user and carries a correlation id that follows it into the API logs (section 9).
client.use({
  onRequest({ request }) {
    request.headers.set('X-User', currentUserName());
    request.headers.set('X-Correlation-Id', correlationId());
    return request;
  },
});

// ---------------------------------------------------------------- errors (one ProblemDetails format, section 9)

/** The ProblemDetails of the API, with the extensions the endpoints add to their 409 answers. */
export interface Problem {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  correlationId?: string | null;
  errors?: Record<string, string[]>;
  /** 409 on a conflict decision: the conflict as it is now. */
  conflict?: ConflictDetails;
  /** 409 on a position: the position as it is now. */
  position?: PositionDto | null;
  /** 409 on a snapshot without positions. */
  votesWithoutPosition?: { count: number; assemblyVoteIds: string[] };
  /** 409 on publish: the snapshot that already holds the quarter. */
  published?: SnapshotSummary | null;
}

export const PROBLEM = {
  conflictAlreadyDecided: 'urn:meridian:problem:conflict-already-decided',
  conflictStale: 'urn:meridian:problem:conflict-stale',
  positionChanged: 'urn:meridian:problem:position-changed',
  votesWithoutPosition: 'urn:meridian:problem:snapshot-votes-without-position',
  quarterPublished: 'urn:meridian:problem:snapshot-quarter-published',
} as const;

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Problem,
  ) {
    super(problem.detail || problem.title || `HTTP ${status}`);
  }

  get isConflict() {
    return this.status === 409;
  }

  /** Validation messages by field name, matched without regard to case ("weight", "expectedVersion"). */
  fieldError(field: string): string | null {
    const errors = this.problem.errors;
    if (!errors) return null;
    const key = Object.keys(errors).find((k) => k.toLowerCase() === field.toLowerCase());
    return key ? errors[key].join(' ') : null;
  }

  /** Validation messages that belong to no field the screen shows. */
  unmatched(fields: string[]): string[] {
    const known = fields.map((f) => f.toLowerCase());
    return Object.entries(this.problem.errors ?? {})
      .filter(([k]) => !known.includes(k.toLowerCase()))
      .flatMap(([k, v]) => v.map((m) => (k ? `${k}: ${m}` : m)));
  }
}

type Result<T> = { data?: T; error?: unknown; response: Response };

/** Unwraps a response: the data, or an ApiError that carries the ProblemDetails. */
export async function call<T>(request: Promise<Result<T>>): Promise<Strict<T>> {
  let result: Result<T>;
  try {
    result = await request;
  } catch {
    throw new ApiError(0, { title: 'The API cannot be reached', detail: 'The request did not reach the server. Check that the API is running and try again.' });
  }
  if (result.response.ok) return result.data as Strict<T>;
  const problem = (result.error && typeof result.error === 'object' ? result.error : {}) as Problem;
  throw new ApiError(result.response.status, {
    ...problem,
    title: problem.title ?? result.response.statusText,
  });
}

// ---------------------------------------------------------------- endpoints

export const api = {
  imports: {
    list: (take = 50) => call(client.GET('/api/imports', { params: { query: { take } } })),
    get: (id: number) => call(client.GET('/api/imports/{id}', { params: { path: { id } } })),
    upload: (file: File) => {
      const form = new FormData();
      form.append('file', file, file.name);
      return call(client.POST('/api/imports', { body: { file: '' }, bodySerializer: () => form }));
    },
    rows: (importId: number, o: { classes?: string; vote?: string; after?: number; take?: number } = {}) =>
      call(
        client.GET('/api/imports/{importId}/rows', {
          params: { path: { importId }, query: { class: o.classes, vote: o.vote, after: o.after, take: o.take ?? 100 } },
        }),
      ),
    votes: (importId: number, o: { status?: string; after?: string; take?: number } = {}) =>
      call(client.GET('/api/imports/{importId}/votes', { params: { path: { importId }, query: { status: o.status, after: o.after, take: o.take ?? 100 } } })),
  },
  conflicts: {
    list: (o: { status?: string; assemblyVoteId?: string; afterId?: number; take?: number } = {}) =>
      call(client.GET('/api/conflicts', { params: { query: { status: o.status, assemblyVoteId: o.assemblyVoteId, afterId: o.afterId, take: o.take ?? 100 } } })),
    get: (id: number) => call(client.GET('/api/conflicts/{id}', { params: { path: { id } } })),
    // The contract lists every body property as required and nullable (C# positional records), so empty ones are sent as null.
    decide: (id: number, b: { decision: 'Approve' | 'Reject'; note?: string; expectedApprovedValue?: string }) =>
      call(
        client.POST('/api/conflicts/{id}/decision', {
          params: { path: { id } },
          body: { decision: b.decision, note: b.note ?? null, expectedApprovedValue: b.expectedApprovedValue ?? null },
        }),
      ),
  },
  votes: {
    list: (o: { assemblyVoteId?: string; text?: string; session?: number; dateFrom?: string; dateTo?: string; hasPosition?: boolean; afterId?: number; take?: number } = {}) =>
      call(client.GET('/api/votes', { params: { query: { ...o, take: o.take ?? 100 } } })),
    get: (id: number) => call(client.GET('/api/votes/{id}', { params: { path: { id } } })),
    timeline: (id: number) => call(client.GET('/api/votes/{id}/timeline', { params: { path: { id } } })),
    setPosition: (id: number, body: { stance: string; weight: number | null; expectedVersion: number }) =>
      call(client.PUT('/api/votes/{id}/position', { params: { path: { id } }, body })),
  },
  snapshots: {
    list: (take = 50) => call(client.GET('/api/snapshots', { params: { query: { take } } })),
    get: (id: number) => call(client.GET('/api/snapshots/{id}', { params: { path: { id } } })),
    create: (b: { name: string; dateFrom?: string; dateTo?: string; session?: number; assemblyVoteIds?: string[]; excludeVotesWithoutPosition?: boolean }) =>
      call(
        client.POST('/api/snapshots', {
          body: {
            name: b.name,
            dateFrom: b.dateFrom ?? null,
            dateTo: b.dateTo ?? null,
            session: b.session ?? null,
            assemblyVoteIds: b.assemblyVoteIds ?? null,
            excludeVotesWithoutPosition: b.excludeVotesWithoutPosition ?? null,
          },
        }),
      ),
    country: (id: number, countryCode: string, afterOrdinal = -1, take = 100) =>
      call(client.GET('/api/snapshots/{id}/countries/{countryCode}', { params: { path: { id, countryCode }, query: { afterOrdinal, take } } })),
    publish: (id: number, replace = false) => call(client.PUT('/api/snapshots/{id}/publish', { params: { path: { id } }, body: { replace } })),
  },
};
