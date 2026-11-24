import { DEFAULT_CHAT_MAX_TOKENS, MAX_CHAT_CHARS, MAX_CHAT_MAX_TOKENS, MAX_CHAT_MESSAGES } from "../config";
import { badRequest, tooLarge } from "../api/errors";
import type { ChatMessage } from "../api/hypa";
import type { Route } from "../api/types";
import { optional } from "../utils/validate";

/** Chat. One request, no audio seconds. An explicit model uses provider/model format. */
export const chat: Route = (body, hypa) => {
  const system = optional(body, "system", MAX_CHAT_CHARS);
  const language = optional(body, "language", 64);
  const raw = body.messages;
  if (!Array.isArray(raw) || raw.length === 0) throw badRequest("'messages' must be a non-empty array.");
  if (raw.length > MAX_CHAT_MESSAGES) throw tooLarge("Too many messages.");
  const messages: ChatMessage[] = raw.map((m) => {
    if (!["system", "user", "assistant"].includes(m?.role))
      throw badRequest("Each message needs a role of system, user or assistant.");
    if (typeof m.content !== "string" || !m.content.trim()) throw badRequest("Each message needs text 'content'.");
    return { role: m.role, content: m.content };
  });
  const instructions = [system, language && "Answer in " + language + "."].filter(Boolean).join("\n\n");
  if (messages.reduce((n, m) => n + m.content.length, instructions.length) > MAX_CHAT_CHARS)
    throw tooLarge("The conversation is too long.");

  const model = optional(body, "model", 200)?.trim() || undefined;
  if (model && model.toLowerCase() !== "auto" && !/^[^/\s]+\/[^/\s]+(?:\/[^/\s]+)*$/.test(model))
    throw badRequest("'model' must be in provider/model format.");

  const { maxTokens = DEFAULT_CHAT_MAX_TOKENS } = body;
  if (typeof maxTokens !== "number" || !Number.isInteger(maxTokens) || maxTokens < 1)
    throw badRequest("'maxTokens' must be a positive integer.");

  return {
    cost: { requests: 1, seconds: 0 },
    run: async () => {
      const { content, route } = await hypa.chat({
        messages: instructions ? [{ role: "system", content: instructions }, ...messages] : messages,
        model,
        maxTokens: Math.min(maxTokens, MAX_CHAT_MAX_TOKENS),
      });
      return { content, text: content, model: route.model, route };
    },
  };
};
