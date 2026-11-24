import { ASSUMED_AUDIO_BYTES_PER_SECOND } from "../config";

/** Duration of a WAV from its header, or undefined if it is not a readable WAV. Streaming headers (size 0 / 0xFFFFFFFF) use the bytes that follow. */
export function wavSeconds(buf: Buffer): number | undefined {
  if (buf.length < 44 || buf.toString("ascii", 0, 4) !== "RIFF" || buf.toString("ascii", 8, 12) !== "WAVE")
    return undefined;
  let byteRate: number | undefined;
  for (let pos = 12; pos + 8 <= buf.length;) {
    const id = buf.toString("ascii", pos, pos + 4);
    const size = buf.readUInt32LE(pos + 4);
    const data = pos + 8;
    if (id === "fmt " && data + 12 <= buf.length) byteRate = buf.readUInt32LE(data + 8);
    if (id === "data") {
      if (!byteRate) return undefined;
      const bytes = size === 0 || size === 0xffffffff || data + size > buf.length ? buf.length - data : size;
      return bytes / byteRate;
    }
    pos = data + size + (size % 2);
  }
  return undefined;
}

/** Whole seconds to charge: the WAV duration (or a size estimate), never below what the caller claims, at least 1. */
export const audioSeconds = (buf: Buffer, declared = 0) =>
  Math.max(1, Math.ceil(Math.max(wavSeconds(buf) ?? buf.length / ASSUMED_AUDIO_BYTES_PER_SECOND, declared)));
