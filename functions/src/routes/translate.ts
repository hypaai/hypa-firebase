import { MAX_TRANSLATE_CHARS } from "../config";
import type { Route } from "../api/types";
import { optional, required } from "../utils/validate";

/** Translation. One request, no audio seconds. */
export const translate: Route = (body, hypa) => {
  const text = required(body, "text", MAX_TRANSLATE_CHARS);
  const targetLang = required(body, "targetLang", 64);
  const sourceLang = optional(body, "sourceLang", 64);
  return {
    cost: { requests: 1, seconds: 0 },
    run: async () => ({ ...(await hypa.translate({ text, targetLang, sourceLang })) }),
  };
};
