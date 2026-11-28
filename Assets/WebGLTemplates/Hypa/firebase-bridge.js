// Firebase JS SDK side of the WebGL bridge (G03, docs/web-bridge.md). The Firebase Unity SDK does not run in WebGL, so C#
// calls these through Assets/Plugins/WebGL/HypaFirebase.jslib: call(method, args, ctx) returns a promise; streaming
// methods also send chunks with ctx.emit, and auth and progress changes go out through ctx.event. index.html imports this module and exposes it as window.hypaFirebase.
// Tokens are returned to C# only; nothing here logs a token.
import { initializeApp } from "https://www.gstatic.com/firebasejs/13.4.0/firebase-app.js";
import {
  getAuth, connectAuthEmulator, onAuthStateChanged, signInAnonymously, linkWithPopup, signInWithCredential, GoogleAuthProvider, OAuthProvider,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-auth.js";
import { initializeAppCheck, ReCaptchaEnterpriseProvider, getToken } from "https://www.gstatic.com/firebasejs/13.4.0/firebase-app-check.js";
import {
  initializeFirestore, connectFirestoreEmulator, persistentLocalCache, persistentMultipleTabManager, doc, getDoc, getDocFromCache, setDoc, onSnapshot,
  collection, addDoc, getDocs,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-firestore.js";
import {
  getRemoteConfig, ensureInitialized, fetchAndActivate, getAll,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-remote-config.js";
import {
  getDatabase, connectDatabaseEmulator, ref, get, set, remove, onValue, onDisconnect, serverTimestamp,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-database.js";
import {
  getAI, getGenerativeModel, getLiveGenerativeModel, GoogleAIBackend, InferenceMode, InferenceSource, ResponseModality,
} from "https://www.gstatic.com/firebasejs/13.4.0/firebase-ai.js";

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
let auth, appCheck, db, rc, ai, nano, rtdb = null, sendEvent = () => {};
let usingEmulators = false;
const cloudModels = new Map();
// Progress: the newest updatedAt this page has seen (set once it has loaded or saved, so the first snapshot is not "news"),
// and a save waiting for sign-in (first launch, still offline).
let known = null, pendingSave = null, unwatch = null;

function init(config, ctx) {
  sendEvent = ctx.event;
  usingEmulators = config.emulators === true;
  // Google sign-in (Sign in to sync) needs an auth domain; the emulators serve its popup themselves, so any name will do.
  if (usingEmulators && !config.firebase.authDomain) config.firebase.authDomain = config.firebase.projectId + ".firebaseapp.com";
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
  // Story Circle rooms (G09): only when the config names a Realtime Database.
  if (config.firebase.databaseURL) {
    rtdb = getDatabase(app);
    if (config.emulators) connectDatabaseEmulator(rtdb, location.hostname, 9000);
    onValue(ref(rtdb, ".info/serverTimeOffset"), (s) => sendEvent("roomOffset", s.val() || 0));
    onValue(ref(rtdb, ".info/connected"), (s) => { if (s.val() === true) rejoinPresence(); });
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

// Sign in to sync (G08): links this device's anonymous account to Google or Apple, so its progress stays. When that account
// already has a player (signed in on another device first), this device switches to it, so both share one save; what this
// anonymous account saved stays on this device only. Returns "linked" or "switched".
async function link({ provider }) {
  const u = await user();
  const p = provider === "google.com" ? new GoogleAuthProvider() : new OAuthProvider(provider);
  try {
    await linkWithPopup(u, p);
    return "linked";
  } catch (e) {
    if (e.code !== "auth/credential-already-in-use") throw e;
    const credential = provider === "google.com" ? GoogleAuthProvider.credentialFromError(e) : OAuthProvider.credentialFromError(e);
    if (!credential) throw e;
    await signInWithCredential(auth, credential);
    return "switched";
  }
}

/** Who is signed in: { anonymous, email }, or null before sign-in. */
async function account() {
  await ready;
  const u = auth.currentUser;
  return u ? { anonymous: u.isAnonymous, email: u.email || "" } : null;
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

const profileRef = (u) => doc(db, "users", u.uid);

async function loadProfile() {
  let u;
  try { u = await user(); } catch { return null; }
  let snap;
  try { snap = await getDoc(profileRef(u)); } catch { snap = await getDocFromCache(profileRef(u)).catch(() => null); }
  return snap && snap.exists() ? snap.data() : null;
}

async function saveProfile(p) {
  const u = await user();
  setDoc(profileRef(u), p).catch((e) => console.warn("Hypa: profile not saved (" + e.code + ")."));
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
  if (response.inferenceSource !== InferenceSource.ON_DEVICE) throw new BridgeError("not_on_device", "The answer did not come from the device.");
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

async function generateTurn({ model, contents, tools }, { signal }) {
  const params = { model };
  if (tools && tools.length) params.tools = [{ functionDeclarations: tools }];
  const response = (await getGenerativeModel(ai, params).generateContent({ contents }, { signal })).response;
  const calls = (response.functionCalls && response.functionCalls()) || [];
  const content = (response.candidates && response.candidates[0] && response.candidates[0].content) || { role: "model", parts: [] };
  return { content, calls, text: calls.length ? "" : response.text() };
}

async function logRoute(event) {
  const u = await user();
  addDoc(collection(db, "routes", u.uid, "events"), event).catch((e) => console.warn("Hypa: route event not saved (" + e.code + ")."));
  return null;
}

async function nanoDownload(args, { emit }) {
  // Needs a recent user gesture (Chrome's rule for starting a download); C# calls it from a button press.
  await (await nanoModel()).initializeDeviceModel((fraction) => emit(fraction));
  return nanoStatus();
}

// ---------- Live API (G10): talking with the old woman ----------
// One session at a time. The page records the microphone (echo cancellation on, so she does not hear herself) and plays her
// voice, so audio never crosses into C#; transcripts, tool calls, interruptions and levels go back as liveStart's chunks.
// No voice activity settings: the SDK's defaults. The session ends when C# aborts the call (End, leaving the hut).

const LIVE_WORKLET =
  "class Live16k extends AudioWorkletProcessor {" +
  " constructor() { super(); this.step = sampleRate / 16000; this.acc = 0; this.sum = 0; this.n = 0; this.sq = 0; this.buf = new Int16Array(1600); this.len = 0; }" +
  " process(inputs) { var ch = inputs[0] && inputs[0][0]; if (!ch) return true;" +
  "  for (var i = 0; i < ch.length; i++) { this.sum += ch[i]; this.n++; this.acc += 1;" +
  "   if (this.acc >= this.step) { this.acc -= this.step; var v = this.sum / this.n; this.sum = 0; this.n = 0; this.sq += v * v;" +
  "    this.buf[this.len++] = Math.max(-32768, Math.min(32767, Math.round(v * 32767)));" +
  "    if (this.len === this.buf.length) { this.port.postMessage({ pcm: this.buf.slice(0), rms: Math.sqrt(this.sq / this.len) }); this.len = 0; this.sq = 0; } } }" +
  "  return true; } }" +
  " registerProcessor('hypa-live-16k', Live16k);";
const LIVE_SPEECH_RMS = 0.02; // the same speech threshold as MicListener, for the first-audio timing only
let live = null;

function base64(buffer) {
  const bytes = new Uint8Array(buffer);
  let s = "";
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

class LiveRun {
  constructor(session, emit) {
    this.session = session; this.emit = emit;
    this.stopped = false; this.held = false;
    this.sources = []; this.next = 0; this.lastSpeech = 0; this.spoke = false; this.mic = 0; this.sent = { v: -1, mic: -1 };
  }

  async start() {
    this.ctx = new AudioContext();
    if (this.ctx.state === "suspended") await this.ctx.resume().catch(() => {});
    this.out = this.ctx.createGain();
    this.analyser = this.ctx.createAnalyser();
    this.analyser.fftSize = 512;
    this.out.connect(this.analyser);
    this.analyser.connect(this.ctx.destination);
    this.levels = setInterval(() => this.level(), 66);
    try {
      this.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 } });
    } catch (e) {
      this.emit({ t: "mic", state: "denied" }); // the conversation carries on in text
      return;
    }
    if (this.stopped) { this.stream.getTracks().forEach((t) => t.stop()); return; }
    const url = URL.createObjectURL(new Blob([LIVE_WORKLET], { type: "application/javascript" }));
    await this.ctx.audioWorklet.addModule(url);
    URL.revokeObjectURL(url);
    if (this.stopped) return;
    this.node = new AudioWorkletNode(this.ctx, "hypa-live-16k");
    this.node.port.onmessage = (e) => this.heard(e.data);
    this.ctx.createMediaStreamSource(this.stream).connect(this.node);
    this.emit({ t: "mic", state: "on" });
  }

  heard({ pcm, rms }) {
    this.mic = Math.min(1, rms * 6);
    if (rms > LIVE_SPEECH_RMS) this.lastSpeech = performance.now();
    if (this.held || this.stopped) return;
    this.session.sendAudioRealtime({ mimeType: "audio/pcm;rate=16000", data: base64(pcm.buffer) }).catch(() => {});
  }

  play(data) {
    const bytes = Uint8Array.from(atob(data), (c) => c.charCodeAt(0));
    const pcm = new Int16Array(bytes.buffer, 0, bytes.length >> 1);
    const buffer = this.ctx.createBuffer(1, pcm.length, 24000);
    const ch = buffer.getChannelData(0);
    for (let i = 0; i < pcm.length; i++) ch[i] = pcm[i] / 32768;
    const src = this.ctx.createBufferSource();
    src.buffer = buffer;
    src.connect(this.out);
    src.onended = () => { this.sources = this.sources.filter((x) => x !== src); };
    this.next = Math.max(this.ctx.currentTime, this.next);
    src.start(this.next);
    this.next += buffer.duration;
    this.sources.push(src);
  }

  interrupt() {
    this.sources.forEach((s) => { try { s.stop(0); } catch (e) { /* already ended */ } });
    this.sources = [];
    if (this.ctx) this.next = this.ctx.currentTime;
  }

  level() {
    if (!this.analyser) return;
    const buf = new Float32Array(this.analyser.fftSize);
    this.analyser.getFloatTimeDomainData(buf);
    let sum = 0;
    for (let i = 0; i < buf.length; i++) sum += buf[i] * buf[i];
    const v = Math.round(Math.min(1, Math.sqrt(sum / buf.length) * 5) * 50) / 50, mic = Math.round(this.mic * 50) / 50;
    if (v === this.sent.v && mic === this.sent.mic) return;
    this.sent = { v, mic };
    this.emit({ t: "level", v, mic });
  }

  async loop() {
    for await (const m of this.session.receive()) {
      if (this.stopped) break;
      if (m.type === "serverContent") {
        if (m.interrupted) { this.interrupt(); this.spoke = false; this.emit({ t: "interrupted" }); }
        if (m.inputTranscription && m.inputTranscription.text) this.emit({ t: "in", text: m.inputTranscription.text });
        for (const part of (m.modelTurn && m.modelTurn.parts) || []) {
          const inline = part.inlineData;
          if (!inline || !inline.mimeType || !inline.mimeType.startsWith("audio/") || this.held) continue;
          if (!this.spoke) {
            this.spoke = true;
            const since = performance.now() - this.lastSpeech;
            this.emit({ t: "first", ms: this.lastSpeech > 0 && since < 15000 ? Math.round(since) : 0 });
          }
          this.play(inline.data);
        }
        if (m.outputTranscription && m.outputTranscription.text) this.emit({ t: "out", text: m.outputTranscription.text });
        if (m.turnComplete) { this.spoke = false; this.emit({ t: "turn" }); }
      } else if (m.type === "toolCall") {
        this.emit({ t: "tool", calls: (m.functionCalls || []).map((c) => ({ id: c.id, name: c.name, args: c.args || {} })) });
      } else if (m.type === "toolCallCancellation") {
        this.emit({ t: "toolCancel", ids: m.ids || [] });
      } else if (m.type === "goingAwayNotice") {
        this.emit({ t: "goAway", timeLeft: m.timeLeft || 0 });
      } else if (m.type === "sessionResumptionUpdate") {
        if (m.newHandle && m.resumable !== false) this.emit({ t: "handle", handle: m.newHandle });
      }
    }
  }

  stop() {
    if (this.stopped) return;
    this.stopped = true;
    clearInterval(this.levels);
    this.interrupt();
    if (this.node) { this.node.port.onmessage = null; this.node.disconnect(); }
    if (this.stream) this.stream.getTracks().forEach((t) => t.stop());
    if (this.ctx && this.ctx.state !== "closed") this.ctx.close().catch(() => {});
    this.session.close().catch(() => {});
    if (live === this) live = null;
  }
}

async function liveConnect({ model, system, voice, handle, tools }) {
  const generationConfig = {
    responseModalities: [ResponseModality.AUDIO],
    inputAudioTranscription: {}, outputAudioTranscription: {},
    contextWindowCompression: { slidingWindow: {} },
  };
  if (voice) generationConfig.speechConfig = { voiceConfig: { prebuiltVoiceConfig: { voiceName: voice } } };
  const params = { model, generationConfig };
  if (system) params.systemInstruction = system;
  if (tools && tools.length) params.tools = [{ functionDeclarations: tools }];
  return getLiveGenerativeModel(ai, params).connect(handle ? { handle } : {});
}

const LIVE_FALLBACK_VOICE = "Aoede";

async function liveStart(args, { emit, signal }) {
  await ready;
  if (live) live.stop();
  let session;
  try {
    session = await liveConnect(args);
  } catch (e) {
    // the model's default voice is a man's (Puck): fall back to another woman's voice, never to the default
    if (!args.voice || args.voice === LIVE_FALLBACK_VOICE) throw e;
    console.warn("Hypa: Live session refused with voice " + args.voice + "; trying " + LIVE_FALLBACK_VOICE + ".");
    session = await liveConnect({ ...args, voice: LIVE_FALLBACK_VOICE });
  }
  const run = (live = new LiveRun(session, emit));
  signal.addEventListener("abort", () => run.stop(), { once: true });
  if (signal.aborted) { run.stop(); return null; }
  emit({ t: "ready" });
  try {
    await run.start();
  } catch (e) {
    console.warn("Hypa: the microphone could not start for the Live session (" + (e && e.name) + ").");
    emit({ t: "mic", state: "denied" });
  }
  try { await run.loop(); } finally { run.stop(); }
  return null;
}

async function liveSend({ text }) {
  if (live) await live.session.sendTextRealtime(text);
  return null;
}

async function liveTool({ id, name, result }) {
  if (live) await live.session.sendFunctionResponses([{ id, name, response: { result } }]);
  return null;
}

async function liveHold({ on }) {
  if (live) { live.held = !!on; if (on) live.interrupt(); }
  return null;
}

// ---------- Story Circle rooms (G09, docs/contracts.md section 4) ----------
// The room node is not readable as a whole, so each child is listened to on its own. Presence: onDisconnect sets
// players/{uid}/online to false (never removes anything, so handover still works) and is set again on every reconnect.
// Handover and turn-taking are decided in C# (RoomHandover, CircleSession); this side only reads and writes.

let room = null; // { code, uid, me, unsubs, answersSeen, primed }

function database() {
  if (!rtdb) throw new BridgeError("not_configured", "No Realtime Database URL in the web config.");
  return rtdb;
}

const roomRef = (path) => ref(database(), "rooms/" + room.code + (path ? "/" + path : ""));
const denied = (e) => e && (e.code === "PERMISSION_DENIED" || String(e.message || "").toLowerCase().includes("permission"));

function playerEntry(me) {
  return { name: String(me.name || "").slice(0, 24), platform: me.platform || "web", seat: me.seat, online: true, handUp: !!me.handUp };
}

async function seat(me) {
  await set(roomRef("players/" + room.uid), playerEntry(me));
  await onDisconnect(roomRef("players/" + room.uid + "/online")).set(false);
}

function rejoinPresence() {
  // after a dropped connection the server ran onDisconnect (online = false): say we are back, and arm it again
  if (!room) return;
  seat(room.me).catch((e) => console.warn("Hypa: room presence not restored (" + (e.code || e.name) + ")."));
}

function listen() {
  const on = (path, fn) => room.unsubs.push(onValue(roomRef(path), (s) => fn(s.val()), (e) => {
    console.warn("Hypa: room listener stopped (" + (e.code || e.name) + ").");
    if (denied(e)) closeRoom();
  }));
  on("host", (v) => { if (v === null) closeRoom(); else sendEvent("roomHost", v); });
  on("state", (v) => { if (v !== null) sendEvent("roomState", v); });
  on("clock", (v) => { if (v !== null) sendEvent("roomClock", v); });
  on("players", (v) => sendEvent("roomPlayers", v || {}));
  on("poses", (v) => sendEvent("roomPoses", v || {}));
  on("game", (v) => { if (v !== null) sendEvent("roomGame", v); });
  on("answers", (v) => {
    const all = v || {};
    const keys = Object.keys(all).map(Number).filter((n) => Number.isInteger(n) && n >= 0).sort((a, b) => a - b);
    if (!room.primed) { room.primed = true; room.answersSeen = keys.length ? keys[keys.length - 1] : -1; room.nextAnswer = room.answersSeen + 1; return; }
    for (const n of keys) {
      if (n <= room.answersSeen) continue;
      room.answersSeen = n;
      room.nextAnswer = Math.max(room.nextAnswer, n + 1);
      if (all[n] && all[n].uid !== room.uid) sendEvent("roomAnswer", all[n]);
    }
  });
}

function stopListening() {
  if (!room) return;
  for (const u of room.unsubs) u();
  room.unsubs = [];
}

function closeRoom() {
  if (!room) return;
  stopListening();
  room = null;
  sendEvent("roomClosed", null);
}

async function roomCreate({ code, storyId, language, me }) {
  const u = await user();
  if (room) throw new BridgeError("bad_request", "Already in a room.");
  const r = ref(database(), "rooms/" + code);
  try {
    await set(r, { host: u.uid, storyId: storyId || "", language: language || "", state: "lobby", createdAt: serverTimestamp() });
  } catch (e) {
    if (denied(e)) throw new BridgeError("taken", "That room code is taken.");
    throw e;
  }
  room = { code, uid: u.uid, me: { ...me, seat: 0 }, unsubs: [], primed: false, answersSeen: -1, nextAnswer: 0 };
  await seat(room.me);
  listen();
  return { host: u.uid, storyId, language, state: "lobby", createdAt: Date.now() };
}

async function roomJoin({ code, me, maxPlayers, serverNow }) {
  const u = await user();
  if (room) throw new BridgeError("bad_request", "Already in a room.");
  const base = "rooms/" + code;
  const read = async (path) => (await get(ref(database(), base + "/" + path))).val();
  let host;
  try { host = await read("host"); } catch (e) { if (denied(e)) throw new BridgeError("not_found", "No room " + code + "."); throw e; }
  if (host === null) throw new BridgeError("not_found", "No room " + code + ".");
  const createdAt = (await read("createdAt")) || 0;
  if (serverNow - createdAt > 2 * 60 * 60 * 1000) throw new BridgeError("expired", "Room " + code + " has expired.");
  const players = (await read("players")) || {};
  let seatNo = players[u.uid] ? players[u.uid].seat : -1;
  for (let s = 0; seatNo < 0 && s < maxPlayers; s++) if (!Object.values(players).some((p) => p && p.seat === s)) seatNo = s;
  if (seatNo < 0) throw new BridgeError("full", "Room " + code + " is full.");
  room = { code, uid: u.uid, me: { ...me, seat: seatNo }, unsubs: [], primed: false, answersSeen: -1, nextAnswer: 0 };
  try {
    await seat(room.me);
    // two players may have taken the same seat at once: the lower uid keeps it
    const after = (await read("players")) || {};
    if (Object.entries(after).some(([id, p]) => id !== u.uid && p && p.seat === seatNo && id < u.uid)) throw new BridgeError("full", "Room " + code + " is full.");
  } catch (e) {
    await remove(roomRef("players/" + u.uid)).catch(() => {});
    room = null;
    throw e;
  }
  listen();
  return { info: { host, storyId: await read("storyId"), language: await read("language"), state: await read("state"), createdAt }, seat: seatNo };
}

async function roomLeave({ closeRoom: close }) {
  if (!room) return null;
  const r = room;
  stopListening();
  room = null;
  await onDisconnect(ref(database(), "rooms/" + r.code + "/players/" + r.uid + "/online")).cancel().catch(() => {});
  const path = "rooms/" + r.code + (close ? "" : "/players/" + r.uid);
  await remove(ref(database(), path)).catch((e) => console.warn("Hypa: leaving the room was not recorded (" + (e.code || e.name) + ")."));
  return null;
}

function inRoom() { if (!room) throw new BridgeError("not_in_room", "Not in a room."); }

async function roomSetState({ state }) { inRoom(); await set(roomRef("state"), state); return null; }
async function roomSetClock({ clock }) {
  inRoom();
  await set(roomRef("clock"), { lineIndex: Math.max(0, clock.lineIndex | 0), startedAtServerMs: clock.startedAtServerMs, paused: !!clock.paused });
  return null;
}
// Story results and saved words (G08): users/{uid}/stories/{storyId} and users/{uid}/words/{wordId}. Reads fall back to the
// local cache offline; writes go to the cache at once and reach the server when it is reachable.
async function readDoc(ref) {
  try { return await getDoc(ref); } catch { return await getDocFromCache(ref).catch(() => null); }
}

async function getStory({ storyId }) {
  const snap = await readDoc(doc(db, "users", (await user()).uid, "stories", storyId));
  return snap && snap.exists() ? snap.data() : null;
}

async function saveStory(result) {
  const u = await user();
  setDoc(doc(db, "users", u.uid, "stories", result.storyId), result).catch((e) => console.warn("Hypa: story result not saved (" + e.code + ")."));
  return null;
}

async function listWords() {
  const ref = collection(db, "users", (await user()).uid, "words");
  let snap;
  try { snap = await getDocs(ref); } catch { return []; }
  return snap.docs.map((d) => d.data());
}

async function saveWord({ id, word }) {
  const u = await user();
  setDoc(doc(db, "users", u.uid, "words", id), word).catch((e) => console.warn("Hypa: word not saved (" + e.code + ")."));
  return null;
}

async function roomUpdateMe({ me }) {
  inRoom();
  room.me = { ...room.me, name: me.name ?? room.me.name, platform: me.platform ?? room.me.platform, handUp: !!me.handUp };
  await set(roomRef("players/" + room.uid), playerEntry(room.me));
  return null;
}
async function roomPose({ pose }) {
  inRoom();
  const p = { h: pose.h, t: pose.t };
  if (pose.l && pose.l.length) p.l = pose.l;
  if (pose.r && pose.r.length) p.r = pose.r;
  set(roomRef("poses/" + room.uid), p).catch(() => {}); // fire and forget: the next pose replaces it
  return null;
}
async function roomAnswer({ sentence }) {
  inRoom();
  const value = { uid: room.uid, q: Math.max(0, sentence.q | 0), i: Math.max(0, sentence.i | 0), text: sentence.text, last: !!sentence.last };
  for (let attempt = 0; ; attempt++) {
    const n = room.nextAnswer++;
    try { await set(roomRef("answers/" + n), value); return null; } catch (e) { if (!denied(e) || attempt >= 2) throw e; } // n taken: next one
  }
}
async function roomGame({ result }) { inRoom(); await set(roomRef("game/" + room.uid), result); return null; }
async function roomTakeOver() {
  inRoom();
  try { await set(roomRef("host"), room.uid); } catch (e) { if (!denied(e)) throw e; } // someone else was first, or the host is back
  return null;
}

const methods = {
  init, signIn, link, account, idToken: async () => (await user()).getIdToken(), appCheckToken,
  loadProgress, saveProgress, loadProfile, saveProfile, getStory, saveStory, listWords, saveWord, fetchFlags, generate, stream, generateTurn, logRoute, nanoStatus, nanoDownload,
  liveStart, liveSend, liveTool, liveHold,
  roomCreate, roomJoin, roomLeave, roomSetState, roomSetClock, roomUpdateMe, roomPose, roomAnswer, roomGame, roomTakeOver,
};

/** Runs a method. ctx: { emit(chunk), event(name, value), signal }. */
export function call(method, args, ctx) {
  const fn = methods[method];
  if (!fn) return Promise.reject(new BridgeError("bad_request", "Unknown method " + method + "."));
  return Promise.resolve().then(() => fn(args, ctx));
}
