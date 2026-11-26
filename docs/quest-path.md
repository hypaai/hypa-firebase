# Which Firebase path Quest uses (HYP-197, scope item 6)

Quest has no Google Play services, so the Firebase Unity SDK may not run there. Two paths exist. **The spike decides which one Quest uses.**

| | SDK path (Native) | REST path |
| -- | -- | -- |
| Auth, App Check | Firebase Unity SDK | `RestAuthSession`, `RestAppCheck` (debug provider) |
| Progress | SDK Firestore, with its offline cache | `RestProgressStore`: Firestore REST with its own offline queue |
| Rooms | SDK Realtime Database, `onDisconnect` | `RestRealtimeDatabase`: REST and server-sent events. **No `onDisconnect`**: presence needs a heartbeat that times out on the reading side |
| Live updates to progress | SDK listener | None over REST; poll `LoadAsync` |
| Linking Google or Apple | Android, iOS only | Not available; Quest users stay anonymous |
| Cloud model | AI Logic | Through the proxy (`/gemini/generate`, optional, `docs/contracts.md` section 6) |

**Decision: not made yet.** Fill this in after the spike: `Quest uses: SDK | REST` (who, when, on which Quest build).

## A. Test the REST path (no SDK needed)

With the config file below in place, the app itself also starts the REST services at launch (`FirebaseStartup`): sign-in, App Check, progress, cloud Gemini through Firebase AI Logic (`RestCloudModel`) and route stats. Without the file it plays on local data. The editor does the same, with the file in the editor's data folder.

1. Get the Firebase web API key, project id, app id and database URL from the Firebase console (project settings), and register a **debug token** for App Check (App Check, Apps, Manage debug tokens).
2. Create `hypa.firebase.local.json` and push it to the app's data folder. This file is never committed.

   ```json
   {
     "apiKey": "…",
     "projectId": "…",
     "appId": "1:…:android:…",
     "appCheckDebugToken": "…",
     "databaseUrl": "https://<project>-default-rtdb.firebaseio.com"
   }
   ```

   ```
   adb push hypa.firebase.local.json /sdcard/Android/data/<package id>/files/hypa.firebase.local.json
   ```

3. Put `QuestRestSpike` (`Assets/HypaFirebase/Runtime/Rest`) on an object in a scene and run the build on the Quest.
4. Read the log (`adb logcat -s Unity`). The report looks like:

   ```
   QUEST REST SPIKE
   [PASS] Anonymous sign-in: signed in as abc123...
   [PASS] ID token: received, 912 characters
   [PASS] App Check token: received
   [PASS] Progress save and load (Firestore): saved and read back from the server
   [PASS] Realtime Database write and read: wrote a room and read it back
   [PASS] Realtime Database event stream: first event: put
   [PASS] Clean up the test room: removed
   RESULT: the REST path works on this device.
   ```

   A failed step names the error code Firebase sent (for example `403 PERMISSION_DENIED`) or the kind of failure. The report never contains a token.

The spike writes only to its own room (a new 5-letter code that it hosts), and the signed-in user's own progress document, and removes the room afterwards. It also exercises the thread-safe `PlayerPrefs` store on the device.

## B. Test the SDK path (needs the SDK imported, G02)

On the Quest, in a build with the Firebase Unity SDK:

1. `FirebaseApp.CheckAndFixDependenciesAsync()`: record the status it returns.
2. Anonymous sign-in with Firebase Auth.
3. One Firestore write and read (`users/{uid}/progress/current`).
4. One Realtime Database write and listener on that room's `host` and `state`.
5. One AI Logic `GenerateContentAsync`.

If any of these fails on Quest, Quest uses the REST path (A) for that service.

## Record the result

Write the outcome of A and B here, with the date, the Quest model and build, and who ran it. Then set **Decision** above.

| Step | REST (A) | SDK (B) |
| -- | -- | -- |
| Dependencies | n/a | |
| Anonymous sign-in | | |
| App Check | | |
| Progress save and load | | |
| Realtime Database | | |
| Cloud model | n/a (proxy) | |
