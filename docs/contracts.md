# Day 0 contracts (G01)

The interfaces, data shapes and wire formats everyone builds against, with fakes behind them. The code is in
`Assets/HypaCore/Runtime/Firebase/` (namespace `HypaCore.Hybrid`: not `HypaCore.Firebase`, which would hide the Firebase SDK's own `Firebase` namespace).

## 1. Platform services

`IHypaAuth`, `IProgressStore`, `IRemoteFlags`, `IRoomSync`, `IOnDeviceModel`, `ICloudModel` (see `Interfaces.cs`).

- Two real implementations of each: **Native** (Firebase Unity SDK; Android, iOS, Quest; G02) and **Web** (`.jslib` + Firebase JS SDK; G03). **Fake** (in-memory) for the editor and tests (`Fakes.cs`).
- `HybridServices.Current` picks one at start-up: WebGL build → Web; Android, iOS, Quest → Native; editor → Fake unless "Use real Firebase" is ticked (`PlayerPrefs` key `hypa.useRealFirebase`). G02 sets `HybridServices.NativeFactory`, G03 sets `WebFactory`. With no factory registered the Fakes are used.
- `IOnDeviceModel` implementations: `ChromeNanoModel` (G03), `AndroidNanoModel` (G06), `AppleFoundationModel` (G07), `NoOnDeviceModel` (Quest, editor).
- `IRoomSync` is empty until G09 defines it: create or join a room, presence, story clock, pose stream.

## 2. Hybrid route decision (G05 owns the logic)

```csharp
public enum Route { OnDevice, CloudGemini, Hypa, Offline }
public sealed record RouteDecision(Route Route, string Model, string Reason, int LatencyMs);
```

Shown as a chip: "On device · Gemini Nano · 0.4 s", "Cloud · Gemini · 1.2 s", "Hypa · Yorùbá · 2.1 s", "Offline · on device".

## 3. Firestore (progress, G08)

```
users/{uid}                      { displayName?, language, createdAt, lastDevice, lastSeenAt }
users/{uid}/progress/current     { storyId, lineId, lineIndex, language, place: "camp"|"hut"|"village", updatedAt, device }
users/{uid}/stories/{storyId}    { finished, bestGameScore, gamePlays, lastLineId, updatedAt }
users/{uid}/words/{wordId}       { text, translation, language, storyId, savedAt }
```

A user reads and writes only `users/{their uid}/**` (`firebase/firestore.rules`). `Progress` in `Models.cs` is `progress/current`.

**Provisional (G08, waiting for Timi's OK):** `stories/*` and `words/*` go through `IUserDataStore` (`GetStoryAsync`, `SaveStoryAsync`, `ListWordsAsync`, `SaveWordAsync`) with `StoryResult` and `SavedWord`, in `UserData.cs`. Results merge: finished stays finished, `bestGameScore` and `gamePlays` take the higher, `lastLineId` comes from the later update. A word's id is built from its text and language, so the same word saved on two devices is one document.

## 4. Realtime Database (rooms, G09)

```
rooms/{code}                 { host, storyId, language, state: "lobby"|"story"|"game", createdAt }
rooms/{code}/clock           { lineIndex, startedAtServerMs, paused }       // host writes
rooms/{code}/players/{uid}   { name, platform, seat: 0|1, online, handUp }  // onDisconnect → online=false
rooms/{code}/poses/{uid}     { h: [x,y,z,qx,qy,qz,qw], l: [...], r: [...], t } // ≤ 10 Hz, head + hands
rooms/{code}/game/{uid}      { startedAt, finishedMs, score }
```

Codes are 5 letters, no vowels-only words. Rooms expire after 2 hours (scheduled cleanup in G04's functions).

Rules (`firebase/database.rules.json`): signed-in players read a room; the host creates it and is the only one who changes the room and its clock; each player writes only their own `players`, `poses` and `game` entries. In the Realtime Database a grant higher up cannot be taken back lower down, so the host's right to the whole room also covers other players' entries.

## 5. Remote Config keys

| Key | Default | Used by |
| -- | -- | -- |
| `route_prefer_on_device` | `true` | G05 |
| `route_on_device_langs` | `en,fr,es` | G05 |
| `route_hypa_langs` | `yo,ha,ig,pcm,sw` | G05 |
| `route_max_on_device_tokens` | `5000` | G05 |
| `cloud_model` | `gemini-2.5-flash-lite` | G05 |
| `live_model` | (Live API model name from the Firebase docs on Nov 22) | G10 |
| `live_langs` | `en,fr,es,sw` | G10 |
| `proxy_daily_seconds_per_user` | `600` | G04 |
| `proxy_daily_requests_per_user` | `400` | G04 |
| `proxy_global_daily_seconds` | `20000` | G04 |
| `proxy_global_daily_requests` | `10000` | G04 |

Constants and defaults: `RemoteConfigKeys` in `Models.cs`. `live_model` has no default until G10 sets it.

## 6. Hypa proxy API (G04 builds it, G05, G10 and G12 call it)

All `POST`, JSON, headers `Authorization: Bearer <Firebase ID token>` and `X-Firebase-AppCheck: <token>`.

These are the shapes the Cloud Function already serves, the same fields the Quest app sends to Hypa (`text_to_speech`, `speech_to_text`, `translate_message`, `/v2/developer/chat`).

| Path | Body | Returns |
| -- | -- | -- |
| `/hypa/tts` | `{ text, language, voice?, model?, inline? }` | `{ audioUrl?, audioBase64?, mime, sampleRate, route, usage }` |
| `/hypa/asr` | `{ audio, sourceLang?, durationSeconds? }` | `{ transcript, language?, audioSeconds, route, usage }` |
| `/hypa/chat` | `{ messages, model?, maxTokens? }` | `{ content, route, usage }` |
| `/hypa/translate` | `{ text, targetLang, sourceLang? }` | `{ translation, language?, route, usage }` |
| `/gemini/generate` (optional) | `{ prompt, model? }` | `{ text, model }` |

`messages` is `[{ role, content }]` with roles `system`, `user` or `assistant`. `model` is `"auto"` or `"provider/model"`. `inline: true` makes TTS return `audioBase64` instead of Hypa's signed `audioUrl`. `route` is `{ provider, model, voice }`. `usage` is `{ secondsUsed, secondsLimit, requestsUsed, requestsLimit, resetsAt }`.

`/gemini/generate` is built only if G02's Quest spike finds the Firebase AI Logic SDK does not run on Quest (G04). It calls Gemini through the AI Logic server path or Vertex and backs `ICloudModel.GenerateAsync` there.

Errors: `{ error: { code, message } }`. `code` is `quota`, `unauthenticated`, `app_check`, `bad_request`, `too_large`, `upstream`, `not_found`, `method_not_allowed` or `internal`.
`HypaRestClient` gets a "proxy mode": same calls, these paths, Firebase tokens instead of an API key. Request and response shapes: `ProxyApi` and the `*Request` / `*Response` classes in `Models.cs`.

## 7. Shared rules

- Every new feature is behind an interface with a Fake, so it runs in the editor without Firebase.
- Platform code only in `Assets/HypaCore/Runtime/Firebase/{Native,Web}` and `Assets/Plugins/{Android,iOS,WebGL}`. Stories never reference Firebase types directly.
- Every PR: EditMode tests pass, a web build passes in CI, Randy runs it on web and Android.
