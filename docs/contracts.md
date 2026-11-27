# Day 0 contracts (G01)

The interfaces, data shapes and wire formats everyone builds against, with fakes behind them, so five people can build in parallel.
The code is in `Assets/HypaCore/Runtime/Firebase/` (namespace `HypaCore.Hybrid`: not `HypaCore.Firebase`, which would hide the
Firebase SDK's own `Firebase` namespace). Every feature in the hackathon build runs on Firebase; section 0 says which product does what.

## 0. Firebase everywhere

| Feature | Firebase product | Android, iOS | Web (WebGL) | Quest |
| -- | -- | -- | -- | -- |
| Sign-in (anonymous, then Google / Apple) | Authentication | Firebase Unity SDK | Firebase JS SDK | Auth REST API |
| Proof the call comes from our app | App Check | Play Integrity / App Attest | reCAPTCHA Enterprise | Custom provider: a Cloud Function verifies Meta's Platform Integrity attestation and mints the App Check token |
| Progress, profile, saved words | Cloud Firestore | SDK | JS SDK | Firestore REST API |
| Two-player rooms | Realtime Database | SDK | JS SDK | REST API + server-sent events |
| Flags | Remote Config | SDK | JS SDK | Remote Config fetch API |
| Cloud Gemini | Firebase AI Logic | SDK | JS SDK | Through a Cloud Function |
| Talking with the old woman (Gemini Live) | Firebase AI Logic Live API | WebSocket from C# (`RestLiveModel`) | JS SDK `getLiveGenerativeModel` | WebSocket from C# (`RestLiveModel`) |
| Hypa (Yorùbá, Hausa, Igbo, Pidgin, Swahili) | Cloud Functions + Secret Manager (the Hypa key) | proxy | proxy | proxy |
| Room cleanup, quotas | Cloud Functions (scheduled) + Firestore | | | |
| The web build | Hosting | | | |

Quest has no Google Play services, so the Firebase Android SDK may not run there and Play Integrity cannot run. The Quest column
shows the expected REST path (G02's `HypaFirebase.Rest`, same Firebase project); G02's spike decides between it and the SDK
(`docs/quest-path.md`). Its App Check must not use a debug token
outside development: debug tokens skip the check.

## 1. Platform services

`IHypaAuth`, `IProgressStore`, `IProfileStore`, `IRemoteFlags`, `IRoomSync`, `IOnDeviceModel`, `ICloudModel`, `IRouteLog`
(`Interfaces.cs`), and `ILiveModel` / `ILiveSession` for the Live API (G10, `Live.cs`; `NoLiveModel` until a platform sets one).

- Three real implementations: **Native** (Firebase Unity SDK; Android, iOS, and Quest if the spike passes; G02), **Rest**
  (Firebase REST APIs; Quest if the SDK fails there; G02, in `Assets/HypaFirebase/Runtime/Rest`) and **Web** (`.jslib` + Firebase JS SDK; G03). **Fake** (in-memory) for the editor and tests
  (`Fakes.cs`): the stand-in until real services start, and when the config file is missing.
- `HybridServices.Current` picks one at start-up: WebGL build → Web; Android, iOS, Quest and the editor → `NativeFactory` (on Quest G02 registers the SDK or the REST services, whichever the spike picks;
  without one, `FirebaseStartup` starts the REST services from the local config file). G02 sets `HybridServices.NativeFactory`, G03 sets
  `WebFactory`. With no factory registered the Fakes are used, so the app always runs.
- `HybridServices.UpgradeAsync` starts the real services in the background and swaps them in; progress and a profile saved to the
  Fakes meanwhile are sent if they are newer. Always read services through `HybridServices.Current`.
- `IOnDeviceModel` implementations: `ChromeNanoModel` (G03), `AndroidNanoModel` (G06), `AppleFoundationModel` (G07),
  `NoOnDeviceModel` (Quest, editor).
- `ICloudModel.GenerateAsync(prompt, tools, ct)` is Firebase AI Logic function calling: the implementation declares each `CloudTool`
  (name, description, string parameters), runs the model's calls with `CloudTool.Run`, sends the results back and returns the final
  text. G05 passes Hypa's `translate(text, to)` and `define(word, language)`.
- `IRouteLog.Log(RouteEvent)` writes anonymous route statistics to `routes/{uid}/events` (section 3). Fire and forget.
  `RestRouteLog` (`QuestRestServices`) does it over Firestore REST: events wait in memory (at most 50) while the network is down
  and go out with the next one; an event Firestore refuses is dropped.
- `RestCloudModel` (`QuestRestServices`) is cloud Gemini through Firebase AI Logic over REST, the same request the JS SDK makes
  (`firebasevertexai.googleapis.com/v1beta/projects/{project}/models/{cloud_model}:generateContent`, Gemini Developer API, with the
  Firebase ID token and App Check token), including function calling. `FirebaseStartup` starts these services when the app
  opens on a platform without `NativeFactory` (Quest, and phones until the SDK path from G02 lands), from `hypa.firebase.local.json`
  in the app's data folder (`docs/quest-path.md`); without that file the app plays on the Fakes. It also fetches Remote Config.
- `AndroidNanoModel` (G06) is Gemini Nano through Google ML Kit's GenAI Prompt API (AICore), reached by a small Java bridge
  (`Assets/HypaCore/Plugins/Android/HypaNano.androidlib`, pinned `genai-prompt:1.0.0-beta5`, needs API 26). It answers on the device
  only; when it cannot, the router sends the question to the cloud model, so the Hypa proxy stays the one cloud path. The platform
  registers it with `HybridServices.OnDeviceFactory`, which every new `HybridServices` reads, so the real services built by
  `NativeFactory` get it too. `Available` is the last status seen (the router reads it without waiting; the registration reads the
  status once at start-up, and `StatusAsync()` refreshes it). Status is "available", "downloadable", "downloading" or
  "unavailable"; on Quest and on a phone with no AICore it is "unavailable" and nothing runs. A failed reply throws
  `OnDeviceModelException` with a short `Reason` ("busy", "background", "quota", "no-space", "needs-update", "unavailable",
  "too-large", "cancelled", "error"), never the prompt. If the status is "downloadable", `IDownloadableOnDeviceModel.DownloadAsync`
  fetches the model with progress for the settings screen. Limits set by Google: text in, text out, one question at a time,
  the prompt under about 4,000 tokens, and only while the app is in the foreground. Tests: `AndroidNanoModelTests` (a fake
  bridge, so they run anywhere) and the `Android on-device bridge` workflow (compiles the Java against the real library).
  A platform's model registers `HybridServices.OnDeviceFactory`, and every service set (the Fakes too) uses it, so on-device
  answers do not wait for Firebase. iOS: `docs/ios-on-device.md`.
- Tokens are fetched per call and never stored or logged by gameplay code.

## 2. Hybrid route decision (G05 owns the logic, everyone reads it)

```csharp
public enum Route { OnDevice, CloudGemini, Hypa, Offline }
public sealed record RouteDecision(Route Route, string Model, string Reason, int LatencyMs);
```

`decision.Chip()` is the UI chip: "On device · Gemini Nano · 0.4 s", "Cloud · Gemini · 1.2 s", "Hypa · Yorùbá · 2.1 s",
"Offline · on device". `Model` is what the chip names (the language for Hypa); `Reason` is the short "why" shown when the chip is tapped.

`HybridRouter.Decide(RouteAsk, RouteContext)` (`HybridRouter.cs`, pure) decides, for an answer:

1. Offline (no network, or a call failed to reach a server in the last 30 s, `NetworkStatus`): the device's model if it is ready,
   else the saved answers (`Model` "saved answers").
2. A language in `route_hypa_langs`: Hypa (`/hypa/chat`).
3. A language in `route_on_device_langs`, the model `available`, the prompt within `route_max_on_device_tokens` and no tools needed:
   on device.
4. Otherwise cloud Gemini. A model still downloading shows "On-device model downloading (first time only)" in the
   story's language (`QaLines.Preparing`).

Settings (`HypaSettings.AiMode`): Hybrid (default), Cloud only (never the device), On device only (never the network). For a
translation: English, French and Spanish pairs on device, everything else (and auto-detect) through Hypa (`/hypa/translate`).
Stories' `HybridStoryQa` and `HybridTryApi` run the decision and fall back when a path fails: Hypa → cloud; device or a reply in
the wrong language → cloud; cloud → device → saved answers.

## 3. Firestore (progress and profile, G08)

```
users/{uid}                      { username?, displayName?, language, appearance?, appearanceVersion?, createdAt, lastDevice, lastSeenAt }
users/{uid}/progress/current     { storyId, lineId, lineIndex, language, place: "camp"|"hut"|"village", updatedAt, device }
users/{uid}/stories/{storyId}    { finished, bestGameScore, gamePlays, lastLineId, updatedAt }
users/{uid}/words/{wordId}       { text, translation, language, storyId, savedAt }
routes/{uid}/events/{eventId}    { kind: "answer"|"translate", route: "onDevice"|"cloud"|"hypa"|"offline", model, language,
                                   latencyMs, ok, fallbackFrom?, device, at }
```

- Types: `UserProfile`, `Progress` and paths `FirestorePaths` (`Models.cs`); `StoryResult` and `SavedWord` come with G08
  (`UserData.cs`). Times are Unix ms (a server timestamp is also accepted). A story result may carry `storyId`, equal to its document id.
- `users/{uid}` carries the profile card: `username` (3–20 of `a-z 0-9 _`, starting with a letter; a display handle, not unique),
  `displayName` (≤ 24) and `appearance`: the app's own JSON for the character (≤ 4000 characters), versioned by
  `appearanceVersion`. Stories maps its `StoryProfile` to it, so a character made on one device follows the player; the platform
  layer never needs to know the character model. `IProfileStore` loads and saves it: `RestProfileStore` (Quest, phones; Firestore REST with an offline copy) and
  `WebProfileStore` (G03's bridge). `StoryProfileSync` sends every save from the character creator, and at launch and when the
  real services come up takes whichever side was saved last (`lastSeenAt` against the device's `savedAt`).
- `routes/{uid}/events` (`RouteEvent`, G05) is anonymous: no question, answer or text. Written unless the player turns
  "Anonymous route stats" off; G14 reads it with the Admin SDK ("62% of answers ran on device").
- Rules (`firebase/firestore.rules`): a user reads and writes only `users/{their uid}` and the three collections above, and only
  the listed fields with the listed types; they may only add `routes/{their uid}/events`, never read or change them. Everything
  else is closed, including other documents under `users/{uid}`.

**Provisional (G08, waiting for Timi's OK):** `stories/*` and `words/*` go through `IUserDataStore` (`GetStoryAsync`, `SaveStoryAsync`, `ListWordsAsync`, `SaveWordAsync`) with `StoryResult` and `SavedWord`, in `UserData.cs`. Results merge: finished stays finished, `bestGameScore` and `gamePlays` take the higher, `lastLineId` comes from the later update. A word's id is built from its text and language, so the same word saved on two devices is one document.

## 4. Realtime Database (rooms, G09)

```
rooms/{code}                 { host, storyId, language, state: "lobby"|"story"|"game", createdAt }
rooms/{code}/clock           { lineIndex, startedAtServerMs, paused }          // host writes
rooms/{code}/players/{uid}   { name, platform, seat: 0|1, online, handUp }     // onDisconnect → online = false
rooms/{code}/poses/{uid}     { h: [x,y,z,qx,qy,qz,qw], l: [...], r: [...], t } // ≤ 10 Hz, head + hands; an untracked hand is left out
rooms/{code}/game/{uid}      { startedAt, finishedMs, score }
```

- `IRoomSync` (`Interfaces.cs`) is the contract G09 implements; the types are in `Rooms.cs`. Create a room (you take seat 0), join
  (seat 1; `RoomException` NotFound, Full or Expired), leave (the host leaving closes the room), host-only state and clock, your own
  entry and hand, poses (fire and forget, at most 10 a second), game results, and events for each. `ServerTimeMs` estimates the
  server clock (`.info/serverTimeOffset`); `RoomClock.ElapsedMs(serverNow)` says how far into the line both players are.
- `FakeRoomSync` clients on the same `FakeRoomServer` see each other like two devices (tests: `HybridRoomsTests`).
- Codes: `RoomCode.New()`, 5 letters from `BCDFGHJKLMNPQRSTVWXZ` (no vowels, so no words; no Y), shown and compared in upper case.
  Rooms expire after 2 hours; G04's scheduled `cleanupRooms` deletes them.
- Rules (`firebase/database.rules.json`):
  - Anyone signed in can see a room's lobby (`host`, `storyId`, `language`, `state`, `createdAt`, `players`), so they can join.
    Its `clock`, `poses` and `game` are visible only to players in the room. Listen to these children separately: the room node
    as a whole is not readable.
  - The host creates (with a valid code) and deletes the room, and alone changes `storyId`, `language`, `state` and `clock`.
    `host` and `createdAt` never change.
  - Each player writes only their own `players`, `poses` and `game` entries, and `poses` and `game` only once seated. The host
    cannot overwrite other players' entries. Create the room first, then your `players` entry.
  - Every node accepts only its contract fields and types.

## 5. Remote Config keys

| Key | Default | Used by |
| -- | -- | -- |
| `route_prefer_on_device` | `true` | G05 |
| `route_on_device_langs` | `en,fr,es` | G05 |
| `route_hypa_langs` | `yo,ha,ig,pcm,sw` | G05 |
| `route_max_on_device_tokens` | `5000` | G05 |
| `cloud_model` | `gemini-3.5-flash-lite` | G05 |
| `live_model` | `gemini-2.5-flash-native-audio-preview-12-2025` (a native-audio model on the Gemini Developer API; `gemini-3.1-flash-live-preview` is the newer preview) | G10 |
| `live_langs` | `en,fr,es,sw` | G10 |
| `live_voice` | `Gacrux` (a prebuilt woman's voice; if refused, `Aoede`; blank for the model's default, a man's) | G10 |
| `talk_max_seconds` | `300` (one conversation with the old woman, for judges' cost) | G10 |
| `proxy_daily_seconds_per_user` | `600` | G04 (server) |
| `proxy_daily_requests_per_user` | `400` | G04 (server) |
| `proxy_global_daily_seconds` | `20000` | G04 (server) |
| `proxy_global_daily_requests` | `10000` | G04 (server) |

Constants and defaults: `RemoteConfigKeys` in `Models.cs`.

## 6. Hypa proxy API (G04 builds it as Cloud Functions, G05, G10 and G12 call it)

All `POST`, JSON, headers `Authorization: Bearer <Firebase ID token>` and `X-Firebase-AppCheck: <token>`. The functions verify
both, read the Hypa key from Secret Manager, and count each user's use per UTC day. Hosting rewrites `/hypa/**` to the function, so
the web build calls same-origin paths. These are the shapes G04's function implements (`docs/hypa-proxy.md`).

| Path | Body | Returns (plus `usage`) |
| -- | -- | -- |
| `/hypa/tts` | `{ text (≤ 1000), language, voice?, model?, inline? }` | `{ audioUrl \| audioBase64, mime, sampleRate, route }` |
| `/hypa/asr` | `{ audio (base64, ≤ 1 MiB, ≤ 30 s), sourceLang?, durationSeconds?, model? }` | `{ transcript, language, audioSeconds, route }` |
| `/hypa/chat` | `{ messages (≤ 20, ≤ 8000 characters, roles system / user / assistant), model?, maxTokens? (≤ 1024) }` | `{ content, route }` |
| `/hypa/translate` | `{ text (≤ 2000), targetLang, sourceLang?, model? }` | `{ translation, language, route }` |
| `/gemini/generate` (optional) | `{ prompt, model? }` | `{ text, model }` |

- `route` is `{ provider, model, voice }` as Hypa reported them, for the route chip. `usage` is `{ secondsUsed, secondsLimit,
  requestsUsed, requestsLimit, resetsAt }`.
- TTS: named voices are matched to a model when no model is supplied. The default response contains a signed audio link. WebGL sends inline: true to receive Base64 audio.
- Chat: the system prompt can be the first message with role system. Auto tries the ordered VR models with one 20-second deadline. An explicitly pinned model is tried on its own.
- The existing story client can also use the fields in [the proxy runbook](hypa-proxy.md).
- `/gemini/generate` is built only if G02's Quest spike finds the Firebase AI Logic SDK does not run on Quest (G04). It calls Gemini
  through the AI Logic server path or Vertex and backs `ICloudModel.GenerateAsync` there.

Errors: `{ error: { code, message, ... } }`.

| HTTP | `code` | Extra fields |
| -- | -- | -- |
| 401 | `unauthenticated` | |
| 401, 403 | `app_check` | |
| 400 | `bad_request` | |
| 413 | `too_large` | |
| 429 | `quota` | `scope` (`user_seconds`, `user_requests`, `global_seconds`, `global_requests`, `provider_balance`), `limit`, `used`, `resetsAt`, `retryAfterSeconds` |
| 502, 503, 504 | `upstream` | `reason` (`timeout`, `unavailable`, `rejected`, `invalid_response`, `tool_error`), `retryable` |
| 404, 405, 500 | `not_found`, `method_not_allowed`, `internal` | |

Client: `HypaProxyClient` (`HypaProxyClient.cs`): typed calls for the four paths, both tokens on every call, optional fields left
out, errors thrown as `HypaProxyException` (`Code`, `Status`, `Details`, `Retryable`, `RetryAfterSeconds`). Types: `ProxyApi` and
the `*Request` / `*Response` classes in `Models.cs`. `HypaProxyClient.FromConfig()` uses the proxy URL from `HypaConfig` and the
current account. The older `HypaRestClient` (Hypa's developer API shapes) stays for existing features until they move to these
paths. No Hypa key is ever in the app.

### Cloud Functions (G04)

| Function | Trigger | Does |
| -- | -- | -- |
| `hypa` (`/hypa/tts`, `asr`, `chat`, `translate`) | HTTPS | The proxy above. |
| `hypa` (`/gemini/generate`, optional) | HTTPS | Gemini for Quest, only if the Firebase AI Logic SDK does not run there (above). |
| `questAppCheck` | HTTPS | Exchanges a Meta Platform Integrity attestation for an App Check token (Admin SDK `createToken`), so Quest passes App Check. |
| `cleanupRooms` | Scheduled, every 30 minutes | Deletes `rooms/{code}` older than 2 hours (indexed on `createdAt`). |

## 7. Shared rules

- Every new feature is behind an interface with a Fake, so it runs in the editor without Firebase.
- Platform code only in `Assets/HypaFirebase/Runtime/{Native,Rest,Web}` and `Assets/Plugins/{Android,iOS,WebGL}`. Stories and Lens
  never reference Firebase types directly; they use `HybridServices.Current`.
- Every PR: EditMode tests pass, a web build passes in CI, Randy runs it on web and Android.

## Testing the rules

The emulator tests are in `firebase/tests/rules.test.mjs` (Firestore and Realtime Database). The Firebase CLI needs Java 21 or later.

```sh
cd firebase/tests && npm install && cd ../..
npx --prefix firebase/tests firebase emulators:exec --only firestore,database --project demo-hypa "cd firebase/tests && node --test rules.test.mjs"
```
