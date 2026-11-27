# Submission work

New work done for the Firebase hackathon. Imported work goes in `docs/pre-existing-work.md`.

## G01 (HYP-196): Day 0 contracts

The shared interfaces, data shapes and wire formats, so G02–G12 can build in parallel. The contract is `docs/contracts.md`.

- Interfaces in `Assets/HypaCore/Runtime/Firebase/` (namespace `HypaCore.Hybrid`): `IHypaAuth`, `IProgressStore`, `IRemoteFlags`, `IRoomSync` (empty until G09), `IOnDeviceModel`, `ICloudModel`.
- Types: `Route` / `RouteDecision`, `Progress`, `RemoteConfigKeys` with defaults, `ProxyApi` and the proxy request and response classes.
- The proxy wire format now matches the Cloud Function and the Quest app: `audio` / `audioUrl`, `targetLang`, `messages` plus `maxTokens`, `route`, `usage`, and the error code `app_check`. Daily audio limit default is 600 seconds.
- Fakes for every interface, and `HybridServices.Current` (WebGL: Web, builds: Native, editor: real Firebase from the local config file, Fakes until it starts).
- `firebase/firestore.rules` (each user only their own `users/{uid}/**`) and `firebase/database.rules.json` (rooms: the host controls the room and clock, each player writes only their own entries), with emulator tests in `firebase/tests/`.
- Tests: EditMode tests in `Assets/HypaCore/Tests/Editor/HybridContractsTests.cs`, 9 rules tests on the Firebase emulators.
- Merged without the four reviews the ticket asks for.

## G01 (HYP-196), part 2: rooms, profile and proxy client

Filled in what the first draft left open. The contract is still `docs/contracts.md`.

- `IRoomSync` for G09, with its types in `Rooms.cs`: create, join (not found, full, expired), leave, host-only state and clock, presence and raised hand, poses at most 10 a second, game results, server time. `RoomCode`: 5 letters without vowels, 2-hour lifetime. `FakeRoomSync` clients sharing a `FakeRoomServer` act like two devices.
- `UserProfile` (`users/{uid}`: username, display name, the character as JSON) and `IProfileStore` with a fake, so a character follows the player across devices. `UpgradeAsync` also hands the profile over. `FirestorePaths`.
- `RouteDecision.Chip()` for the route chip.
- Section 6 of the contract now matches G04's Cloud Function (field names, `route` and `usage` in replies, error codes and details). `HypaProxyClient`: typed calls to `/hypa/*` with the ID token and App Check token, errors as `HypaProxyException`.
- Rules: each Firestore document accepts only its contract fields and types; Realtime Database rooms check the code format, the host can no longer overwrite other players, and clock, poses and game results are readable only by players in the room. `createdAt` is indexed for `cleanupRooms`.
- `docs/contracts.md`: which Firebase product does each feature on each platform (Quest through the REST APIs), and the Cloud Functions list.
- Tests: `HybridRoomsTests`, `HypaProxyClientTests`, a profile case in `HybridUpgradeTests`; 15 rules tests on the Firebase emulators.
- Not yet tested against real Firebase or on a device.

## G02 (HYP-197), part 1: REST auth and App Check for Quest

Fallback for Quest in case the Firebase Unity SDK does not run there. Code in `Assets/HypaFirebase/Runtime/Rest/`.

- `RestAuthSession`: `IAuthSession` over the Identity Toolkit and Secure Token REST APIs. Signs in anonymously on first launch and brings the same user back later using a saved refresh token. If the saved token is dead (`INVALID_REFRESH_TOKEN`, `TOKEN_EXPIRED`, `USER_NOT_FOUND`), it clears the token and signs in as a new user. A refresh that finishes after `SignOut` is ignored.
- `RestAppCheck`: swaps an App Check debug token (registered in the Firebase console) for an App Check token, for Quest and the editor.
- `IRefreshTokenStore` with a PlayerPrefs version, and `HypaFirebaseException`.
- No token is logged. The web API key and app id are passed in, not committed.
- Tests: EditMode tests in `Assets/HypaFirebase/Tests/Editor/RestAuthTests.cs`, using a fake HTTP handler.
- Not yet tested against real Firebase or on a device.

## G02 (HYP-197), part 2: Quest REST services

The rest of the Quest REST path, and the start-up that moves from the Fakes to the real services. How to run the Quest spike: `docs/quest-path.md`.

- `RestHypaAuth`: `IHypaAuth` over `RestAuthSession` and `RestAppCheck`. Google and Apple linking are not available on Quest, so Quest users stay anonymous.
- `RestProgressStore`: `IProgressStore` over Firestore REST, with its own offline queue. Saves go out one at a time, so an older save never overwrites a newer one.
- `RestRealtimeDatabase`: get, set, update, delete and a server-sent-events stream, for G09. REST has no `onDisconnect`, so presence needs a heartbeat.
- `HybridServices.UpgradeAsync`: starts the real services in the background and swaps them in, sending progress saved on the Fakes meanwhile.
- `PlayerPrefsKeyValueStore`: lets the REST classes store data from any thread (PlayerPrefs works only on Unity's main thread).
- `QuestRestSpike` / `QuestRestSpikeRunner`: runs each REST call on a Quest and logs which worked. `NoOnDeviceModel` for platforms without one.
- Tests: EditMode tests in `Assets/HypaFirebase/Tests/Editor/` and `HybridUpgradeTests`.
- Not yet tested against real Firebase or on a Quest.

## Stories: character profile

A profile card for Stories with a realistic character the player shapes. Code in `Assets/HypaStories/Runtime/Profile/`.

- `ProfileCreator`: a simple card with the character as a round portrait, rendered only when something changes, and a mixed-reality panel close to the player on Quest.
- `ProfileAvatar`, `ProfileCatalog`, `StoryProfile`: the character (MakeHuman CC0, built by `tools/avatar/build_avatar.py`), its choices and the saved profile. The imported assets are listed in `docs/pre-existing-work.md`.
- Web profile fields use the physical or system keyboard. The in-game keyboard remains available on Quest.
- Tests: `Assets/HypaStories/Tests/Editor/StoryProfileTests.cs`.
- Not yet synced through Firebase.

## G03: Firebase web bridge (HYP-198)

- Integrated the Firebase browser bridge, browser microphone and Chrome Nano from G03. The localhost bridge returns no App Check token for the emulator. Hosted builds still request a real token.

## G04: Hypa proxy and hosting (HYP-199)

- `functions/`: TypeScript, Node 22, Cloud Functions v2 (`us-central1`). `hypa` function with `/hypa/tts`, `/hypa/asr`, `/hypa/chat`, `/hypa/translate`; Firebase ID token and App Check verification; per-user and global daily quotas in Firestore with Remote Config limits; Hypa error mapping; key via Secret Manager only. `cleanupRooms` scheduled function. Unit tests with Vitest and `firebase-functions-test`.
- `firebase.json`: `functions` block, `/hypa/**` Hosting rewrite, Functions emulator, Hosting emulator on 5002.
- `docs/hypa-proxy.md`: contract, limits, secrets, runbook.

- Follow-up: aligned the four proxy routes with G01, moved the VR chat fallback order into the proxy and supported explicit model selection on all four routes, fixed quota refunds and room expiration, and wired the Unity calls and editor sign-in to the local proxy. The Unity WebGL build uses Brotli to match the Hosting headers.
- TTS now returns a signed URL by default and requests inline audio in WebGL. ASR checks the decoded duration for compressed and uncompressed recordings. Hypa balance errors include a retry delay.
- WebGL TTS, ASR, and story chat resume on Unity's main thread after the proxy responds, so response parsing can continue on a build without managed worker threads.
- Added the Quest App Check HTTPS exchange and tests with simulated Meta responses. Quest client integration and the physical headset check remain pending.

## G08 (HYP-203): progress sync, logic only

Written against G01's interfaces, so it runs on the Fakes. The screens and device checks are not done yet.

- `ProgressSync` (`Assets/HypaStories/Runtime/Core/ProgressSync.cs`): saves the latest position at most every 5 s, at once on close, and retries a failed save. Decides the "Continue *story* where you left off on your phone?" offer: only for a newer position from another device.
- `UserDataSync`: records per-story results (merged) and saved words (one per word and language).
- `PairingCode`: the 6-letter Quest pairing code. The server side (`/pair/claim`) belongs to G04.
- Provisional contract additions: `StoryResult`, `SavedWord`, `IUserDataStore` with a fake (`Assets/HypaCore/Runtime/Firebase/UserData.cs`), waiting for Timi's OK.
- Tests: `Assets/HypaStories/Tests/Editor/ProgressSyncTests.cs`.
- Not done: "Sign in to sync", the Continue prompt on screen, Firestore-backed stores, and the device checks.

## G05 (HYP-200): Hybrid router for the storyteller

Every storyteller answer and Try Hypa translation picks where it runs, says where it ran, and keeps working offline.

- `HybridRouter.Decide` (`Assets/HypaCore/Runtime/Firebase/HybridRouter.cs`): offline → Hypa languages → on device → cloud Gemini, with the thresholds from Remote Config and `HypaSettings.AiMode` (Hybrid by default; Cloud only and On device only for testing, no UI). `NetworkStatus` treats a failed connection as offline for 30 s.
- `HybridStoryQa`, the only answer path: one single-turn prompt with the story trimmed to fit, sentences streamed as they come, fallbacks when a path fails (including an on-device reply in the wrong language), the saved answers offline with "I'll tell you more when we're back online", the 12 s deadline.
- Cloud Gemini calls Hypa's `translate` and `define` through function calling (`CloudTool`) for "what does … mean" questions.
- `HybridTryApi`: Try Hypa's translation through the same router.
- Players never see which model answered; each answer's route is written to the log (`Hypa Stories answered: Cloud · Gemini · 1.2 s (why)`). While the device's model is still downloading, the Q&A says "On-device model downloading (first time only)" in the story's language (`QaLines.Preparing`).
- Anonymous route statistics to Firestore `routes/{uid}/events`, with rules; `RestRouteLog` writes them over REST (in `QuestRestServices`).
- Web (with G03): `WebCloudModel` does function calling (`generateTurn` in the bridge, tool calls run in C# via `CloudToolCalls`), and `WebRouteLog` writes route events through the bridge.
- Integration: `FirebaseStartup` starts the REST services at launch (sign-in, App Check, progress, cloud Gemini, route stats) and fetches Remote Config; `RestCloudModel` is cloud Gemini through Firebase AI Logic over REST, with tool calls. The storyteller's speech (lines and answers) goes through `/hypa/tts` (`HypaProxyClient.SpeechAsync`) instead of MCP.
- Real services instead of Fakes: `NoCloudModel` is the default cloud model (it fails, so the router falls back and no placeholder is ever spoken); `RestRemoteFlags` (Firebase Installations + Remote Config REST) and `RestProfileStore` join `QuestRestServices`; `WebProfileStore` joins the web services. `StoryProfileSync` syncs the character (newest save wins).
- `HypaProxyClient` uses `UnityWebRequest` in a web build (`HttpClient` does not work in the browser), so Hypa answers work on the web.
- Tests: `HybridRouterTests` (every combination of network, language, model status, prompt size, tools, setting and preference), `HybridStoryQaTests`, 2 more rules tests.
- Not yet tested on a device or a web build, nor against real Firebase or Hypa: the on-device and cloud models are G03, G06 and G07's, and the "back online" and "getting ready" lines in Hausa, Igbo, Naija, Kiswahili and Yorùbá need a native speaker's check.

## G07 (HYP-202): iOS on-device model (Apple Foundation Models)

On an iPhone with Apple Intelligence, answers come from Apple's on-device model; on any other iPhone the status is `unavailable` and the router uses the cloud. How it works: `docs/ios-on-device.md`.

- `Assets/Plugins/iOS/HypaFoundationModels.swift`: Apple's Foundation Models framework behind four C functions (`@_cdecl`): status, start (answer or stream), cancel, and the reply callback. No Objective-C shim. Each session starts with family-friendly instructions for Apple's acceptable-use requirements.
- `AppleFoundationModel : IOnDeviceModel` (`Assets/HypaFirebase/Runtime/Native/`): status (`available`, `downloading`, `unavailable` with a reason), answers and streams by request id, errors as `OnDeviceModelException` codes, and cancelling. A model that is not ready fails at once, never waits.
- `HybridServices.OnDeviceFactory`: the platform's on-device model for every service set, so it works before and without the native Firebase services.
- `HypaIosBuild`: weak-links FoundationModels (the app still opens on iOS 15 to 18), pins the Swift version, and makes sure the microphone prompt is in Info.plist.
- Firebase's iOS hybrid API (`HybridModel`, `SystemLanguageModel`, 12.13 to 12.19) was removed in Firebase iOS 13.0.0, and the Firebase Unity SDK 14.2.0 ships 13.2.0. Firebase now points to Apple's Foundation Models framework directly for on-device, which is what this uses.
- Tests: `Assets/HypaFirebase/Tests/Editor/AppleFoundationModelTests.cs`. The Swift plugin type-checks against the iOS 26.2 SDK (Xcode 26.3), and `tools/apple-model/check.sh` ran it on a Mac (macOS 26.3, Apple Intelligence): available, an answer, a Spanish stream in pieces, and a cancel.
- Not yet run on an iPhone, and no iOS build yet (the iOS Build Support module is not installed on the build machine).

## G03 (HYP-198): Firebase web bridge and Gemini Nano in Chrome

- A WebGL bridge to the Firebase JS SDK: `HypaFirebase.jslib`, `firebase-bridge.js` and `WebBridge`.
- Web implementations of the G01 services: `WebAuth`, `WebProgressStore`, `WebRemoteFlags`, `WebCloudModel` and `ChromeNanoModel` (Gemini Nano through Firebase AI Logic).
- A browser microphone at 16 kHz for `MicListener`.
- On the web, the on-device model status is shown on Home, and the help card has a section on on-device AI.
- Details: [web-bridge.md](web-bridge.md).

## G09 (HYP-204): Story Circle, contract and rules first

Work in progress; the room itself is not built yet.

- Handover: when the host goes (leaves, drops, or stops sending a heartbeat for 10 s) the next online player takes the room and the story carries on from the same clock. The ticket's scope said the room closes and its Done-when said the guest becomes host; handover is what was chosen. `docs/contracts.md` section 4 and `firebase/database.rules.json` now say so, and `RoomHandover` picks who takes it.
- `rooms/{code}/answers/{n}`: an answer one sentence at a time, written by the player whose hand is up, so the other player hears it after the first sentence. The ticket left `{n}` open. `RoomAnswer`, `IRoomSync.SendAnswerAsync` and `AnswerReceived`.
- `players/{uid}/lastSeen`: the heartbeat for clients with no `onDisconnect` (REST on Quest and phones). `RoomPresence`.
- `FakeRoomSync` does handover and answers, so the room logic can be built and tested before any real service.
- Tests: 9 more rules tests on the Realtime Database emulator (26 in all); EditMode tests in `HybridRoomsTests`.
- Review fixes: `RoomSeats.MaxPlayers` (2); the contract now says how REST measures server time (no `.info/serverTimeOffset` there), how `n` is picked and retried, that the asker lowers their hand only after the last sentence, that presence never removes the room on disconnect, and that the lower seat wins a tie on the clients, not in the rules. `AGENTS.md` and `Assets/HypaMultiplayer/README.md` no longer say multiplayer is undecided.
- Decided with the ticket's owner: one `RestRoomSync` for Quest, Android and iOS (the Firebase Unity SDK is not in the project, so the ticket's `NativeRoomSync` cannot be built), and `WebRoomSync` for the web.
