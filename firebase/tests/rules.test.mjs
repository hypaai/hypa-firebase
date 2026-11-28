import { readFileSync } from "node:fs";
import { after, before, beforeEach, describe, it } from "node:test";
import { assertFails, assertSucceeds, initializeTestEnvironment } from "@firebase/rules-unit-testing";
import { doc, getDoc, setDoc } from "firebase/firestore";
import { get, ref, set } from "firebase/database";

const read = (name) => readFileSync(new URL(`../${name}`, import.meta.url), "utf8");

let env;

before(async () => {
  env = await initializeTestEnvironment({
    projectId: "demo-hypa",
    firestore: { rules: read("firestore.rules") },
    database: { rules: read("database.rules.json") },
  });
});

after(async () => { await env.cleanup(); });

beforeEach(async () => {
  await env.clearFirestore();
  await env.clearDatabase();
});

describe("firestore.rules", () => {
  const progress = { storyId: "s1", lineId: "l1", lineIndex: 0, language: "yo", place: "camp", updatedAt: 1, device: "quest" };

  it("lets a user read and write their own tree", async () => {
    const db = env.authenticatedContext("alice").firestore();
    await assertSucceeds(setDoc(doc(db, "users/alice"), { language: "yo" }));
    await assertSucceeds(setDoc(doc(db, "users/alice/progress/current"), progress));
    await assertSucceeds(setDoc(doc(db, "users/alice/stories/s1"), { finished: false }));
    await assertSucceeds(setDoc(doc(db, "users/alice/words/w1"), { text: "omi" }));
    await assertSucceeds(getDoc(doc(db, "users/alice/progress/current")));
  });

  it("keeps one user out of another's tree", async () => {
    await env.withSecurityRulesDisabled(async (ctx) => {
      await setDoc(doc(ctx.firestore(), "users/alice/progress/current"), progress);
    });
    const bob = env.authenticatedContext("bob").firestore();
    await assertFails(getDoc(doc(bob, "users/alice/progress/current")));
    await assertFails(setDoc(doc(bob, "users/alice/progress/current"), progress));
    await assertFails(setDoc(doc(bob, "users/alice"), { language: "en" }));
  });

  it("refuses everyone who is not signed in", async () => {
    const anon = env.unauthenticatedContext().firestore();
    await assertFails(getDoc(doc(anon, "users/alice/progress/current")));
    await assertFails(setDoc(doc(anon, "users/alice"), { language: "en" }));
  });

  it("keeps every other path closed, even for a signed-in user", async () => {
    const db = env.authenticatedContext("alice").firestore();
    await assertFails(setDoc(doc(db, "config/app"), { x: 1 }));
    await assertFails(getDoc(doc(db, "rooms/BCDFG")));
    await assertFails(setDoc(doc(db, "users/alice/progress/other"), progress));
    await assertFails(setDoc(doc(db, "users/alice/secrets/x"), { x: 1 }));
  });

  it("accepts the profile with the character's appearance, and checks its fields", async () => {
    const db = env.authenticatedContext("alice").firestore();
    const profile = {
      username: "timi_01", displayName: "Timi", language: "yo", appearance: '{"skin":4}', appearanceVersion: 2,
      createdAt: 1, lastDevice: "quest", lastSeenAt: 2,
    };
    await assertSucceeds(setDoc(doc(db, "users/alice"), profile));
    await assertFails(setDoc(doc(db, "users/alice"), { ...profile, username: "Timi Space" }));
    await assertFails(setDoc(doc(db, "users/alice"), { ...profile, username: "9lives" }));
    await assertFails(setDoc(doc(db, "users/alice"), { ...profile, displayName: "x".repeat(25) }));
    await assertFails(setDoc(doc(db, "users/alice"), { ...profile, isAdmin: true }));
    await assertFails(setDoc(doc(db, "users/alice"), { ...profile, appearance: "x".repeat(4001) }));
  });

  it("checks progress, story and word fields", async () => {
    const db = env.authenticatedContext("alice").firestore();
    await assertFails(setDoc(doc(db, "users/alice/progress/current"), { ...progress, place: "moon" }));
    await assertFails(setDoc(doc(db, "users/alice/progress/current"), { ...progress, lineIndex: "3" }));
    await assertFails(setDoc(doc(db, "users/alice/progress/current"), { ...progress, extra: 1 }));
    await assertSucceeds(setDoc(doc(db, "users/alice/stories/s1"), { finished: true, bestGameScore: 7, gamePlays: 2, lastLineId: "l9", updatedAt: 3 }));
    await assertFails(setDoc(doc(db, "users/alice/stories/s1"), { finished: "yes" }));
    await assertSucceeds(setDoc(doc(db, "users/alice/stories/s1"), { storyId: "s1", finished: true }));
    await assertFails(setDoc(doc(db, "users/alice/stories/s1"), { storyId: "s2", finished: true }));
    await assertSucceeds(setDoc(doc(db, "users/alice/words/w1"), { text: "omi", translation: "water", language: "yo", storyId: "s1", savedAt: 4 }));
    await assertFails(setDoc(doc(db, "users/alice/words/w1"), { text: "x".repeat(201) }));
  });
});

describe("firestore.rules: route statistics", () => {
  const event = { kind: "answer", route: "onDevice", model: "Gemini Nano", language: "en", latencyMs: 420, ok: true, device: "web", at: 1 };

  it("lets a user add their own route events, and nothing more", async () => {
    const db = env.authenticatedContext("alice").firestore();
    await assertSucceeds(setDoc(doc(db, "routes/alice/events/e1"), event));
    await assertSucceeds(setDoc(doc(db, "routes/alice/events/e2"), { ...event, route: "cloud", fallbackFrom: "onDevice" }));
    await assertFails(setDoc(doc(db, "routes/alice/events/e1"), { ...event, ok: false }));
    await assertFails(getDoc(doc(db, "routes/alice/events/e1")));
    await assertFails(setDoc(doc(db, "routes/bob/events/e1"), event));
  });

  it("checks the event's shape: no question or answer text", async () => {
    const db = env.authenticatedContext("alice").firestore();
    await assertFails(setDoc(doc(db, "routes/alice/events/e3"), { ...event, question: "what does ìyá mean?" }));
    await assertFails(setDoc(doc(db, "routes/alice/events/e4"), { ...event, route: "somewhere" }));
    await assertFails(setDoc(doc(db, "routes/alice/events/e5"), { ...event, kind: "chat" }));
    await assertFails(setDoc(doc(env.unauthenticatedContext().firestore(), "routes/alice/events/e6"), event));
  });
});

describe("database.rules.json", () => {
  const code = "BCDFG";
  const room = (host) => ({ host, storyId: "s1", language: "yo", state: "lobby", createdAt: 1 });
  const player = (name, seat) => ({ name, platform: "quest", seat, online: true, handUp: false });
  const pose = { h: [0, 1.6, 0, 0, 0, 0, 1], l: [0.2, 1.2, 0.3, 0, 0, 0, 1], t: 1 };

  async function hostedRoom() {
    const alice = env.authenticatedContext("alice").database();
    await assertSucceeds(set(ref(alice, `rooms/${code}`), room("alice")));
    await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice`), player("Alice", 0)));
    return alice;
  }

  it("keeps a room closed to people who are not signed in", async () => {
    const anon = env.unauthenticatedContext().database();
    await assertFails(get(ref(anon, `rooms/${code}/state`)));
    await assertFails(set(ref(anon, `rooms/${code}`), room("alice")));
  });

  it("lets a signed-in player create a room only as its host, with a valid code", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertFails(set(ref(alice, `rooms/${code}`), room("bob")));
    await assertFails(set(ref(alice, "rooms/ABCDE"), room("alice")));
    await assertFails(set(ref(alice, "rooms/bcdfg"), room("alice")));
    await assertFails(set(ref(alice, `rooms/${code}`), { host: "alice" }));
    await assertFails(set(ref(alice, `rooms/${code}`), { ...room("alice"), state: "party" }));
    await assertFails(set(ref(alice, `rooms/${code}`), { ...room("alice"), secret: 1 }));
    await assertSucceeds(set(ref(alice, `rooms/${code}`), room("alice")));
  });

  it("lets anyone signed in see the lobby, but only the host change it or its clock", async () => {
    const alice = await hostedRoom();
    await assertSucceeds(set(ref(alice, `rooms/${code}/state`), "story"));
    await assertSucceeds(set(ref(alice, `rooms/${code}/clock`), { lineIndex: 1, startedAtServerMs: 0, paused: false }));
    await assertFails(set(ref(alice, `rooms/${code}/host`), "bob"));
    await assertFails(set(ref(alice, `rooms/${code}/clock`), { lineIndex: -1, startedAtServerMs: 0, paused: false }));

    const bob = env.authenticatedContext("bob").database();
    await assertSucceeds(get(ref(bob, `rooms/${code}/state`)));
    await assertSucceeds(get(ref(bob, `rooms/${code}/players`)));
    await assertFails(set(ref(bob, `rooms/${code}/state`), "game"));
    await assertFails(set(ref(bob, `rooms/${code}/clock`), { lineIndex: 9, startedAtServerMs: 0, paused: true }));
    await assertFails(set(ref(bob, `rooms/${code}`), null));
  });

  it("shows the clock, poses and game results only to players in the room", async () => {
    const alice = await hostedRoom();
    await assertSucceeds(set(ref(alice, `rooms/${code}/clock`), { lineIndex: 1, startedAtServerMs: 0, paused: false }));
    await assertSucceeds(set(ref(alice, `rooms/${code}/poses/alice`), pose));

    const eve = env.authenticatedContext("eve").database();
    await assertFails(get(ref(eve, `rooms/${code}/poses`)));
    await assertFails(get(ref(eve, `rooms/${code}/clock`)));
    await assertFails(get(ref(eve, `rooms/${code}/game`)));
    await assertFails(get(ref(eve, `rooms/${code}`)));

    const bob = env.authenticatedContext("bob").database();
    await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 1)));
    await assertSucceeds(get(ref(bob, `rooms/${code}/poses`)));
    await assertSucceeds(get(ref(bob, `rooms/${code}/clock`)));
  });

  it("lets each player write only their own seat, pose and game entries, in the contract's shape", async () => {
    await hostedRoom();
    const bob = env.authenticatedContext("bob").database();
    await assertFails(set(ref(bob, `rooms/${code}/poses/bob`), pose)); // not seated yet
    await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 1)));
    await assertSucceeds(set(ref(bob, `rooms/${code}/poses/bob`), pose));
    await assertSucceeds(set(ref(bob, `rooms/${code}/game/bob`), { startedAt: 1, finishedMs: 2, score: 3 }));
    await assertFails(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 2)));
    await assertFails(set(ref(bob, `rooms/${code}/players/bob`), { ...player("Bob", 1), platform: "fridge" }));
    await assertFails(set(ref(bob, `rooms/${code}/poses/bob`), { h: [0, 1, 2], t: 1 }));
    await assertFails(set(ref(bob, `rooms/${code}/players/carol`), player("Carol", 1)));
    await assertFails(set(ref(bob, `rooms/${code}/poses/alice`), pose));
    await assertFails(set(ref(bob, `rooms/${code}/game/alice`), { startedAt: 1, finishedMs: 2, score: 999 }));
    await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob`), null)); // leaving
  });

  it("does not let the host overwrite other players' entries", async () => {
    const alice = await hostedRoom();
    const bob = env.authenticatedContext("bob").database();
    await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 1)));
    await assertFails(set(ref(alice, `rooms/${code}/players/bob`), { ...player("Bob", 1), online: false }));
    await assertFails(set(ref(alice, `rooms/${code}/game/bob`), { startedAt: 1, finishedMs: 2, score: 0 }));
  });

  it("lets only the host close the room", async () => {
    const alice = await hostedRoom();
    const bob = env.authenticatedContext("bob").database();
    await assertFails(set(ref(bob, `rooms/${code}`), null));
    await assertSucceeds(set(ref(alice, `rooms/${code}`), null));
  });

  describe("handover", () => {
    const answer = (uid, n, extra = {}) => ({ uid, q: 0, i: n, text: `Sentence ${n}.`, last: false, ...extra });

    async function twoPlayers() {
      const alice = await hostedRoom();
      const bob = env.authenticatedContext("bob").database();
      await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 1)));
      return { alice, bob };
    }

    it("keeps the host in place while they are online", async () => {
      const { alice, bob } = await twoPlayers();
      await assertFails(set(ref(bob, `rooms/${code}/host`), "bob"));
      await assertFails(set(ref(alice, `rooms/${code}/host`), "bob")); // nobody gives the room away either
    });

    it("lets the next player take the room once the host's connection dropped", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice/online`), false)); // what onDisconnect does
      await assertSucceeds(set(ref(bob, `rooms/${code}/host`), "bob"));

      // the clock and the room's state now follow bob, and no longer alice
      await assertSucceeds(set(ref(bob, `rooms/${code}/clock`), { lineIndex: 3, startedAtServerMs: 5, paused: false }));
      await assertSucceeds(set(ref(bob, `rooms/${code}/state`), "story"));
      await assertFails(set(ref(alice, `rooms/${code}/clock`), { lineIndex: 4, startedAtServerMs: 6, paused: false }));
      await assertFails(set(ref(alice, `rooms/${code}/state`), "game"));
      await assertFails(set(ref(alice, `rooms/${code}`), null));
      await assertSucceeds(set(ref(bob, `rooms/${code}`), null));
    });

    it("lets the next player take the room when the host stopped sending a heartbeat", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice/lastSeen`), { ".sv": "timestamp" }));
      await assertFails(set(ref(bob, `rooms/${code}/host`), "bob")); // heard from a moment ago
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice/lastSeen`), 1)); // long ago
      await assertSucceeds(set(ref(bob, `rooms/${code}/host`), "bob"));
    });

    it("lets the next player take the room when the host left it", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice`), null));
      await assertSucceeds(set(ref(bob, `rooms/${code}/host`), "bob"));
    });

    it("lets only an online player of the room take it, and only for themselves", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice/online`), false));
      const eve = env.authenticatedContext("eve").database();
      await assertFails(set(ref(eve, `rooms/${code}/host`), "eve")); // not in the room
      await assertFails(set(ref(bob, `rooms/${code}/host`), "eve")); // not for someone else
      await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob/online`), false));
      await assertFails(set(ref(bob, `rooms/${code}/host`), "bob")); // offline themselves
      await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob/online`), true));
      await assertSucceeds(set(ref(bob, `rooms/${code}/host`), "bob"));
    });

    it("does not let the old host take the room back while the new host is online", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice/online`), false));
      await assertSucceeds(set(ref(bob, `rooms/${code}/host`), "bob"));
      await assertSucceeds(set(ref(alice, `rooms/${code}/players/alice`), player("Alice", 0))); // comes back as a guest
      await assertFails(set(ref(alice, `rooms/${code}/host`), "alice"));
    });

    it("does not let a heartbeat claim to be from the future", async () => {
      const { alice } = await twoPlayers();
      await assertFails(set(ref(alice, `rooms/${code}/players/alice/lastSeen`), Date.now() + 3600 * 1000));
      await assertFails(set(ref(alice, `rooms/${code}/players/alice/lastSeen`), "now"));
    });

    it("lets the player whose hand is up add the answer, a sentence at a time", async () => {
      const { alice, bob } = await twoPlayers();
      await assertFails(set(ref(bob, `rooms/${code}/answers/0`), answer("bob", 0))); // hand not up
      await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob/handUp`), true));
      await assertSucceeds(set(ref(bob, `rooms/${code}/answers/0`), answer("bob", 0)));
      await assertSucceeds(set(ref(bob, `rooms/${code}/answers/1`), answer("bob", 1, { last: true })));

      await assertSucceeds(get(ref(alice, `rooms/${code}/answers`))); // both players hear it
      await assertSucceeds(get(ref(bob, `rooms/${code}/answers`)));
      const eve = env.authenticatedContext("eve").database();
      await assertFails(get(ref(eve, `rooms/${code}/answers`)));
      await assertFails(set(ref(eve, `rooms/${code}/answers/2`), answer("eve", 2)));
    });

    it("keeps a spoken sentence from being rewritten, and checks its shape", async () => {
      const { alice, bob } = await twoPlayers();
      await assertSucceeds(set(ref(bob, `rooms/${code}/players/bob/handUp`), true));
      await assertSucceeds(set(ref(bob, `rooms/${code}/answers/0`), answer("bob", 0)));
      await assertFails(set(ref(bob, `rooms/${code}/answers/0`), answer("bob", 0, { text: "Something else." })));
      await assertFails(set(ref(bob, `rooms/${code}/answers/0`), null));
      await assertFails(set(ref(bob, `rooms/${code}/answers/1`), answer("alice", 1))); // not in someone else's name
      await assertFails(set(ref(bob, `rooms/${code}/answers/1`), answer("bob", 1, { text: "" })));
      await assertFails(set(ref(bob, `rooms/${code}/answers/1`), answer("bob", 1, { text: "x".repeat(1001) })));
      await assertFails(set(ref(bob, `rooms/${code}/answers/1`), answer("bob", 1, { q: -1 })));
      await assertFails(set(ref(bob, `rooms/${code}/answers/1`), answer("bob", 1, { secret: 1 })));
      await assertFails(set(ref(bob, `rooms/${code}/answers/one`), answer("bob", 1)));
      await assertFails(set(ref(bob, `rooms/${code}/answers/12345`), answer("bob", 1)));
      await assertFails(set(ref(alice, `rooms/${code}/answers/1`), answer("alice", 1))); // alice's hand is down
    });
  });

  it("does not let anyone join a room that does not exist", async () => {
    const bob = env.authenticatedContext("bob").database();
    await assertFails(set(ref(bob, `rooms/${code}/players/bob`), player("Bob", 1)));
  });

  it("keeps everything outside rooms closed", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertFails(set(ref(alice, "users/alice"), { x: 1 }));
    await assertFails(get(ref(alice, "users/alice")));
  });
});
