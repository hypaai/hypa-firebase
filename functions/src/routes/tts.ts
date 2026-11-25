import { MAX_TTS_CHARS, TTS_CHARS_PER_SECOND } from "../config";
import { badRequest } from "../api/errors";
import type { Route } from "../api/types";
import { optional, required } from "../utils/validate";

export const tts: Route = (body, hypa) => {
  const text = required(body, "text", MAX_TTS_CHARS);
  const language = required(body, "language", 64);
  const voice = optional(body, "voice", 64);
  const model = optional(body, "model", 200);
  const { inline } = body;
  if (inline !== undefined && typeof inline !== "boolean") throw badRequest("'inline' must be a boolean.");
  return {
    cost: { requests: 1, seconds: Math.max(1, Math.ceil(text.length / TTS_CHARS_PER_SECOND)) },
    run: async () => {
      const { audioUrl, audioBase64, mime, sampleRate, route } = await hypa.tts({
        text,
        language,
        voice,
        model,
        inline: inline === true,
      });
      return { audioUrl, audioBase64, mime, sampleRate, model: route.model, voice: route.voice, route };
    },
  };
};
