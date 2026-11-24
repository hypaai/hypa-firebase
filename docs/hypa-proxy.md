# Hypa proxy (Cloud Function)

`functions/` is the only place the Hypa API key exists. Clients (web, Android, iOS, Quest) send a Firebase ID token and an App Check token; the function checks both, counts usage, calls Hypa, and returns the answer plus which model and voice produced it.

> **Contract note.** These shapes are the contract in `docs/contracts.md` §6 and in `ProxyApi` (`Models.cs`). They match the fields the Quest app already sends to Hypa. G05 calls these routes instead of `/v2/mcp` and `/v2/developer/*`.

## Calling it

All endpoints are `POST` with a JSON body.

| Header | Value |
| --- | --- |
| `Authorization` | `Bearer <Firebase ID token>` (anonymous sign-in is fine) |
| `X-Firebase-AppCheck` | App Check token |

Hosting rewrites `/hypa/**` to the function, so the web build calls same-origin paths (`/hypa/tts`) and needs no CORS. Native builds call the Hosting domain the same way.

| Route | Body | Success (plus `usage`) |
| --- | --- | --- |
| `/hypa/tts` | `text` (≤1000 chars), `language` (e.g. `"Yoruba"`), optional `voice` + `model` (a voice only works with its model), optional `inline: true` | `audioUrl` or `audioBase64`, `mime`, `sampleRate`, `route` |
| `/hypa/asr` | `audio` (base64, ≤1 MiB decoded, ≤30 s), optional `sourceLang`, optional `durationSeconds` | `transcript`, `language`, `audioSeconds`, `route` |
| `/hypa/chat` | `messages` (≤20, ≤8000 chars total, roles system/user/assistant), optional `model` (`"auto"` or `"provider/model"`), optional `maxTokens` (≤1024) | `content`, `route` |
| `/hypa/translate` | `text` (≤2000 chars), `targetLang`, optional `sourceLang` | `translation`, `language`, `route` |

`route` is `{ provider, model, voice }` as Hypa reported them, for the route chip. `usage` is `{ secondsUsed, secondsLimit, requestsUsed, requestsLimit, resetsAt }` for the caller today.

`inline: true` makes the function download Hypa's signed audio link and return base64, so a browser never fetches a third-party host (CORS). Without it the app gets Hypa's `audioUrl`.

### Errors

Every failure is `{ "error": { "code", "message", ... } }`.

| HTTP | `code` | When |
| --- | --- | --- |
| 401 | `unauthenticated` | no / invalid / expired ID token |
| 401 / 403 | `app_check` | App Check token missing (401) / invalid (403) |
| 400 | `bad_request` | missing or malformed field |
| 413 | `too_large` | text, conversation, audio bytes or audio length over the cap |
| 429 | `quota` | `scope`: `user_seconds`, `user_requests`, `global_seconds`, `global_requests`, or `provider_balance` (Hypa key out of budget). Includes `limit`, `used`, `resetsAt`, `retryAfterSeconds` (also the `Retry-After` header) |
| 502 / 503 / 504 | `upstream` | Hypa failed. `reason`: `timeout`, `unavailable`, `rejected`, `invalid_response`, `tool_error`; `retryable` tells the app whether to offer retry |
| 404 / 405 | `not_found` / `method_not_allowed` | wrong route / not POST |
| 500 | `internal` | our bug; message is generic |

Upstream error bodies are never forwarded, and a failed Hypa call is refunded to the caller's quota.

## Limits

Counted per UTC day in Firestore: `usage/{uid}/days/{yyyy-mm-dd}` (per user) and `usage_global/{yyyy-mm-dd}` (everyone). Audio is priced as: TTS ≈ 15 characters per second; ASR from the WAV header (or file size if it is not a WAV), never below `durationSeconds`; chat and translate cost one request, zero seconds.

Remote Config parameters (read server-side, cached 5 min; built-in defaults are placeholders to align with the Hypa key's own spending cap):

| Parameter | Default |
| --- | --- |
| `proxy_daily_seconds_per_user` | 600 |
| `proxy_daily_requests_per_user` | 400 |
| `proxy_global_daily_seconds` | 20000 |
| `proxy_global_daily_requests` | 10000 |

Usage documents carry `expireAt` (35 days). Create a Firestore TTL policy on `expireAt` for collection group `days` and for `usage_global`, or they are kept forever.

## Secrets and configuration

- **Production key:** a Hypa developer key created for this entry (its own spending cap in Hypa), stored only in Secret Manager: `firebase functions:secrets:set HYPA_API_KEY -P hypa-firebase`. It is read through `defineSecret`, sent only in `x-api-key` to Hypa, and never logged or returned (no Hypa response body is logged either). Never put it in the repo, the Unity project, the form or the test instructions.
- **Local key:** copy `functions/secret.local.example` to `functions/.secret.local` (git-ignored) and put a **dev** key in it.
- **Other local settings:** copy `functions/.env.example` to `functions/.env.local` (git-ignored). `HYPA_SKIP_APP_CHECK=true` skips App Check (there is no App Check emulator; sign-in is still required) and `HYPA_API_BASE` points at a fake Hypa. Both are read in one place, `emulatorSettings()` in `functions/src/config.ts`, and only take effect inside the emulator, so a deployed function cannot skip App Check or send the key anywhere but the real API.

## Run locally

```sh
cd functions && npm ci && npm run check   # typecheck, lint, formatting, unit tests (no Firebase needed)
cd .. && firebase emulators:start --only functions,firestore,auth,hosting
```

The Firestore emulator needs Java 21+. Hosting serves `Builds/Web` (output of `tools/build.sh web`) on port 5002 and rewrites `/hypa/**` to the Functions emulator. Emulator Hosting moved from 5000 to 5002 because macOS uses 5000 for AirPlay.

## Deploy (needs the owner's go-ahead)

Always the `hypa-firebase` project, never `hypaall-v1` (production) or `hypa-webapp-bbe81`. Name it on every command, so a
different local `firebase use` or `gcloud` default cannot send the deploy or the key elsewhere (`gcloud` commands: `--project hypa-firebase`).

```sh
firebase functions:secrets:set HYPA_API_KEY -P hypa-firebase   # once, paste the entry-scoped key
firebase deploy --only functions,hosting -P hypa-firebase
```

Nothing has been deployed from this repository yet.

### Judging window: warm instance

`MIN_INSTANCES` in `functions/src/config.ts` is `0`. On **Nov 27** change it to `1` and redeploy the function; after **Dec 16** change it back to `0` and redeploy. It is a constant, not a prompt, so the change is a reviewed commit.

## Realtime Database room cleanup (G09)

`cleanupRooms` runs every 30 minutes and deletes `/rooms/{id}` entries whose numeric `createdAt` (epoch ms) is older than 2 hours. Rooms without a numeric `createdAt` are left alone. The path and field are **assumptions** (`ROOMS_PATH`, `ROOMS_CREATED_FIELD` in `functions/src/config.ts`): multiplayer is undecided, so align them with G09's data model or remove the function. For a large `rooms` tree, add `".indexOn": ["createdAt"]` under `rooms` in `firebase/database.rules.json`.

## Not done here

- `/gemini/generate`: only needed if G02's Quest spike asks for it; nothing in the repo does.
- Deploy, hosted-web check of speech and Yorùbá chat, and the `cp-a` tag: need the Firebase project, the Hypa key and G05's cloud route.
