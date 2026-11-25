import type { Route } from "../api/types";
import { asr } from "./asr";
import { chat } from "./chat";
import { translate } from "./translate";
import { tts } from "./tts";

export const routes: Record<string, Route> = { "/tts": tts, "/asr": asr, "/chat": chat, "/translate": translate };

export const routePath = (path: string) => path.replace(/\/+$/, "").replace(/^\/hypa(?=\/|$)/, "") || "/";
