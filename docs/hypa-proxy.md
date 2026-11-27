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
| `/hypa/tts` | `text` (max 1000 chars), `language`, optional voice, model, and inline | audioUrl by default, audioBase64 when inline is true, `mime`, `model`, `voice` |
| `/hypa/asr` | `audioBase64` (max 1 MiB decoded and 30 s), `mime`, `language`, optional `durationSeconds` and `model` | `text`, `model` |
| `/hypa/chat` | Optional `system`, `messages`, `language`, optional `model` and `maxTokens` | `text`, `model` |
| `/hypa/translate` | `text` (max 2000 chars), `from`, `to`, optional `model` | `text`, `model` |

The typed G01 client can also send ASR as audio and sourceLang, put chat instructions in a system message without a separate language field, and send translation with targetLang and sourceLang. Responses include transcript, content, and translation respectively, alongside the story client fields in the table.

For ASR, set `language` to `auto` to omit the language hint. For translation, set `from` to `auto` to detect the source. Each response also includes the reported `route` and current user `usage`.

TTS returns a signed audio link by default. The WebGL client explicitly requests inline audio to avoid a cross-origin fetch. For a voice-only request, the proxy looks up a matching model and asks for an explicit model if several match.

All four routes accept an optional `model`. With a blank model or `"auto"`, speech, transcription, and translation ask Hypa to choose, with the voice matching described above. Automatic chat tries Muse Spark, Gemini 3.5 Flash, Muse Glimmer, and Muse Wonder in that order. All chat attempts share one 20-second budget and count as one proxy request. An explicit `provider/model` is tried on its own. `model` in the response identifies the model that answered.

### Errors

Every failure is `{ "error": { "code", "message", ... } }`.

| HTTP | `code` | When |
| --- | --- | --- |
| 401 | `unauthenticated` | no / invalid / expired ID token |
| 401 / 403 | `app_check` | App Check token missing (401) / invalid (403) |
| 400 | `bad_request` | missing or malformed field |
| 413 | `too_large` | text, conversation, audio bytes or audio length over the cap |
| 429 | `quota` | `scope`: `user_seconds`, `user_requests`, `global_seconds`, `global_requests`, or `provider_balance` (Hypa key out of budget). The daily limits include `limit`, `used`, `resetsAt`, `retryAfterSeconds` (also the `Retry-After` header) |
| 502 / 503 / 504 | `upstream` | Hypa failed. `reason`: `timeout`, `unavailable`, `rejected`, `invalid_response`, `tool_error`, `model_unavailable`; `retryable` tells the app whether to offer retry |
| 404 / 405 | `not_found` / `method_not_allowed` | wrong route / not POST |
| 500 | `internal` | our bug; message is generic |

If Hypa reports that its balance is exhausted, the proxy advises waiting 300 seconds before retrying.

Upstream error bodies are never forwarded, and a failed Hypa call is refunded to the caller's quota.

## Limits

ASR needs FFmpeg on PATH. Install it for local emulator use and tests.

Counted per UTC day in Firestore: `usage/{uid}/days/{yyyy-mm-dd}` (per user) and `usage_global/{yyyy-mm-dd}` (everyone). Audio is priced as: TTS ≈ 15 characters per second; ASR from decoded audio duration rounded up, with no file-size estimate; chat and translate cost one request, zero seconds.

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

## Quest App Check

Use the direct HTTPS URL for the questAppCheck function. Both requests require the player's Firebase ID token as a bearer credential. The client obtains an App Check token only after the exchange below.

1. POST to /challenge on the function to receive nonce and expiresAt. Requesting a new challenge replaces the previous challenge for that player.
2. Pass the nonce to the Meta Platform SDK to request an attestation. POST the nonce and attestation JSON fields to the function root. A successful reply contains token and ttlMillis.
3. Send the issued token in X-Firebase-AppCheck on requests to the Hypa proxy.

For deployment, set QUEST_FIREBASE_APP_ID to the registered Firebase Android app ID, QUEST_ANDROID_PACKAGE_ID to the Quest build's package ID, and QUEST_ANDROID_SIGNING_SHA256 to the approved signing certificate's SHA-256 digest. Store the Meta app access token as META_QUEST_APP_ACCESS_TOKEN in Secret Manager. The runtime service account needs permission to sign Firebase App Check tokens.

The verifier requires a StoreRecognized app, the configured package and signer, and Advanced device integrity. Challenges expire after five minutes, and Firestore consumes each challenge in a transaction before contacting Meta. Failed exchanges require a new challenge. The questAppCheckChallenges collection is server-only. A Firestore TTL policy on expiresAt can remove abandoned challenges.

The Functions tests use simulated Meta responses. Connecting the production Quest client and verifying the full exchange on a supported physical headset are still pending.

## Deploy (needs the owner's go-ahead)

Always the `hypa-firebase` project, never `hypaall-v1` (production) or `hypa-webapp-bbe81`. Name it on every command, so a
different local `firebase use` or `gcloud` default cannot send the deploy or the key elsewhere (`gcloud` commands: `--project hypa-firebase`).

```sh
firebase functions:secrets:set HYPA_API_KEY -P hypa-firebase   # once, paste the entry-scoped key
firebase deploy --only functions:hypa,hosting -P hypa-firebase
```

Enable and verify the target platform's Firebase Auth and App Check integration before testing the deployed build. The web deployment above targets the Hypa proxy and Hosting. Deploy questAppCheck separately once its Meta settings are configured.

Use the registered Hypa Stories Web app and its reCAPTCHA Enterprise site key when building WebGL for release. See [Web bridge configuration](web-bridge.md).

### Judging window: warm instance

`MIN_INSTANCES` in `functions/src/config.ts` is `0`. On **Nov 27** change it to `1` and redeploy the function; after **Dec 16** change it back to `0` and redeploy. It is a constant, not a prompt, so the change is a reviewed commit.

## Realtime Database room cleanup (G09)

`cleanupRooms` runs every 30 minutes and deletes `/rooms/{id}` entries whose numeric `createdAt` (epoch ms) is older than 2 hours. The query only selects non-negative numeric timestamps strictly before the cutoff. The room path and field follow section 4 of [contracts.md](contracts.md), and the database rules index `createdAt`.

## Hosted acceptance

- `/gemini/generate`: needed if G02's Quest spike finds the AI Logic SDK can't run on Quest. See [quest-path.md](quest-path.md).
- Deploy, hosted-web check of speech and Yorùbá chat, and the `cp-a` tag: need the Firebase project, the entry's scoped key and G05's cloud-only route.
