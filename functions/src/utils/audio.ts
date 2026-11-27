import { execFile } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { promisify } from "node:util";
import { MAX_ASR_SECONDS } from "../config";
import { badRequest, ProxyError } from "../api/errors";

const exec = promisify(execFile);
const sampleRate = 16_000;
const bytesPerSample = 2;
const sampleLimit = MAX_ASR_SECONDS * sampleRate;

/** Decode at most one sample more than the limit, then measure the decoded audio. */
export async function audioSeconds(audio: Buffer): Promise<number> {
  const dir = await mkdtemp(join(tmpdir(), "hypa-asr-"));
  try {
    // A seekable input also handles recordings with their metadata at the end.
    const input = join(dir, "recording");
    await writeFile(input, audio);
    const args = [
      "-nostdin",
      "-v",
      "error",
      "-xerror",
      "-protocol_whitelist",
      "file,pipe",
      "-format_whitelist",
      "wav,mp3,ogg,flac,matroska,webm,mov,aac",
      "-threads",
      "1",
      "-i",
      input,
      "-map",
      "0:a:0",
      "-af",
      "aresample=16000,atrim=end_sample=" + (sampleLimit + 1),
      "-filter_threads",
      "1",
      "-ac",
      "1",
      "-c:a",
      "pcm_s16le",
      "-f",
      "s16le",
      "pipe:1",
    ];
    let output: Buffer;
    try {
      ({ stdout: output } = await exec("ffmpeg", args, {
        encoding: "buffer",
        timeout: 5000,
        maxBuffer: (sampleLimit + 1) * bytesPerSample + 4096,
        windowsHide: true,
        env: { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot },
      }));
    } catch (error) {
      const cause = error as NodeJS.ErrnoException;
      if (cause.code === "ENOENT" || cause.code === "EACCES") {
        throw new ProxyError("internal", 500, "Audio validation is unavailable.");
      }
      throw badRequest("Could not read the recording. Check the audio format and try again.");
    }
    if (output.length === 0) throw badRequest("The recording contains no audio.");
    return Math.ceil(output.length / (bytesPerSample * sampleRate));
  } finally {
    await rm(dir, { recursive: true, force: true });
  }
}
