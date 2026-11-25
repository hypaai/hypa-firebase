// Firebase JS SDK side of the WebGL bridge (G03, docs/web-bridge.md). The Firebase Unity SDK does not run in WebGL, so C#
// calls these through Assets/Plugins/WebGL/HypaFirebase.jslib: call(method, args, ctx) returns a promise; streaming
// methods also send chunks with ctx.emit, and auth and progress changes go out through ctx.event. index.html imports this module and exposes it as window.hypaFirebase.
// Tokens are returned to C# only; nothing here logs a token.
import { initializeApp } from "https://www.gstatic.com/firebasejs/13.4.0/firebase-app.js";
import {
  getAuth, connectAuthEmulator, onAuthStateChanged, signInAnonymously, linkWithPopup, GoogleAuthProvider, OAuthProvider,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-auth.js";
import { initializeAppCheck, ReCaptchaEnterpriseProvider, getToken } from "https://www.gstatic.com/firebasejs/13.4.0/firebase-app-check.js";
import {
  initializeFirestore, connectFirestoreEmulator, persistentLocalCache, persistentMultipleTabManager, doc, getDoc, getDocFromCache, setDoc, onSnapshot,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-firestore.js";
import {
  getRemoteConfig, ensureInitialized, fetchAndActivate, getAll,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-remote-config.js";
import { getAI, getGenerativeModel, GoogleAIBackend, InferenceMode } from "https://www.gstatic.com/firebasejs/13.4.0/firebase-ai.js";

/** Thrown to C# as { code, message }. */
class BridgeError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

// The languages the router may send on device (Remote Config route_on_device_langs). Chrome checks availability per language.
const ON_DEVICE = {
  expectedInputs: [{ type: "text", languages: ["en", "es", "fr"] }],
  expectedOutputs: [{ type: "text", languages: ["en", "es", "fr"] }],
};

let ready = Promise.reject(new BridgeError("not_configured", "Firebase was not initialised."));
ready.catch(() => {});
let auth, appCheck, db, rc, ai, nano, sendEvent = () => {};
let usingEmulators = false;
const cloudModels = new Map();
// Progress: the newest updatedAt this page has seen (set once it has loaded or saved, so the first snapshot is not "news"),
// and a save waiting for sign-in (first launch, still offline).
let known = null, pendingSave = null, unwatch = null;

function init(config, ctx) {
  sendEvent = ctx.event;
  usingEmulators = config.emulators === true;
  const app = initializeApp(config.firebase);
  // The emulators do not check App Check, so it stays off there. The real project on localhost (?firebase=live) uses the
  // App Check debug provider: register the token printed in the console in the Firebase console.
  if (!config.emulators) {
    if (location.hostname === "localhost" || location.hostname === "127.0.0.1") self.FIREBASE_APPCHECK_DEBUG_TOKEN = true;
    if (config.recaptchaEnterpriseSiteKey) {
      appCheck = initializeAppCheck(app, {
        provider: new ReCaptchaEnterpriseProvider(config.recaptchaEnterpriseSiteKey), isTokenAutoRefreshEnabled: true,
      });
    } else {
      console.warn("Hypa: no reCAPTCHA Enterprise site key, so App Check is off and enforced services will refuse calls.");
    }
  }
  auth = getAuth(app);
  // Persistent cache shared by tabs. Where IndexedDB is unavailable (some private windows) Firestore keeps memory only.
  db = initializeFirestore(app, { localCache: persistentLocalCache({ tabManager: persistentMultipleTabManager() }) });
  if (config.emulators) {
    // Ports from firebase.json. Remote Config and AI Logic have no emulator: flags stay on their defaults, cloud Gemini
    // fails, and Gemini Nano works (it runs on the device).
    connectAuthEmulator(auth, "http://" + location.hostname + ":9099", { disableWarnings: true });
    connectFirestoreEmulator(db, location.hostname, 8080);
  }
  rc = getRemoteConfig(app);
  ai = getAI(app, { backend: new GoogleAIBackend() });
  onAuthStateChanged(auth, (user) => {
    sendEvent("uid", user ? user.uid : null);
    watchProgress(user);
    if (user && pendingSave) { const p = pendingSave; pendingSave = null; writeProgress(user, p); }
  });
  // Resolves once the cached user (if any) is restored, so a returning player keeps their anonymous uid.
  ready = auth.authStateReady();
  return ready.then(() => null);
}

async function signIn() {
  await ready;
  if (auth.currentUser) return auth.currentUser.uid;
  return (await signInAnonymously(auth)).user.uid;
}

async function user() {
  await signIn();
  return auth.currentUser;
}

async function link({ provider }) {
  const u = await user();
  const p = provider === "google.com" ? new GoogleAuthProvider() : new OAuthProvider(provider);
  try {
    await linkWithPopup(u, p);
  } catch (e) {
    // Linking must never swap the player for another account and lose this one's progress.
    if (e.code === "auth/credential-already-in-use") throw new BridgeError("already_linked", "That account already belongs to another player.");
    throw e;
  }
  return null;
}

async function appCheckToken() {
  await ready;
  if (usingEmulators) return "";
  if (!appCheck) throw new BridgeError("appcheck", "App Check is not configured.");
  return (await getToken(appCheck, false)).token;
}

const progressRef = (u) => doc(db, "users", u.uid, "progress", "current");

function watchProgress(u) {
  if (unwatch) { unwatch(); unwatch = null; }
  if (!u) return;
  unwatch = onSnapshot(progressRef(u), (snap) => {
    // News only from the server, about a save newer than anything this page has loaded or saved. Our own saves come back
    // with pending writes; the cache replays what we already had.
    if (known === null || snap.metadata.hasPendingWrites || snap.metadata.fromCache || !snap.exists()) return;
    const p = snap.data();
    if (!(p.updatedAt > known)) return;
    known = p.updatedAt;
    sendEvent("progress", p);
  }, (e) => console.warn("Hypa: progress listener stopped (" + e.code + ")."));
}

async function loadProgress() {
  let u;
  try { u = await user(); } catch { return pendingSave; } // first launch offline: nothing on the server is reachable yet
  let snap;
  try { snap = await getDoc(progressRef(u)); } catch { snap = await getDocFromCache(progressRef(u)).catch(() => null); }
  const p = snap && snap.exists() ? snap.data() : null;
  see(p ? p.updatedAt : 0);
  return p;
}

function writeProgress(u, p) {
  // setDoc resolves only when the server confirms, which never happens offline. The write is in the local cache as soon as
  // setDoc is called, which is all SaveAsync promises; errors (rules, quota) are logged without the data.
  setDoc(progressRef(u), p).catch((e) => console.warn("Hypa: progress not saved (" + e.code + ")."));
}

function see(updatedAt) {
  if (known === null || updatedAt > known) known = updatedAt;
}

async function saveProgress(p) {
  see(p.updatedAt);
  await ready;
  if (auth.currentUser) { writeProgress(auth.currentUser, p); return null; }
  // Not signed in yet (first launch, maybe offline): keep the newest save and write it once sign-in succeeds.
  pendingSave = p;
  signInAnonymously(auth).catch(() => {});
  return null;
}

async function fetchFlags() {
  await ready;
  await ensureInitialized(rc);
  await fetchAndActivate(rc).catch(() => {}); // offline: the last activated values stay
  const values = {};
  for (const [key, value] of Object.entries(getAll(rc))) {
    if (value.getSource() === "remote") values[key] = value.asString();
  }
  return values;
}

function cloudModel(name) {
  if (!cloudModels.has(name)) cloudModels.set(name, getGenerativeModel(ai, { model: name }));
  return cloudModels.get(name);
}

async function nanoStatus() {
  // Safari, Firefox and phones have no Prompt API: the router uses the cloud.
  if (!("LanguageModel" in self)) return "unavailable";
  return self.LanguageModel.availability(ON_DEVICE);
}

async function nanoModel() {
  await ready;
  // ONLY_ON_DEVICE, never PREFER: an answer from this model is always from the device, so the route chip is honest, and
  // G05's router does the cloud fallback itself.
  return nano || (nano = getGenerativeModel(ai, { mode: InferenceMode.ONLY_ON_DEVICE, onDeviceParams: { createOptions: ON_DEVICE } }));
}

async function readyNano() {
  // ONLY_ON_DEVICE would wait for a download that can take minutes. Refuse instead, so a story never waits on it.
  const status = await nanoStatus();
  if (status !== "available") throw new BridgeError("unavailable", "On-device model is " + status + ".");
  return nanoModel();
}

function checkOnDevice(response) {
  if (response.inferenceSource !== "on_device") throw new BridgeError("not_on_device", "The answer did not come from the device.");
}

async function generate({ side, model, prompt }, { signal }) {
  const m = side === "device" ? await readyNano() : cloudModel(model);
  const result = await m.generateContent(prompt, { signal });
  if (side === "device") checkOnDevice(result.response);
  return result.response.text();
}

async function stream({ side, model, prompt }, { emit, signal }) {
  const m = side === "device" ? await readyNano() : cloudModel(model);
  const result = await m.generateContentStream(prompt, { signal });
  for await (const chunk of result.stream) {
    if (signal.aborted) break;
    if (side === "device") checkOnDevice(chunk);
    const text = chunk.text();
    if (text) emit(text);
  }
  return null;
}

async function nanoDownload(args, { emit }) {
  // Needs a recent user gesture (Chrome's rule for starting a download); C# calls it from a button press.
  await (await nanoModel()).initializeDeviceModel((fraction) => emit(fraction));
  return nanoStatus();
}

const methods = {
  init, signIn, link, idToken: async () => (await user()).getIdToken(), appCheckToken,
  loadProgress, saveProgress, fetchFlags, generate, stream, nanoStatus, nanoDownload,
};

/** Runs a method. ctx: { emit(chunk), event(name, value), signal }. */
export function call(method, args, ctx) {
  const fn = methods[method];
  if (!fn) return Promise.reject(new BridgeError("bad_request", "Unknown method " + method + "."));
  return Promise.resolve().then(() => fn(args, ctx));
}
