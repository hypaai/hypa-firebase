import { MAX_INLINE_AUDIO_BYTES, TIMEOUT_MS } from "../config";
import { jsonPayloads } from "../utils/sse";
import {
  badRequest,
  isTimeout,
  mapHttpError,
  providerBalanceError,
  ProxyError,
  timeoutError,
  upstream,
} from "./errors";

export interface Route {
  provider?: string;
  model?: string;
  voice?: string;
}

export interface ChatMessage {
  role: "system" | "user" | "assistant";
  content: string;
}

export interface HypaApi {
  tts(i: {
    text: string;
    language: string;
    voice?: string;
    model?: string;
  }): Promise<{ audioBase64: string; mime: string; sampleRate?: number; route: Route }>;

  asr(i: {
    audioBase64: string;
    sourceLang?: string;
  }): Promise<{ transcript: string; language?: string; route: Route }>;

  translate(i: {
    text: string;
    targetLang: string;
    sourceLang?: string;
  }): Promise<{ translation: string; language?: string; route: Route }>;

  chat(i: { messages: ChatMessage[]; model?: string; maxTokens: number }): Promise<{ content: string; route: Route }>;
}

const str = (v: unknown): string | undefined => (typeof v === "string" && v ? v : undefined);

const obj = (v: unknown): Record<string, unknown> | undefined =>
  v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : undefined;

/** A tools/call response body -> the tool's structured output, or a ProxyError. */
export function parseToolResult(body: string, tool: string): Record<string, unknown> {
  let env: Record<string, unknown> | undefined;
  for (const p of jsonPayloads(body)) {
    try {
      const o = obj(JSON.parse(p));
      if (o && (o.result !== undefined || o.error !== undefined)) env = o;
    } catch {
      // Not Json Skip
    }
  }
  if (!env) throw upstream("Hypa returned an unreadable answer.", 502, "invalid_response", true);
  if (env.error) throw upstream(`Hypa could not run ${tool}.`, 502, "tool_error", false);

  const result = obj(env.result);
  if (!result) throw upstream("Hypa returned an empty answer.", 502, "invalid_response", true);
  let out = obj(result.structuredContent);
  if (!out) {
    const content = Array.isArray(result.content) ? result.content : [];
    const text = content.map(obj).find((c) => c?.type === "text")?.text;
    if (typeof text === "string") {
      try {
        out = obj(JSON.parse(text)) ?? { error: text };
      } catch {
        out = { error: text };
      }
    }
  }
  if (result.isError === true) {
    if (out?.code === "INSUFFICIENT_BALANCE") throw providerBalanceError();
    throw upstream(`Hypa could not run ${tool}.`, 502, "tool_error", false);
  }
  if (!out) throw upstream("Hypa returned an empty answer.", 502, "invalid_response", true);
  return out;
}

/** Calls Hypa's public API. The key is read per call, sent only in `x-api-key`, and never logged or returned. */
export class HypaHttpClient implements HypaApi {
  constructor(
    private readonly opts: {
      baseUrl: string;
      apiKey: () => string;
      defaultChatModel?: () => string;
      fetchImpl?: typeof fetch;
    },
  ) {}

  private get fetch() {
    return this.opts.fetchImpl ?? fetch;
  }

  private async post(path: string, body: unknown, timeout: number | AbortSignal): Promise<string> {
    try {
      const res = await this.fetch(this.opts.baseUrl.replace(/\/+$/, "") + path, {
        method: "POST",
        redirect: "error",
        headers: {
          "content-type": "application/json",
          accept: "application/json, text/event-stream",
          "x-api-key": this.opts.apiKey(),
        },
        body: JSON.stringify(body),
        signal: typeof timeout === "number" ? AbortSignal.timeout(timeout) : timeout,
      });
      const text = await res.text();
      if (!res.ok) throw mapHttpError(res.status, text);
      return text;
    } catch (e) {
      throw asUpstream(e);
    }
  }

  /** MCP tools/call (stateless Streamable HTTP). */
  private async tool(name: string, args: Record<string, unknown>, timeout: number | AbortSignal) {
    const body = await this.post(
      "/v2/mcp",
      { jsonrpc: "2.0", id: 1, method: "tools/call", params: { name, arguments: args } },
      timeout,
    );
    return parseToolResult(body, name);
  }

  async tts(i: Parameters<HypaApi["tts"]>[0]) {
    const signal = AbortSignal.timeout(TIMEOUT_MS.tts);
    const requestedVoice = i.voice?.trim();
    let model = i.model?.trim() || "auto";
    let voice = requestedVoice?.toLowerCase() === "auto" ? undefined : requestedVoice;

    if (voice && model.toLowerCase() === "auto") {
      const match = await this.resolveVoice(i.language, voice, signal);
      model = match.model;
      voice = match.voice;
    }

    const out = await this.tool(
      "text_to_speech",
      { text: i.text, language: i.language, model, voice, format: "base64" },
      signal,
    );
    const audioBase64 = str(out.audioBase64);
    const mime = str(out.mime);
    const spokenVoice = str(out.voice) ?? voice;
    const spokenModel = str(out.model);
    if (!audioBase64 || !mime || !spokenVoice || !spokenModel) {
      throw upstream("Hypa returned incomplete audio.", 502, "invalid_response", true);
    }
    if (Buffer.byteLength(audioBase64, "base64") > MAX_INLINE_AUDIO_BYTES) {
      throw upstream("Hypa returned audio that was too large.", 502, "invalid_response", false);
    }
    return {
      audioBase64,
      mime,
      sampleRate: typeof out.sampleRate === "number" ? out.sampleRate : undefined,
      route: { provider: str(out.provider), model: spokenModel, voice: spokenVoice },
    };
  }

  private async resolveVoice(language: string, voice: string, signal: AbortSignal) {
    const catalog = await this.tool("list_voices", { language }, signal);
    const options: unknown[] = Array.isArray(catalog.options) ? catalog.options : [];
    const matches = options.flatMap((option) => {
      const entry = obj(option);
      const model = str(entry?.model);
      const name = Array.isArray(entry?.voices)
        ? entry.voices.find((value) => typeof value === "string" && value.toLowerCase() === voice.toLowerCase())
        : undefined;
      return model && typeof name === "string" ? [{ model, voice: name }] : [];
    });
    const [match, other] = matches;
    if (!match) throw badRequest("The requested voice is unavailable for that language.");
    if (other) throw badRequest("The requested voice matches multiple models. Specify its model.");
    return match;
  }

  async asr(i: Parameters<HypaApi["asr"]>[0]) {
    const o = await this.tool(
      "speech_to_text",
      { audio: i.audioBase64, audioType: "base64", model: "auto", ...(i.sourceLang && { sourceLang: i.sourceLang }) },
      TIMEOUT_MS.asr,
    );
    if (typeof o.transcript !== "string" || !str(o.model))
      throw upstream("Hypa returned an incomplete transcript.", 502, "invalid_response", true);
    return {
      transcript: o.transcript,
      language: str(o.language),
      route: { provider: str(o.provider), model: str(o.model) },
    };
  }

  async translate(i: Parameters<HypaApi["translate"]>[0]) {
    const o = await this.tool(
      "translate_message",
      { text: i.text, targetLang: i.targetLang, model: "auto", ...(i.sourceLang && { sourceLang: i.sourceLang }) },
      TIMEOUT_MS.translate,
    );
    const translation = str(o.translation);
    if (translation === undefined || !str(o.model))
      throw upstream("Hypa returned no translation.", 502, "invalid_response", true);
    return { translation, language: str(o.language), route: { provider: str(o.provider), model: str(o.model) } };
  }

  /** Developer chat requires a concrete provider and model. */
  async chat(i: Parameters<HypaApi["chat"]>[0]) {
    const selected = i.model ?? this.opts.defaultChatModel?.().trim();
    const separator = selected?.indexOf("/") ?? -1;
    if (!selected || separator < 1 || separator === selected.length - 1 || /\s/.test(selected)) {
      throw badRequest("Chat requires a model in provider/model format.");
    }
    const provider = selected.slice(0, separator);
    const model = selected.slice(separator + 1);
    const raw = await this.post(
      "/v2/developer/chat",
      {
        messages: i.messages,
        stream: false,
        thinking: false,
        max_tokens: i.maxTokens,
        provider,
        model,
      },
      TIMEOUT_MS.chat,
    );
    let res: Record<string, unknown> | undefined;
    try {
      res = obj(JSON.parse(jsonPayloads(raw).at(-1) ?? ""));
    } catch {
      /* handled below */
    }
    if (!res) throw upstream("Hypa returned an unreadable answer.", 502, "invalid_response", true);
    const choices = Array.isArray(res.choices) ? res.choices : [];
    const content = obj(obj(choices[0])?.message)?.content;
    if (typeof content !== "string" || !content.trim())
      throw upstream("Hypa returned no answer.", 502, "invalid_response", true);
    return {
      content,
      route: { provider: str(res.provider) ?? provider, model: str(res.model) ?? model },
    };
  }
}

/** Anything that is not already our error becomes `upstream` (timeout 504, network 502). */
function asUpstream(e: unknown): ProxyError {
  if (e instanceof ProxyError) return e;
  return isTimeout(e) ? timeoutError() : upstream("Could not reach Hypa. Try again.", 502, "unavailable", true);
}
