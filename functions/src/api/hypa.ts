import { MAX_INLINE_AUDIO_BYTES, TIMEOUT_MS } from "../config";
import { jsonPayloads } from "../utils/sse";
import { isTimeout, mapHttpError, providerBalanceError, ProxyError, timeoutError, upstream } from "./errors";

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
    inline?: boolean;
  }): Promise<{ audioUrl?: string; audioBase64?: string; mime?: string; sampleRate?: number; route: Route }>;

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
  constructor(private readonly opts: { baseUrl: string; apiKey: () => string; fetchImpl?: typeof fetch }) {}

  private get fetch() {
    return this.opts.fetchImpl ?? fetch;
  }

  private async post(path: string, body: unknown, timeoutMs: number): Promise<string> {
    try {
      const res = await this.fetch(this.opts.baseUrl.replace(/\/+$/, "") + path, {
        method: "POST",
        headers: {
          "content-type": "application/json",
          accept: "application/json, text/event-stream",
          "x-api-key": this.opts.apiKey(),
        },
        body: JSON.stringify(body),
        signal: AbortSignal.timeout(timeoutMs),
      });
      const text = await res.text();
      if (!res.ok) throw mapHttpError(res.status, text);
      return text;
    } catch (e) {
      throw asUpstream(e);
    }
  }

  /** MCP tools/call (stateless Streamable HTTP). */
  private async tool(name: string, args: Record<string, unknown>, timeoutMs: number) {
    const body = await this.post(
      "/v2/mcp",
      { jsonrpc: "2.0", id: 1, method: "tools/call", params: { name, arguments: args } },
      timeoutMs,
    );
    return parseToolResult(body, name);
  }

  async tts(i: Parameters<HypaApi["tts"]>[0]) {
    // No voice = Hypa's auto router. A named voice only works with the model that owns it, so it travels with its model.
    const named = !!i.voice && i.voice.toLowerCase() !== "auto" && !!i.model;
    const o = await this.tool(
      "text_to_speech",
      { text: i.text, language: i.language, model: named ? i.model : "auto", ...(named && { voice: i.voice }) },
      TIMEOUT_MS.tts,
    );
    const out = {
      audioUrl: str(o.audioUrl),
      audioBase64: str(o.audioBase64),
      mime: str(o.mime),
      sampleRate: typeof o.sampleRate === "number" ? o.sampleRate : undefined,
      route: { provider: str(o.provider), model: str(o.model), voice: str(o.voice) },
    };
    if (!out.audioUrl && !out.audioBase64) throw upstream("Hypa returned no audio.", 502, "invalid_response", true);
    if (i.inline && !out.audioBase64 && out.audioUrl) {
      out.audioBase64 = await this.download(out.audioUrl);
      out.audioUrl = undefined;
    }
    return out;
  }

  /** Fetch Hypa's signed audio link on the server so a browser makes no cross-origin request. https only; no key is sent. */
  private async download(url: string): Promise<string> {
    try {
      const u = new URL(url);
      if (u.protocol !== "https:") throw upstream("Hypa returned a bad audio link.", 502, "invalid_response", false);
      const res = await this.fetch(u, { signal: AbortSignal.timeout(TIMEOUT_MS.tts) });
      if (!res.ok) throw upstream("The audio link did not work.", 502, "unavailable", true);
      const buf = Buffer.from(await res.arrayBuffer());
      if (buf.length > MAX_INLINE_AUDIO_BYTES)
        throw upstream("The audio was too large to send inline.", 502, "invalid_response", false);
      return buf.toString("base64");
    } catch (e) {
      throw asUpstream(e);
    }
  }

  async asr(i: Parameters<HypaApi["asr"]>[0]) {
    const o = await this.tool(
      "speech_to_text",
      { audio: i.audioBase64, audioType: "base64", ...(i.sourceLang && { sourceLang: i.sourceLang }) },
      TIMEOUT_MS.asr,
    );
    return {
      transcript: str(o.transcript) ?? "",
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
    if (translation === undefined) throw upstream("Hypa returned no translation.", 502, "invalid_response", true);
    return { translation, language: str(o.language), route: { provider: str(o.provider), model: str(o.model) } };
  }

  /**
   * Chat over REST (POST /v2/developer/chat, not streamed). "auto"/omitted lets Hypa route; "provider/model" pins one (the form the
   * Unity client verified live). UNVERIFIED: the auto form (provider "auto", no model) mirrors the REST mt call.
   */
  async chat(i: Parameters<HypaApi["chat"]>[0]) {
    const pinned = i.model && i.model.toLowerCase() !== "auto" ? i.model.split("/") : undefined;
    const provider = pinned ? pinned[0] : "auto";
    const model = pinned?.[1];
    const raw = await this.post(
      "/v2/developer/chat",
      {
        messages: i.messages,
        stream: false,
        thinking: false,
        max_tokens: i.maxTokens,
        provider,
        ...(model && { model }),
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
      route: { provider: str(res.provider) ?? (pinned ? provider : undefined), model: str(res.model) ?? model },
    };
  }
}

/** Anything that is not already our error becomes `upstream` (timeout 504, network 502). */
function asUpstream(e: unknown): ProxyError {
  if (e instanceof ProxyError) return e;
  return isTimeout(e) ? timeoutError() : upstream("Could not reach Hypa. Try again.", 502, "unavailable", true);
}
