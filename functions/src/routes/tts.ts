import { MAX_TTS_CHARS, TTS_CHARS_PER_SECOND } from "../config";
import type { Route } from "../api/types";
import { optional, required } from "../utils/validate";

export const tts: Route = (body, hypa) => {
  const text = required(body, "text", MAX_TTS_CHARS);
  const language = required(body, "language", 64);
  const voice = optional(body, "voice", 64);
  const model = optional(body, "model", 200);
  return {
    cost: { requests: 1, seconds: Math.max(1, Math.ceil(text.length / TTS_CHARS_PER_SECOND)) },
    run: async () => {
      const { audioBase64, mime, sampleRate, route } = await hypa.tts({ text, language, voice, model });
      return { audioBase64, mime, sampleRate, model: route.model, voice: route.voice, route };
    },
  };
};
