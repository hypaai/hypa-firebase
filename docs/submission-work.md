# Submission work

New work done for the Firebase hackathon. Imported work goes in `docs/pre-existing-work.md`.

## G01 (HYP-196): Day 0 contracts

The shared interfaces, data shapes and wire formats, so G02–G12 can build in parallel. The contract is `docs/contracts.md`.

- Interfaces in `Assets/HypaCore/Runtime/Firebase/` (namespace `HypaCore.Hybrid`): `IHypaAuth`, `IProgressStore`, `IRemoteFlags`, `IRoomSync` (empty until G09), `IOnDeviceModel`, `ICloudModel`.
- Types: `Route` / `RouteDecision`, `Progress`, `RemoteConfigKeys` with defaults, `ProxyApi` and the proxy request and response classes.
- The proxy wire format now matches the Cloud Function and the Quest app: `audio` / `audioUrl`, `targetLang`, `messages` plus `maxTokens`, `route`, `usage`, and the error code `app_check`. Daily audio limit default is 600 seconds.
- Fakes for every interface, and `HybridServices.Current` (WebGL: Web, builds: Native, editor: Fake unless "Use real Firebase" is on).
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
- Tests: `Assets/HypaStories/Tests/Editor/StoryProfileTests.cs`.
- Not yet synced through Firebase.

## G08 (HYP-203): progress sync, logic only

Written against G01's interfaces, so it runs on the Fakes. The screens and device checks are not done yet.

- `ProgressSync` (`Assets/HypaStories/Runtime/Core/ProgressSync.cs`): saves the latest position at most every 5 s, at once on close, and retries a failed save. Decides the "Continue *story* where you left off on your phone?" offer: only for a newer position from another device.
- `UserDataSync`: records per-story results (merged) and saved words (one per word and language).
- `PairingCode`: the 6-letter Quest pairing code. The server side (`/pair/claim`) belongs to G04.
- Provisional contract additions: `StoryResult`, `SavedWord`, `IUserDataStore` with a fake (`Assets/HypaCore/Runtime/Firebase/UserData.cs`), waiting for Timi's OK.
- Tests: `Assets/HypaStories/Tests/Editor/ProgressSyncTests.cs`.
- Not done: "Sign in to sync", the Continue prompt on screen, Firestore-backed stores, and the device checks.

## G04: Hypa proxy and hosting (HYP-199)

- `functions/`: TypeScript, Node 22, Cloud Functions v2 (`us-central1`). `hypa` function with `/hypa/tts`, `/hypa/asr`, `/hypa/chat`, `/hypa/translate`; Firebase ID token and App Check verification; per-user and global daily quotas in Firestore with Remote Config limits; Hypa error mapping; key via Secret Manager only. `cleanupRooms` scheduled function. Unit tests with Vitest and `firebase-functions-test`.
- `firebase.json`: `functions` block, `/hypa/**` Hosting rewrite, Functions emulator, Hosting emulator on 5002.
- `docs/hypa-proxy.md`: contract, limits, secrets, runbook.
