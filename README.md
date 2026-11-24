# Hypa Firebase — Hypa Stories

A Unity storytelling project being prepared for #BuildHybridAI 2026. This repository starts from Hypa Stories for Quest, with imported flat-platform tooling for Android phones, WebGL and iOS. Firebase Auth, App Check and hybrid inference are planned integrations; multiplayer requirements are still undecided.

## Open locally

1. Open this folder in Unity **6000.6.8f1** (the version in `ProjectSettings/ProjectVersion.txt`). Let Unity restore `Packages/manifest.json`.
2. Run `bash tools/setup-git.sh` to enable Git LFS, merge configuration and secret-scan hooks. Install `gitleaks` for a full local secret scan; the bundled fallback is only a preliminary check.
3. Open `Assets/HypaCore/Scenes/HypaShell.unity` and enter Play Mode. Bundled stories do not require Firebase or a Hypa key.
4. Read [Firebase setup](docs/firebase-setup.md) before connecting remote services. The public proxy URL starts blank, so remote translation/voice features are unavailable until configured.

No Hypa API key belongs in the client. Proxy configuration lives in `Assets/HypaFirebase/Resources/hypa_client_config.json`; `.env.example` provides Editor-only overrides for the public URL and debug setting.

## Layout

| Path | Responsibility |
| --- | --- |
| `Assets/HypaCore` | Shared API, interaction, shell and platform build tools |
| `Assets/HypaStories` | Stories, campfire, village and local gameplay |
| `Assets/HypaLens` | Existing Quest camera experience; hidden on flat builds |
| `Assets/HypaFirebase` | Auth boundary and future Firebase SDK/platform adapters |
| `Assets/HypaMultiplayer` | Reserved module; requirements and transport undecided |
| `functions/` | Authenticated Hypa proxy and scheduled cleanup (Cloud Functions) |
| `firebase/` | Firestore/Realtime Database rules and indexes |
| `tools/` | Local builds and repository checks |

Unity stays at the root so existing scene paths and asset GUIDs remain intact. The Hypa proxy lives in `functions/` (Cloud Functions v2, Node 22); see [Hypa proxy](docs/hypa-proxy.md).

## Platform builds

Install the matching Unity Hub build modules, close the editor for this project, then run:

```sh
tools/build.sh web      # Builds/Web
tools/build.sh android  # Builds/Android/HypaStories.apk
tools/build.sh quest    # Builds/Quest/HypaStories.apk
tools/build.sh ios      # Builds/IOS (Xcode project; requires iOS Build Support)
```

The same targets appear under **Hypa > Build**. Flat targets use `HYPA_FLAT`, exclude Quest assemblies/Meta components and add touch/mouse controls. Android/iOS use `ai.hypa.stories.hybrid`; Quest uses `ai.hypa.stories.hybrid.quest`. Web uses the Hypa template and Brotli compression. The imported tooling is not yet proof of a successful platform build; see [initialization status](docs/initialization-status.md).

## Firebase and hybrid inference

The intended flow is local inference through the supported Firebase AI Logic platform API, with cloud fallback and Remote Config routing. This is not implemented yet. Auth identifies users, App Check attests clients, and a server proxy owns the Hypa credential. WebGL requires browser bridges for supported Firebase APIs and browser-compatible networking. A Quest-only submission does not satisfy the listed phone/web platform requirement.

Firebase CLI config targets the isolated `hypa-firebase` project from the supplied notes. No Firebase login, cloud change, deploy or hosting preview was performed. Database rules deny all access. See [setup and remaining work](docs/firebase-setup.md).

## Validation

Use Unity Test Runner for EditMode tests, or:

```sh
Unity -batchmode -nographics -projectPath . -runTests -testPlatform EditMode -testResults Logs/editmode-results.xml -logFile Logs/editmode.log
bash tools/scan-secrets.sh
```

PR workflows are prepared for GameCI EditMode tests, WebGL build and gitleaks. Configure Unity activation secrets before relying on CI. Organization use of the gitleaks action may require `GITLEAKS_LICENSE`. CI has not run remotely.

## What we built during the submission period
--- 

## Pre-existing code disclosure

The existing foundation includes story playback, subtitles and translations, the campfire and village environments, Quest interaction, Lens, gameplay tests, and flat-platform build tooling. These features were already present before this entry’s initialization.

The [official rules](https://firebasehybridaihackathon.devpost.com/rules)

## License and credits

Apache 2.0; see [LICENSE](LICENSE). Third-party assets retain their licenses and attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), including the required CC BY 4.0 campfire credit. Restricted Epidemic Sound media and the Ready Player Me avatar are not included.
