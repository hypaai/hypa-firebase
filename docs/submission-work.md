# Submission work

New work done for the Firebase hackathon. Imported work goes in `docs/pre-existing-work.md`.

## G01 (HYP-196): Day 0 contracts

The shared interfaces, data shapes and wire formats, so G02–G12 can build in parallel. The contract is `docs/contracts.md`.

- Interfaces in `Assets/HypaCore/Runtime/Firebase/` (namespace `HypaCore.Hybrid`): `IHypaAuth`, `IProgressStore`, `IRemoteFlags`, `IRoomSync` (empty until G09), `IOnDeviceModel`, `ICloudModel`.
- Types: `Route` / `RouteDecision`, `Progress`, `RemoteConfigKeys` with defaults, `ProxyApi` and the proxy request and response classes.
- Fakes for every interface, and `HybridServices.Current` (WebGL: Web, builds: Native, editor: Fake unless "Use real Firebase" is on).
- `firebase/firestore.rules` (each user only their own `users/{uid}/**`) and `firebase/database.rules.json` (rooms: the host controls the room and clock, each player writes only their own entries), with emulator tests in `firebase/tests/`.
- Tests: EditMode tests in `Assets/HypaCore/Tests/Editor/HybridContractsTests.cs`, 9 rules tests on the Firebase emulators.
- Merged without the four reviews the ticket asks for.

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
