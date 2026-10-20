# Meridian web (Angular)

The four screens of the assignment (section 6): **Imports**, **Review**, **Votes**, **Snapshots**. Angular 22, standalone components and signals, no UI library.

## Run

With the whole system: from the repository root, `docker compose up --build`, then open http://localhost:4200.

For development (API from `docker compose up -d db api`, or `dotnet run`, on http://localhost:5284):

```
cd src/web
npm install
npm start          # http://localhost:4200, /api is proxied to the API (proxy.conf.json), so no CORS is needed
npm test           # unit tests (vitest)
npm run build
```

Needs Node.js 22.22.3 or newer (or 24.15+), as Angular 22 requires.

## The API client comes from the contract

Nothing in `src/app/api/schema.d.ts` is written by hand. The client is `openapi-fetch`, typed by the types that
`openapi-typescript` generates from `openapi.json`, the OpenAPI document of the API. Paths, parameters, bodies and
responses are therefore checked by the compiler: if the back end changes in a way the screens do not follow, `npm run build` fails.

After a change in the API:

```
npm run api        # downloads http://localhost:5284/openapi/v1.json into openapi.json, then regenerates the types
npm run build      # shows what the change breaks in the screens
```

(`API_URL=http://host:port npm run api` for another address. The document is served only in the Development environment.)
`npm run gen:api` regenerates the types from the `openapi.json` already in the folder.

## What the screens do

- Every request sends `X-User` (the user switcher in the header) and a new `X-Correlation-Id`, which follows the request into the API logs.
- Long work (an import, a snapshot) is read from the server every second, so progress survives a page refresh (ING-02, SNP-02).
- A 400 shows its messages next to the field (`name`, `weight`, `dateFrom`, ...). A 409 is shown as a message with the current state, never as a generic error: a conflict somebody else decided first (CON-05), a position changed by another admin, votes without a position (POS-04), a quarter that already has a published snapshot (SNP-05).
- The role decides what is enabled (admins set positions and publish), but only as a convenience: the API enforces the roles.

Structure: `api/client.ts` (client, errors, endpoints), `imports/`, `review/`, `votes/`, `snapshots/` (one folder per screen), `format.ts` (display helpers), `shared.ts` (polling).
