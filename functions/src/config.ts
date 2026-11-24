export const REGION = "us-central1";
export const MIN_INSTANCES = 0;
export const DEFAULT_HYPA_API_BASE = "https://api.hypaintelligence.com";
export const TIMEOUT_MS = { tts: 20_000, chat: 20_000, translate: 20_000, asr: 30_000 } as const;
export const FUNCTION_TIMEOUT_SECONDS = 60;

export const MAX_ASR_SECONDS = 30;
export const MAX_ASR_BYTES = 1024 * 1024;
export const MAX_ASR_BASE64_CHARS = Math.ceil(MAX_ASR_BYTES / 3) * 4;
export const ASSUMED_AUDIO_BYTES_PER_SECOND = 32_000;

export const MAX_TTS_CHARS = 1_000;
export const MAX_TRANSLATE_CHARS = 2_000;
export const MAX_CHAT_MESSAGES = 20;
export const MAX_CHAT_CHARS = 8_000;
export const DEFAULT_CHAT_MAX_TOKENS = 512;
export const MAX_CHAT_MAX_TOKENS = 1_024;
export const TTS_CHARS_PER_SECOND = 15;
export const MAX_INLINE_AUDIO_BYTES = 8 * 1024 * 1024;

export const DEFAULT_LIMITS = {
  userSecondsPerDay: 600,
  userRequestsPerDay: 400,
  globalSecondsPerDay: 20_000,
  globalRequestsPerDay: 10_000,
} as const;

/** Remote Config parameter names. */
export const RC_KEYS = {
  userSecondsPerDay: "proxy_daily_seconds_per_user",
  userRequestsPerDay: "proxy_daily_requests_per_user",
  globalSecondsPerDay: "proxy_global_daily_seconds",
  globalRequestsPerDay: "proxy_global_daily_requests",
} as const;

export const LIMITS_CACHE_MS = 5 * 60_000;
/** Usage documents carry `expireAt`; enable a Firestore TTL policy on it (collection group `days`, and `usage_global`). */
export const USAGE_RETENTION_DAYS = 35;

/** Realtime Database rooms (G09): path and the epoch-ms field the cleanup reads. */
export const ROOMS_PATH = "rooms";
export const ROOMS_CREATED_FIELD = "createdAt";
export const ROOMS_MAX_AGE_MS = 2 * 60 * 60_000;

/**
 * The only place environment variables are read. They exist only in the local emulator (functions/.env.local);
 * when deployed, FUNCTIONS_EMULATOR is unset so both settings are forced back to safe values.
 */
export function emulatorSettings(env: Record<string, string | undefined> = process.env) {
  const emulated = env.FUNCTIONS_EMULATOR === "true";
  return {
    /** HYPA_SKIP_APP_CHECK=true: the emulator has no App Check, so let it through (sign-in is still required). */
    skipAppCheck: emulated && env.HYPA_SKIP_APP_CHECK === "true",
    /** HYPA_API_BASE=http://127.0.0.1:9911: call a fake Hypa. Deployed, the key can only go to the real API. */
    hypaApiBase: (emulated && env.HYPA_API_BASE) || DEFAULT_HYPA_API_BASE,
  };
}
