# Story Circle: running it on devices (G09, HYP-204)

Two players, on any mix of web, phone and Quest, at one fire: the same story line at the same moment, each other's figure, and
either one can raise a hand to ask. The contract is `docs/contracts.md` section 4.

## What is where

| Piece | File |
| -- | -- |
| Contract types, handover, links | `Assets/HypaCore/Runtime/Firebase/Rooms.cs`, `Interfaces.cs` (`IRoomSync`) |
| Editor and tests | `FakeRoomSync` (`Fakes.cs`) |
| Quest, Android, iOS | `RestRoomSync` (`Assets/HypaFirebase/Runtime/Rest/`), registered in `QuestRestServices` when the config has `databaseUrl` |
| Web | `WebRoomSync` (`Assets/HypaCore/Runtime/Firebase/Web/`) + `room*` in `Assets/WebGLTemplates/Hypa/firebase-bridge.js`, when the web config has `databaseURL` |
| Clock, questions, handover in the story | `CircleSession` (`Assets/HypaStories/Runtime/Circle/`) |
| The other player's figure | `CirclePeer` + `PoseTrack` |
| Lobby and code pad | `CircleView`; wired in `StoryApp` ("Story Circle" on Home and at the campfire, C in the editor) |
| Rules | `firebase/database.rules.json`, tests in `firebase/tests/rules.test.mjs` |

Without a Realtime Database URL in the config, every platform plays on `FakeRoomSync`: the lobby works, but only on that one device.

## How a session goes

1. Player A: Home → **Story Circle** (or **Story Circle** at the campfire) → **Start a circle** → picks a story. The room is made with that
   story and language; a card shows the code and the link `https://hypa-firebase.web.app/circle/ABCDE` (the page's own site on the web).
   A starts telling at once.
2. Player B: **Story Circle** → **Join with a code** → types the 5 letters on the pad → **Join**, or opens the link in Chrome. B's app
   loads the same story in the host's language and starts at the line A is on.
3. Both hear each line together: the host announces each line 0.5 s ahead in server time and both start at that moment.
4. Either raises a hand (Quest) or taps **Ask** (Q / H in the editor). The lower seat wins if both do at once. The storyteller turns to
   the asker, both stop, the asker's device answers, the other device speaks each sentence as it comes, then "Now, where was I…" and the
   story carries on together.
5. Leaving: the story's back button. If the host leaves or drops for more than 10 s, the other player takes the room and carries on.

Only the host's pause, previous and next buttons work; the guest follows.

## 1. Two players on one Mac, no cloud (the emulators)

The web build on localhost uses the Firebase emulators by itself (add `?firebase=live` for the real project).

```sh
tools/build.sh web                                  # Builds/Web
npx --prefix firebase/tests firebase emulators:start --only auth,database,firestore,hosting --project demo-hypa
```

Open `http://localhost:5002` in a normal Chrome window and in an incognito window (two accounts). Create in one, join in the other with
the code or with `http://localhost:5002/circle/ABCDE`. The emulator UI at `http://localhost:4000` shows `rooms/…` live. Speech and
answers need the Hypa proxy (`functions/`, see `docs/hypa-proxy.md`); without it lines play silently but stay in step.

### Real phones on the same Wi-Fi, no cloud

For phones, tablets and the Quest browser on the same Wi-Fi (or hotspot) as the Mac, before the real project is set up:

```sh
HYPA_WEB_LAN=1 tools/build.sh web       # decompression in the page: phones get Brotli only over https
npx --prefix firebase/tests firebase emulators:start --config firebase.lan.json --only auth,database,firestore,hosting --project demo-hypa
ipconfig getifaddr en0                  # the Mac's Wi-Fi address, e.g. 172.20.10.6
```

`firebase.lan.json` is `firebase.json` with the emulators listening on the network (`0.0.0.0`). On each device open
`http://<the Mac's address>:5002` in Chrome (the Mac itself can use the same address or localhost). A page on a private address uses
the emulators by itself, like localhost. The Mac's firewall may ask to allow incoming connections for Java and Node: allow them.
Limits: microphone questions need https (pick a suggested question instead); speech needs the Functions emulator and a dev
Hypa key; the Android and Quest apps (not their browsers) can't reach the emulators, they need the real project.

## 2. Real devices (the `hypa-firebase` project)

These change the Firebase project and need the owner's go-ahead:

1. **Create the Realtime Database** (Firebase console → Build → Realtime Database → Create, `us-central1`, locked mode). Copy its URL,
   for example `https://hypa-firebase-default-rtdb.firebaseio.com`.
2. **Deploy the rules:** `npx --prefix firebase/tests firebase deploy --only database --project hypa-firebase`.
3. **Web:** put the URL in `firebaseWeb.databaseURL` in `Assets/HypaFirebase/Resources/hypa_client_config_local.json` (git-ignored) and
   build; serve it from Hosting (`firebase hosting:channel:deploy circle` gives a preview URL; the `/circle/**` rewrite is in
   `firebase.json`). A share link from a preview channel uses that channel's site.
4. **Quest, Android, iOS:** add `"databaseUrl": "<the URL>"` to `hypa.firebase.local.json` (`docs/quest-path.md`) and copy it to the app's
   data folder. Register each device's App Check debug token in the console.

| Platform | Build | Config file |
| -- | -- | -- |
| Web (Chrome) | `tools/build.sh web`, then Hosting | in the build |
| Android phone | `tools/build.sh android`; `adb install -r Builds/Android/HypaStories.apk` | `adb push hypa.firebase.local.json /sdcard/Android/data/ai.hypa.stories.hybrid/files/` |
| Quest | `tools/build.sh quest`; `adb install -r Builds/Quest/HypaStories.apk` | `adb push hypa.firebase.local.json /sdcard/Android/data/ai.hypa.stories.hybrid.quest/files/` |
| iOS | install iOS Build Support in Unity Hub; `tools/build.sh ios`; run from Xcode | the app's Documents folder (Xcode → Devices → the app → container) |

Logs: Chrome DevTools console; `adb logcat -s Unity` for Android and Quest; the Xcode console for iOS. Filter on `Story Circle` and
`Hypa rooms`.

## 3. The Done-when checks

| Check | How | Pass |
| -- | -- | -- |
| Same line within ~300 ms (web + Android) | Both log `Story Circle: line N starts at server T (host/guest, X ms late)`. For the same N, compare T on the two devices. | The difference is under 300 ms for most lines. A line whose audio was not ready shows a large "late"; check it in Yorùbá (slow voices) too. |
| Both see each other | Each sees the other's coloured figure and name across the fire; turning your head (Quest) or the camera (phone, web) turns that figure's head smoothly. | Yes on both. |
| A question from either is heard by both | Ask on the phone, then on the web. | Both hear the answer and "Now, where was I…", and both carry on at the interrupted line. |
| Quest + web | The same three checks with the Quest (raise a hand to ask; the hands show on the web). | Yes. |
| Host leaves → guest becomes host | Host taps back: the guest carries on at once. Then again with the host's Wi-Fi off: the guest carries on within about 10 s. | The story continues on the guest; its log shows `host` from then on. |
| Late join | Start the story, then join. | The guest starts at the host's current line. |
| Dropped player | Turn the guest's Wi-Fi off for 15 s, then on. | The host carries on; the guest's figure greys "(away)" on the host, and the guest picks up at the current line. |
| Costs | Firebase console → Realtime Database → Usage during a 10-minute session. | Small, and zero once both leave. |

## The game race (G11)

After a story with a game (Seven Hot Stones, Eshu at the Crossroads, The King's Magic Drum), the host gets **Play together** where
the game button would be. Both devices show 3-2-1 from the same server time, then each plays the game on its own. A bar above the
game shows "Ada 12 · You 9"; when both are done a card shows "Ada wins: 41 s" with **Rematch** (either player) and **Back to the
fire**. Points come from each game's own rules; equal points go to the faster game. Going back to the fire mid-game, or leaving the
circle, gives the race to the other player once they finish. Logs: `Hypa Stories: race over, N points (best B, P plays)`.

Checks (Done when): Web + Android race one game end to end with the result card on both; then Quest + web. Also try a rematch
started by the guest, and the guest leaving mid-game (the host still gets a result after finishing).

## Known limits

- The host starts telling as soon as the room is made; a guest who joins later starts at the current line (no waiting room yet).
- Android and iOS use REST (no Firebase Unity SDK in the project): presence is a 3-second heartbeat, so a dropped player shows as
  away after about 10 s rather than at once, and poses are separate HTTP writes (10 a second at most).
- Each device speaks lines and answers itself; a slow voice on one device makes that device late for that line, never the other.
- One story language per room (the host's).
