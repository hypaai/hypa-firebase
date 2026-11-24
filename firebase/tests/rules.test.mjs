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
    await assertFails(getDoc(doc(db, "rooms/ABCDE")));
  });
});

describe("database.rules.json", () => {
  const room = (host) => ({ host, storyId: "s1", language: "yo", state: "lobby", createdAt: 1 });

  it("keeps a room closed to people who are not signed in", async () => {
    const anon = env.unauthenticatedContext().database();
    await assertFails(get(ref(anon, "rooms/ABCDE")));
    await assertFails(set(ref(anon, "rooms/ABCDE"), room("alice")));
  });

  it("lets a signed-in player create a room only as its host", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertFails(set(ref(alice, "rooms/ABCDE"), room("bob")));
    await assertSucceeds(set(ref(alice, "rooms/ABCDE"), room("alice")));
  });

  it("lets any signed-in player read a room, but only the host change it or its clock", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertSucceeds(set(ref(alice, "rooms/ABCDE"), room("alice")));
    await assertSucceeds(set(ref(alice, "rooms/ABCDE/clock"), { lineIndex: 1, startedAtServerMs: 0, paused: false }));

    const bob = env.authenticatedContext("bob").database();
    await assertSucceeds(get(ref(bob, "rooms/ABCDE")));
    await assertFails(set(ref(bob, "rooms/ABCDE/state"), "story"));
    await assertFails(set(ref(bob, "rooms/ABCDE/clock"), { lineIndex: 9, startedAtServerMs: 0, paused: true }));
  });

  it("lets each player write only their own presence, pose and game entries", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertSucceeds(set(ref(alice, "rooms/ABCDE"), room("alice")));

    const bob = env.authenticatedContext("bob").database();
    await assertSucceeds(set(ref(bob, "rooms/ABCDE/players/bob"), { name: "Bob", platform: "quest", seat: 1, online: true, handUp: false }));
    await assertSucceeds(set(ref(bob, "rooms/ABCDE/poses/bob"), { t: 1 }));
    await assertSucceeds(set(ref(bob, "rooms/ABCDE/game/bob"), { startedAt: 1, finishedMs: 2, score: 3 }));
    await assertFails(set(ref(bob, "rooms/ABCDE/players/carol"), { name: "Carol", online: true }));
    await assertFails(set(ref(bob, "rooms/ABCDE/poses/alice"), { t: 1 }));
    await assertFails(set(ref(bob, "rooms/ABCDE/game/alice"), { score: 999 }));
  });

  it("keeps everything outside rooms closed", async () => {
    const alice = env.authenticatedContext("alice").database();
    await assertFails(set(ref(alice, "users/alice"), { x: 1 }));
    await assertFails(get(ref(alice, "users/alice")));
  });
});
