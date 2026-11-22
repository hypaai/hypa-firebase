# Firebase integration

This module owns Firebase SDK adapters and platform bridges. `IAuthSession` is the initial boundary; no adapter is registered and no sign-in runs yet.

Implement native Unity Firebase Auth and App Check adapters for Android/iOS/Quest, plus a JavaScript bridge for WebGL. Do not assume the native Unity SDK runs in a browser. Keep Firebase dependencies here rather than in Stories or Lens. Expose identity through the boundary when wiring the application bootstrap.

`Resources/hypa_client_config.json` contains public client settings. Set `proxyBaseUrl` only after the authenticated proxy exists. Blank means remote Hypa features are unavailable; bundled stories remain local. The client has no Hypa API key path. The existing HTTP/WebSocket transports still need Firebase ID token and App Check attachment, browser-compatible networking, and a server that validates both before remote features are ready.

See `docs/firebase-setup.md` for configuration and pending console work.
