import { DEFAULT_CHAT_MAX_TOKENS, MAX_CHAT_CHARS, MAX_CHAT_MAX_TOKENS, MAX_CHAT_MESSAGES } from "../config";
import { badRequest, tooLarge } from "../api/errors";
import type { ChatMessage } from "../api/hypa";
import type { Route } from "../api/types";
import { optional } from "../utils/validate";

/** Chat. One request, no audio seconds. `model` is "auto" (default) or "provider/model". */
export const chat: Route = (body, hypa) => {
  const raw = body.messages;
  if (!Array.isArray(raw) || raw.length === 0) throw badRequest("'messages' must be a non-empty array.");
  if (raw.length > MAX_CHAT_MESSAGES) throw tooLarge(`Too many messages (max ${MAX_CHAT_MESSAGES}).`);
  const messages: ChatMessage[] = raw.map((m) => {
    if (!["system", "user", "assistant"].includes(m?.role))
      throw badRequest("Each message needs a role of system, user or assistant.");
    if (typeof m.content !== "string" || !m.content.trim()) throw badRequest("Each message needs text 'content'.");
    return { role: m.role, content: m.content };
  });
  if (messages.reduce((n, m) => n + m.content.length, 0) > MAX_CHAT_CHARS)
    throw tooLarge(`The conversation is too long (max ${MAX_CHAT_CHARS} characters).`);

  const model = optional(body, "model", 128);
  if (model && model.toLowerCase() !== "auto" && !/^[^/\s]+\/[^/\s]+$/.test(model))
    throw badRequest('\'model\' must be "auto" or "provider/model".');

  const { maxTokens = DEFAULT_CHAT_MAX_TOKENS } = body;
  if (typeof maxTokens !== "number" || !Number.isInteger(maxTokens) || maxTokens < 1)
    throw badRequest("'maxTokens' must be a positive integer.");

  return {
    cost: { requests: 1, seconds: 0 },
    run: async () => ({
      ...(await hypa.chat({ messages, model, maxTokens: Math.min(maxTokens, MAX_CHAT_MAX_TOKENS) })),
    }),
  };
};
