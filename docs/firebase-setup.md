# Firebase setup

## Prepared locally

`firebase.json` and `.firebaserc` target **hypa-firebase**, using project details from the supplied G00 notes. Cloud state has not been independently checked or changed. Do not use production projects `hypaall-v1` or `hypa-webapp-bbe81` for the entry.

Firestore and Realtime Database rules deny all reads/writes, even for authenticated users. No room/user schema or permissive multiplayer rules are assumed. Auth is only an interface at this stage.

## Next implementation steps

1. Verify team IAM access, Blaze billing/budget and service status in the isolated project.
2. Enable Anonymous, Google and Apple Auth providers as appropriate. Implement anonymous sign-in first behind `IAuthSession`, then account linking. Auth tokens must be refreshed on demand and cleared at sign-out.
3. Integrate Firebase native SDKs for supported native targets; use a JavaScript bridge for WebGL. Check Quest SDK/provider compatibility explicitly. Add SDK licenses/notices when the dependencies are selected.
4. Configure App Check per platform: Play Integrity (Android), App Attest (iOS), reCAPTCHA Enterprise (web), approved debug tokens for Editor/Quest/judges. Verify enforcement before committing any SDK config. Configs and their metadata are currently ignored.
5. Implement the server proxy separately. Verify Firebase ID tokens and App Check tokens server-side, restrict allowed tools, enforce quotas and store Hypa credentials in Secret Manager. Implement authenticated HTTP and live speech/browser transports before enabling remote voice.
6. Put the actual public proxy endpoint in `Assets/HypaFirebase/Resources/hypa_client_config.json`. Do not invent a deployment URL. Authenticated remote features are not ready merely because this field is set.
7. Implement Firebase AI Logic through the supported local-inference platform API and demonstrate cloud fallback. No Firebase AI Logic adapter is bundled yet.

## Registered apps (from supplied notes)

| Platform | Application ID | Firebase app ID |
| --- | --- | --- |
| Web | — | `1:236141821508:web:01ead3f137ae402cba4611` |
| Android phone | `ai.hypa.stories.hybrid` | `1:236141821508:android:9c201d795df6cd3eba4611` |
| Quest | `ai.hypa.stories.hybrid.quest` | `1:236141821508:android:bfc3bd280dfbaa16ba4611` |
| iOS | `ai.hypa.stories.hybrid` | `1:236141821508:ios:a86bc7ae28fca6f2ba4611` |

Keep signing keys private; add SHA-256 fingerprints after creating the release keystore. Never put debug tokens, service accounts or secrets in Git or judge instructions.

## Local emulators

Install Firebase CLI and its supported Java runtime, then run `firebase emulators:start --project demo-hypa-firebase`. The explicit demo ID avoids connecting tests to the real project. Adapters must explicitly connect to emulator endpoints; this scaffold does not automatically connect the Unity app. Hosting emulation requires a successful web build first.

## Publication

A future `firebase hosting:channel:deploy` uploads a preview to the cloud. Obtain explicit user consent before this or any deploy/push. No deployment has been performed. Test the real browser build for HTTPS microphone access, touch input, subtitles, CORS, authentication and local-model availability; check the compressed build stays below the 50 MB goal.
