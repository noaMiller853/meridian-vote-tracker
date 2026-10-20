// Downloads the OpenAPI document of the running API into openapi.json (the contract the client is generated from).
// Usage: npm run api            (API on http://localhost:5284, e.g. after `docker compose up`)
//        API_URL=http://host:port npm run api
import { writeFileSync } from 'node:fs';

const base = process.env.API_URL ?? 'http://localhost:5284';
const url = `${base}/openapi/v1.json`;

let response;
try {
  response = await fetch(url);
} catch (e) {
  console.error(`Cannot reach ${url}. Start the API first (docker compose up) or set API_URL.`);
  process.exit(1);
}
if (!response.ok) {
  console.error(`${url} answered ${response.status}. The OpenAPI document is served only in the Development environment.`);
  process.exit(1);
}
const doc = await response.json();
writeFileSync(new URL('../openapi.json', import.meta.url), JSON.stringify(doc, null, 2) + '\n');
console.log(`Saved ${Object.keys(doc.paths).length} paths from ${url} to openapi.json`);
