import { initializeApp } from "firebase-admin/app";
import { getAppCheck } from "firebase-admin/app-check";
import { getAuth } from "firebase-admin/auth";
import { getDatabase } from "firebase-admin/database";
import { getFirestore } from "firebase-admin/firestore";
import { getRemoteConfig } from "firebase-admin/remote-config";
import * as logger from "firebase-functions/logger";
import { defineSecret } from "firebase-functions/params";
import { onRequest } from "firebase-functions/v2/https";
import { onSchedule } from "firebase-functions/v2/scheduler";
import { handleRequest } from "./api/handler";
import { HypaHttpClient } from "./api/hypa";
import type { Deps } from "./api/types";
import { DEFAULT_LIMITS, emulatorSettings, FUNCTION_TIMEOUT_SECONDS, MIN_INSTANCES, RC_KEYS, REGION } from "./config";
import { RemoteConfigLimits } from "./services/limits";
import { FirestoreQuotaStore } from "./services/quota";
import { deleteStaleRooms } from "./services/rooms";

initializeApp();

/** The Hypa key lives only in Secret Manager: `firebase functions:secrets:set HYPA_API_KEY`. */
const HYPA_API_KEY = defineSecret("HYPA_API_KEY");

const emulator = emulatorSettings();

let deps: Deps | undefined;
function getDeps(): Deps {
  if (deps) return deps;
  const template = getRemoteConfig().initServerTemplate({
    defaultConfig: Object.fromEntries(
      Object.entries(RC_KEYS).map(([k, rc]) => [rc, DEFAULT_LIMITS[k as keyof typeof DEFAULT_LIMITS]]),
    ),
  });
  let config: ReturnType<typeof template.evaluate> | undefined;
  return (deps = {
    verifiers: {
      verifyIdToken: (t) => getAuth().verifyIdToken(t),
      verifyAppCheck: (t) => getAppCheck().verifyToken(t),
    },
    quota: new FirestoreQuotaStore(getFirestore()),
    limits: new RemoteConfigLimits(
      {
        load: async () => {
          await template.load();
          config = template.evaluate();
        },
        // NaN (not loaded yet) is rejected by the limits and replaced with the default
        getNumber: (key) => config?.getNumber(key) ?? NaN,
      },
      Date.now,
      (e) => logger.warn("Remote Config unavailable, using last known or default limits", { error: String(e) }),
    ),
    hypa: new HypaHttpClient({ baseUrl: emulator.hypaApiBase, apiKey: () => HYPA_API_KEY.value() }),
    skipAppCheck: emulator.skipAppCheck,
    log: logger,
  });
}

/**
 * /hypa/tts, /hypa/asr, /hypa/chat, /hypa/translate. Hosting rewrites /hypa/** here (same-origin, no CORS).
 * Publicly invokable so Hosting and native apps can reach it; every request is rejected without a valid ID token and App Check token.
 */
export const hypa = onRequest(
  {
    region: REGION,
    secrets: [HYPA_API_KEY],
    timeoutSeconds: FUNCTION_TIMEOUT_SECONDS,
    memory: "512MiB",
    minInstances: MIN_INSTANCES,
    maxInstances: 20,
    invoker: "public",
    cors: false,
  },
  async (req, res) => {
    const out = await handleRequest(
      { method: req.method, path: req.path, header: (n) => req.header(n), body: req.body },
      getDeps(),
    );
    res
      .set({ "cache-control": "no-store", ...out.headers })
      .status(out.status)
      .json(out.body);
  },
);

/** G09: remove Realtime Database rooms older than 2 hours. */
export const cleanupRooms = onSchedule(
  { region: REGION, schedule: "every 30 minutes", timeoutSeconds: 120, retryCount: 0 },
  async () => {
    logger.info("rooms cleanup", { removed: await deleteStaleRooms(getDatabase() as never, Date.now()) });
  },
);
