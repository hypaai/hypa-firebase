# Work done during the submission period

New work for the Firebase hackathon entry (not part of the imported Hypa Stories foundation).

## G04: Hypa proxy and hosting (HYP-199)

- `functions/`: TypeScript, Node 22, Cloud Functions v2 (`us-central1`). `hypa` function with `/hypa/tts`, `/hypa/asr`, `/hypa/chat`, `/hypa/translate`; Firebase ID token and App Check verification; per-user and global daily quotas in Firestore with Remote Config limits; Hypa error mapping; key via Secret Manager only. `cleanupRooms` scheduled function. Unit tests with Vitest and `firebase-functions-test`.
- `firebase.json`: `functions` block, `/hypa/**` Hosting rewrite, Functions emulator, Hosting emulator on 5002.
- `docs/hypa-proxy.md`: contract, limits, secrets, runbook.
