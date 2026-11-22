# Work in this entry during the submission period

Submission period: November 11–30, 2026. This is a factual implementation log, not a claim that the entry is complete.

## November 22 — local initialization

- Created a fresh local Unity repository from a documented baseline, retaining asset GUIDs and license credits.
- Imported existing flat-platform tooling; adjusted application IDs, web template and build scene selection.
- Removed client Hypa credential loading, API-key headers, WebSocket key subprotocol and baked resource support. Remote requests now require an explicitly configured proxy URL; token/App Check authorization remains to implement.
- Prepared isolated Firebase CLI config, Hosting Brotli headers, emulators and deny-all database rules.
- Added an Auth interface with no Firebase SDK implementation, and reserved a multiplayer module with no selected behavior/transport.
- Prepared PR validation workflows and documentation.

## Still to implement and demonstrate

- Firebase Auth providers and native/browser adapters, App Check integration and enforcement.
- Authenticated server proxy with server-only secrets, quotas and request validation.
- Firebase AI Logic local inference and cloud fallback on a supported platform; Remote Config routing.
- Multiplayer requirements, implementation and tested rules if multiplayer is included.
- Verified platform builds, browser story/subtitle smoke test, compressed web-size check and HTTPS preview.
- Judge access instructions, demonstration video and final feature/change disclosure.
