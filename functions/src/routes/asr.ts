import { MAX_ASR_BASE64_CHARS, MAX_ASR_BYTES, MAX_ASR_SECONDS } from "../config";
import { badRequest, tooLarge } from "../api/errors";
import type { Route } from "../api/types";
import { audioSeconds } from "../utils/audio";
import { required } from "../utils/validate";

/** Speech to text. Up to 30 s / 1 MiB of base64 audio; charged by the recording's length. */
export const asr: Route = (body, hypa) => {
  const audio = required(body, "audioBase64", MAX_ASR_BASE64_CHARS);
  const mime = required(body, "mime", 128);
  if (!/^audio\//i.test(mime)) throw badRequest("'mime' must be an audio MIME type.");
  const language = required(body, "language", 64);
  const sourceLang = language.toLowerCase() === "auto" ? undefined : language;
  const declared = body.durationSeconds;
  if (declared !== undefined && (typeof declared !== "number" || !Number.isFinite(declared) || declared < 0)) {
    throw badRequest("'durationSeconds' must be a non-negative number.");
  }
  const buf = Buffer.from(audio, "base64");
  if (buf.length === 0 || buf.toString("base64") !== audio)
    throw badRequest("'audioBase64' is not valid base64 audio.");
  if (buf.length > MAX_ASR_BYTES) throw tooLarge("The recording is too large (max about 30 seconds).");
  const seconds = audioSeconds(buf, declared as number | undefined);
  if (seconds > MAX_ASR_SECONDS) throw tooLarge(`The recording is too long (max ${MAX_ASR_SECONDS} seconds).`);
  return {
    cost: { requests: 1, seconds },
    run: async () => {
      const { transcript, language, route } = await hypa.asr({ audioBase64: audio, sourceLang });
      return { text: transcript, model: route.model, language, route, audioSeconds: seconds };
    },
  };
};
