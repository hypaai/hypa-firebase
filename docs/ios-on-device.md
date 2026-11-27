# iOS on-device model (G07)

On an iPhone with Apple Intelligence, `AppleFoundationModel` answers on the device with Apple's Foundation Models framework (iOS 26). Every other iPhone reports `unavailable`, and G05's router uses `ICloudModel` instead. The app is the same build either way.

## Why Apple's framework directly, and not Firebase's hybrid API

- Firebase's [iOS hybrid guide](https://firebase.google.com/docs/ai-logic/hybrid/ios/get-started) (Experimental) uses `FirebaseAI.SystemLanguageModel`, `HybridModel` and `generativeModelSession`. Those came in Firebase iOS 12.13 and were **removed in 13.0.0** (FirebaseAI changelog: "Removed `GenerativeModelSession`, `SystemLanguageModel`, `HybridModel` … Going forward, use `GeminiLanguageModel` via `ai.geminiLanguageModel(name:)` directly with Apple's native Foundation Models framework").
- The Firebase Unity SDK we have to match (14.2.0) ships Firebase iOS 13.2.0, so that API is not there. Pinning Firebase iOS 12 for it would break the rule that the iOS packages match the Unity SDK's.
- Firebase's current path is Apple's own framework: `SystemLanguageModel.default` on the device, and Gemini as another model for Apple's `LanguageModelSession` (`FirebaseAILogic`, `geminiLanguageModel`). The removed API's "only on device" mode wrapped the same `SystemLanguageModel`, so it behaved like this code.
- Our router already chooses between device and cloud (G05), and `ICloudModel` is the cloud path (G02). So the on-device half needs only Apple's framework, with no Firebase package to add or keep in step.
- Answers from `AppleFoundationModel` never leave the device, so the route chip "On device" is always true. (The removed API named this model `apple-foundation-models-system-language-model` in `rawResponse.modelVersion`.)

## How a call travels

```
C# AppleFoundationModel (request id)
  → HypaFM_Start (Assets/Plugins/iOS/HypaFoundationModels.swift, @_cdecl, compiled into UnityFramework)
  → LanguageModelSession.respond / streamResponse, on the device
  → the callback C# registered (main thread, which is Unity's thread on iOS): chunk, done or error
  → the C# task completes, or the stream yields a chunk
```

No Objective-C shim and no `UnitySendMessage`: Swift exports C functions directly (`@_cdecl`), and replies come back through a function pointer that C# registers. Each request is a new session (no memory between prompts). A stream sends only the new text in each chunk. Cancelling a call, or stopping a stream early, cancels the Swift task, and late replies are dropped.

| `HypaFM_Status` | Meaning | `StatusAsync` |
| -- | -- | -- |
| 0 | Ready | `available` |
| 1 | Apple is still downloading the model | `downloading` |
| 2 | The device cannot run Apple Intelligence | `unavailable` |
| 3 | Apple Intelligence is turned off in Settings | `unavailable` |
| 4 | iOS older than 26 | `unavailable` |

`AppleFoundationModel.UnavailableReason` gives a line for the help card for 2, 3 and 4. Apple downloads the model itself, so there is no download button on iOS.

Errors (`OnDeviceModelException.Reason`, the same exception as Android's), and what the router should do with each, which is to use the cloud for all of them:

| Code | When |
| -- | -- |
| `unavailable` | The model is not ready. The call fails at once instead of waiting. |
| `too_long` | The prompt and answer exceed Apple's context window (4,096 tokens). Keep `route_max_on_device_tokens` below it on iOS. |
| `guardrail` | Apple's safety guardrails refused the prompt or the answer. |
| `unsupported_language` | The language is not supported on the device. |
| `busy`, `failed`, `cancelled` | Rate-limited, another error, or the Swift task was cancelled. |

## Wiring

`HybridServices.OnDeviceFactory` (new in G07) gives every service set the platform's on-device model, the Fakes included. Apple's model needs no Firebase, so it works before G02's native services start and if they never do. On iOS players, `AppleFoundationModel` registers itself at start-up. G06 can register `AndroidNanoModel` the same way.

## Build

`tools/build.sh ios` (iOS Build Support module, Xcode 26 or newer, because the iOS 26 SDK has FoundationModels). `HypaIosBuild` (Assets/HypaCore/Editor) then:

- weak-links `FoundationModels.framework`, so the app still opens on iOS 15 to 18 (status 4);
- pins `SWIFT_VERSION` to 5.0 for the Swift plugin;
- makes sure `NSMicrophoneUsageDescription` is in Info.plist.

The only manual step is signing: pick the Apple Developer team in Xcode (Signing & Capabilities), then run on a device.

## Apple's acceptable-use requirements

Each session starts with short instructions: a family-friendly storyteller, short answers, and no medical, legal or financial advice. Apple's guardrails run on top of that.

## Testing on devices

0. On a Mac first (no iPhone, no Unity): `tools/apple-model/check.sh` runs the Swift plugin on macOS 26 with Apple Intelligence on, which uses the same on-device model. It prints the status, one answer, one stream in pieces, and one cancelled request.
1. iPhone 15 Pro or newer, iOS 26, Apple Intelligence on, and the model downloaded (Settings → Apple Intelligence & Siri). Turn on **airplane mode**, ask an English question, and the answer arrives with the chip "On device".
2. An older iPhone, or Apple Intelligence off: the status is `unavailable` and the answer comes from the cloud.
3. EditMode tests: `Assets/HypaFirebase/Tests/Editor/AppleFoundationModelTests.cs` (status, answers, errors, streaming, cancelling, the factory).
