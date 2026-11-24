# Hypa proxy (Cloud Function)

`functions/` is the only place the Hypa API key exists. Clients (web, Android, iOS, Quest) send a Firebase ID token and an App Check token; the function checks both, counts usage, calls Hypa, and returns the answer plus which model and voice produced it.

The public request and response fields follow G01 section 6 in [contracts.md](contracts.md). The Unity routes for story speech, transcription, chat and translation use this proxy.

## Calling it

All endpoints are `POST` with a JSON body.

| Header | Value |
| --- | --- |
| `Authorization` | `Bearer <Firebase ID token>` (anonymous sign-in is fine) |
| `X-Firebase-AppCheck` | App Check token |

Hosting rewrites `/hypa/**` to the function, so the web build calls same-origin paths (`/hypa/tts`) and needs no CORS. Native builds call the Hosting domain the same way.

| Route | Body | Success (plus `usage`) |
| --- | --- | --- |
| `/hypa/tts` | `text` (max 1000 chars), `language`, optional `voice` and `model` | `audioBase64`, `mime`, `model`, `voice` |
| `/hypa/asr` | `audioBase64` (max 1 MiB decoded and 30 s), `mime`, `language`, optional `durationSeconds` | `text`, `model` |
| `/hypa/chat` | Optional `system`, `messages`, `language`, optional `model` and `maxTokens` | `text`, `model` |
| `/hypa/translate` | `text` (max 2000 chars), `from`, `to` | `text`, `model` |

For ASR, set `language` to `auto` to omit the language hint. For translation, set `from` to `auto` to detect the source. Each response also includes the reported `route` and current user `usage`.

TTS requests inline audio in one call to Hypa. A named voice uses its named model when supplied. For a voice-only request, the proxy looks up a matching model and asks for an explicit model if several match. Chat uses the configured `HYPA_CHAT_DEFAULT_MODEL` when the request omits `model` (default `google/gemini-3.5-flash`). Explicit chat model IDs have the form `provider/model`.

### Errors

Every failure is `{ "error": { "code", "message", ... } }`.

| HTTP | `code` | When |
| --- | --- | --- |
| 401 | `unauthenticated` | no / invalid / expired ID token |
| 401 / 403 | `appcheck` | App Check token missing (401) / invalid (403) |
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

- **Production key:** a Hypa developer key created for this entry (its own spending cap in Hypa), stored only in Secret Manager: `firebase functions:secrets:set HYPA_API_KEY`. It is read through `defineSecret`, sent only in `x-api-key` to Hypa, and never logged or returned (no Hypa response body is logged either). Never put it in the repo, the Unity project, the form or the test instructions.
- **Local key:** copy `functions/secret.local.example` to `functions/.secret.local` (git-ignored) and put a **dev** key in it.
- **Other local settings:** copy `functions/.env.example` to `functions/.env.local` (git-ignored). `HYPA_SKIP_APP_CHECK=true` skips App Check (there is no App Check emulator; sign-in is still required) and `HYPA_API_BASE` points at a fake Hypa. Both are read in one place, `emulatorSettings()` in `functions/src/config.ts`, and only take effect inside the emulator, so a deployed function cannot skip App Check or send the key anywhere but the real API.

## Run locally

```sh
npm --prefix functions ci
npm --prefix functions run build
 firebase emulators:start --project demo-hypa --only "functions,firestore,auth,database"
```

The Firestore emulator needs Java 21+. To test the scene in Unity Editor, set `HYPA_SKIP_APP_CHECK=true` in the git-ignored `functions/.env.local`, then add these values to the git-ignored `.env` in the project root and restart Play Mode:

```dotenv
HYPA_BASE_URL=http://127.0.0.1:5001/demo-hypa/us-central1
HYPA_FIREBASE_AUTH_EMULATOR_URL=http://127.0.0.1:9099
```

Use the same project ID in `HYPA_BASE_URL` as in the emulator command. Editor sign-in uses the local Auth emulator and keeps its session in memory. The Functions emulator reads the dev key from `functions/.secret.local`.

To test a built WebGL site, include `hosting` in the emulator list. Hosting serves `Builds/Web` (output of `tools/build.sh web`) on port 5002 and rewrites `/hypa/**` to the Functions emulator. Emulator Hosting moved from 5000 to 5002 because macOS uses 5000 for AirPlay.

## Deploy (needs the owner's go-ahead)

```sh
firebase functions:secrets:set HYPA_API_KEY      # once, paste the entry-scoped key
firebase deploy --only functions,hosting
```

Set `HYPA_CHAT_DEFAULT_MODEL` in the Functions environment if this entry needs a different Gemini model or provider. Enable and verify the target platform's Firebase Auth and App Check integration before testing the deployed build.

### Judging window: warm instance

`MIN_INSTANCES` in `functions/src/config.ts` is `0`. On **Nov 27** change it to `1` and redeploy the function; after **Dec 16** change it back to `0` and redeploy. It is a constant, not a prompt, so the change is a reviewed commit.

## Realtime Database room cleanup (G09)

`cleanupRooms` runs every 30 minutes and deletes `/rooms/{id}` entries whose numeric `createdAt` (epoch ms) is older than 2 hours. The query only selects non-negative numeric timestamps strictly before the cutoff. The room path and field follow section 4 of [contracts.md](contracts.md), and the database rules index `createdAt`.

## Hosted acceptance

- `/gemini/generate`: needed if G02's Quest spike finds the AI Logic SDK can't run on Quest. See [quest-path.md](quest-path.md).
- Deploy, hosted-web check of speech and Yorùbá chat, and the `cp-a` tag: need the Firebase project, the entry's scoped key and G05's cloud-only route.
