import { describe, expect, it } from "vitest";
import { DEFAULT_HYPA_API_BASE, emulatorSettings } from "../src/config";

describe("emulatorSettings", () => {
  const fake = { HYPA_SKIP_APP_CHECK: "true", HYPA_API_BASE: "http://127.0.0.1:9911" };

  it("applies the .env.local settings inside the emulator", () => {
    expect(emulatorSettings({ FUNCTIONS_EMULATOR: "true", ...fake })).toEqual({
      skipAppCheck: true,
      hypaApiBase: "http://127.0.0.1:9911",
    });
  });

  it("ignores them when deployed (no FUNCTIONS_EMULATOR), so App Check stays on and the key only goes to Hypa", () => {
    expect(emulatorSettings(fake)).toEqual({ skipAppCheck: false, hypaApiBase: DEFAULT_HYPA_API_BASE });
  });

  it("needs each setting to be asked for explicitly", () => {
    expect(emulatorSettings({ FUNCTIONS_EMULATOR: "true" })).toEqual({
      skipAppCheck: false,
      hypaApiBase: DEFAULT_HYPA_API_BASE,
    });
  });
});
