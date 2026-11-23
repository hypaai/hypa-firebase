# Web bridge and Gemini Nano (G03)

The Firebase Unity SDK does not run in WebGL. The web build uses the Firebase JS SDK (13.4.0, ES modules from gstatic) through a bridge, and implements the G01 interfaces ([contracts](contracts.md)) on top of it.

## How a call travels

```
C# WebBridge.CallAsync / StreamAsync (request id)
  → Assets/Plugins/WebGL/HypaFirebase.jslib
  → Assets/WebGLTemplates/Hypa/firebase-bridge.js  (window.hypaFirebase, imported by index.html)
  → SendMessage("HypaWebBridge", "OnMessage", { id, kind: "ok" | "err" | "chunk", ... })
  → the C# task completes, or the stream yields a chunk
```

Events travel the same way with `id: 0`: `uid` when the signed-in user changes, and `progress` when another device saves newer progress. Cancelling a C# call, or stopping a stream early, tells JS to stop and drops any late answer. WebGL has one thread: await bridged tasks, never `.Result` or `.Wait()`.

| Interface | Web implementation | Notes |
| --- | --- | --- |
| `IHypaAuth` | `WebAuth` | Anonymous on first launch; the user is restored from IndexedDB on later visits. Google and Apple link with a popup, so call them from a button press. Linking an account that already belongs to another player fails with `already_linked` and never swaps the player. |
| `IProgressStore` | `WebProgressStore` | Firestore with the persistent (IndexedDB) cache. `SaveAsync` completes once the write is in the local cache, offline too. Before the first sign-in (first launch offline) the newest save is held and written once sign-in succeeds. `ChangedElsewhere` fires only for a server save newer than anything this page loaded or saved. |
| `IRemoteFlags` | `WebRemoteFlags` | Fetched values win, else the contract default, else the fallback. A failed fetch keeps the last values. |
| `ICloudModel` | `WebCloudModel` | Firebase AI Logic, Gemini Developer API backend, model from `cloud_model`. |
| `IOnDeviceModel` | `ChromeNanoModel` | See below. |
| `IRoomSync` | Fake | G09 defines the interface first. |

App Check uses reCAPTCHA Enterprise. On `localhost` it uses the debug provider: register the debug token Chrome prints to the console in the Firebase console. Tokens are never logged; bridge errors are logged by code only.

## Configuration

`firebaseWeb` in `Assets/HypaFirebase/Resources/hypa_client_config.json` holds the web app's Firebase config (Firebase console → Project settings → Your apps → Web app) and `recaptchaEnterpriseSiteKey`. These values are public, not secrets. While `apiKey`, `projectId` or `appId` is blank, `HybridServices.Current` stays on the Fakes, so the web build still plays.

## Run locally with the Firebase emulators

On `localhost` or `127.0.0.1` the web build uses the Firebase Emulator Suite, project `demo-hypa` (the same one the rules tests use). A `demo-` project never reaches real Firebase and needs no login or console config. Java 21 or newer is required.

1. Build: close Unity, then `tools/build.sh web`.
2. From the repository root: `npx firebase-tools emulators:start --project demo-hypa`.
3. Open `http://localhost:5000` in Chrome. The hosting emulator serves `Builds/Web` with the `.br` headers from `firebase.json`.
4. Watch the data in the Emulator UI at `http://localhost:4000`: anonymous users under Authentication, and `users/{uid}/progress/current` under Firestore. `firebase/firestore.rules` is enforced.

| Service | With the emulators |
| --- | --- |
| Auth, Firestore | Emulated (ports 9099 and 8080) |
| App Check | Off: the emulators do not check it |
| Remote Config | No emulator, so the contract defaults are used |
| Cloud Gemini | No emulator, so calls fail. Use `?firebase=live` |
| Gemini Nano | Works: it runs on the device |

`http://localhost:5000/?firebase=live` uses the real project from `firebaseWeb` instead, with the App Check debug provider.

## Gemini Nano

`ChromeNanoModel` uses Firebase AI Logic with `InferenceMode.ONLY_ON_DEVICE`, so every answer it gives came from the device. The bridge also checks `inferenceSource` on every response and chunk. When the model is not `available`, a call fails at once with `unavailable` instead of waiting for a download. G05's router then uses `ICloudModel`, and the route chip stays honest.

`Status` follows Chrome's `LanguageModel.availability()` for text in English, Spanish and French: `available`, `downloadable`, `downloading` or `unavailable`. On the web, the Home card shows it as a button, and pressing it starts the one-time download (`DownloadAsync`, which needs a user gesture) with a percentage. Safari, Firefox and mobile Chrome have no Prompt API, so they report `unavailable` and use the cloud.

### Chrome requirements (judges)

- Desktop Chrome 139 or newer on Windows 10/11, macOS 13+, Linux or a Chromebook Plus. About 22 GB free on the Chrome profile's disk, and more than 4 GB of VRAM (or 16 GB of RAM and 4 or more cores).
- In `chrome://flags/#prompt-api-for-gemini-nano-multimodal-input`, select **Enabled**, then restart Chrome.
- Open the game, select **Get on-device AI** on Home, and wait for the download. It only happens once, and it can take minutes.

Chrome's own Prompt API page lists a later version for web pages than Firebase's 139. If Nano stays `unavailable` on 139, update Chrome.

## Microphone

`WebMicrophone` (Assets/Plugins/WebGL/HypaMicrophone.jslib) replaces Unity's `Microphone`, which does not work in a browser. It uses `getUserMedia` with echo cancellation, noise suppression and auto gain on, and an AudioWorklet that averages the device rate down to 16 kHz mono. `MicListener` reads it every frame, as it reads a microphone clip on other platforms. A refused microphone ends the listen with "Allow the microphone in your browser". Recording needs HTTPS (or localhost).

## Not done here

- No origin-trial token: basic prompting with Gemini Nano does not need one.
- The firebase config values and the reCAPTCHA Enterprise key must come from the Firebase console.
- Hosting the build, and the end-to-end checks in the ticket (sign-in, progress write, App Check accepted, Nano offline, cloud in Safari), still need a real web build.
