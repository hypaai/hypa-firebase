import { MAX_ASR_BASE64_CHARS, MAX_ASR_BYTES, MAX_ASR_SECONDS } from "../config";
import { badRequest, tooLarge } from "../api/errors";
import type { Route } from "../api/types";
import { audioSeconds } from "../utils/audio";
import { optional, required } from "../utils/validate";

/** Speech to text. Up to 30 s / 1 MiB of base64 audio; charged by the recording's length. */
export const asr: Route = (body, hypa) => {
  const audio = required(body, "audio", MAX_ASR_BASE64_CHARS);
  const sourceLang = optional(body, "sourceLang", 64);
  const declared = body.durationSeconds;
  if (declared !== undefined && (typeof declared !== "number" || !Number.isFinite(declared) || declared < 0)) {
    throw badRequest("'durationSeconds' must be a non-negative number.");
  }
  const buf = Buffer.from(audio, "base64");
  if (buf.length === 0) throw badRequest("'audio' is not valid base64 audio.");
  if (buf.length > MAX_ASR_BYTES) throw tooLarge("The recording is too large (max about 30 seconds).");
  const seconds = audioSeconds(buf, declared as number | undefined);
  if (seconds > MAX_ASR_SECONDS) throw tooLarge(`The recording is too long (max ${MAX_ASR_SECONDS} seconds).`);
  return {
    cost: { requests: 1, seconds },
    run: async () => ({ ...(await hypa.asr({ audioBase64: audio, sourceLang })), audioSeconds: seconds }),
  };
};
