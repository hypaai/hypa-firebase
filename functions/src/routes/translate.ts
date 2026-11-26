import { MAX_TRANSLATE_CHARS } from "../config";
import type { Route } from "../api/types";
import { optional, required } from "../utils/validate";

/** Translation. One request, no audio seconds. */
export const translate: Route = (body, hypa) => {
  const text = required(body, "text", MAX_TRANSLATE_CHARS);
  const targetLang = required(body, body.targetLang === undefined ? "to" : "targetLang", 64);
  const from = optional(body, "sourceLang", 64) ?? optional(body, "from", 64);
  const model = optional(body, "model", 200);
  const sourceLang = from?.toLowerCase() === "auto" ? undefined : from;
  return {
    cost: { requests: 1, seconds: 0 },
    run: async () => {
      const { translation, language, route } = await hypa.translate({ text, targetLang, sourceLang, model });
      return { translation, text: translation, model: route.model, language, route };
    },
  };
};
