import functionsTest from "firebase-functions-test";
import { afterAll, beforeEach, describe, expect, it, vi } from "vitest";
import { ROOMS_MAX_AGE_MS } from "../src/config";
import { deleteStaleRooms, type RoomsDb } from "../src/services/rooms";

/** An in-memory /rooms honouring orderByChild/endAt/limitToFirst like the Admin SDK. */
function fakeDb(rooms: Record<string, any>) {
  const db: RoomsDb = {
    ref: () => ({
      orderByChild: (field: string) => ({
        endAt: (max: number) => ({
          limitToFirst: (n: number) => ({
            get: async () => {
              const keys = Object.keys(rooms)
                .filter((k) => typeof rooms[k]?.[field] === "number" && rooms[k][field] <= max)
                .sort((a, b) => rooms[a][field] - rooms[b][field])
                .slice(0, n);
              return {
                exists: () => keys.length > 0,
                forEach: (cb: (c: { key: string }) => void) => {
                  keys.forEach((key) => cb({ key }));
                  return false;
                },
              };
            },
          }),
        }),
      }),
      update: async (values: Record<string, null>) => {
        for (const k of Object.keys(values)) delete rooms[k];
      },
    }),
  };
  return db;
}

// hoisted: vi.mock runs before imports, so the shared state it closes over must be hoisted too
const shared = vi.hoisted(() => ({ rooms: {} as Record<string, any>, make: undefined as undefined | (() => unknown) }));
vi.mock("firebase-admin/database", () => ({ getDatabase: () => shared.make!() }));

const NOW = Date.parse("2026-11-24T12:00:00Z");

describe("rooms cleanup", () => {
  it("removes rooms older than 2 hours and keeps newer or malformed ones", async () => {
    const rooms: Record<string, any> = {
      old: { createdAt: NOW - ROOMS_MAX_AGE_MS - 1 },
      older: { createdAt: NOW - 5 * 3600_000 },
      exactly: { createdAt: NOW - ROOMS_MAX_AGE_MS },
      fresh: { createdAt: NOW - 60_000 },
      nodate: { host: "x" },
    };
    expect(await deleteStaleRooms(fakeDb(rooms), NOW)).toBe(3);
    expect(Object.keys(rooms).sort()).toEqual(["fresh", "nodate"]);
  });

  it("works through more than one batch", async () => {
    const rooms: Record<string, any> = {};
    for (let i = 0; i < 450; i++) rooms["r" + i] = { createdAt: NOW - 3 * 3600_000 - i };
    expect(await deleteStaleRooms(fakeDb(rooms), NOW)).toBe(450);
    expect(Object.keys(rooms)).toHaveLength(0);
  });

  it("does nothing when there are no rooms", async () => {
    expect(await deleteStaleRooms(fakeDb({}), NOW)).toBe(0);
  });
});

describe("scheduled function (firebase-functions-test)", () => {
  const rooms = shared.rooms;
  Object.assign(rooms, { gone: { createdAt: 1 }, kept: { createdAt: Date.now() } });
  shared.make = () => fakeDb(rooms);
  const t = functionsTest();
  beforeEach(() => {
    process.env.GCLOUD_PROJECT = "demo-hypa";
  });
  afterAll(() => t.cleanup());

  it("runs the cleanup on its schedule", async () => {
    const { cleanupRooms } = await import("../src/index");
    await t.wrap(cleanupRooms as never)({} as never);
    expect(Object.keys(rooms)).toEqual(["kept"]);
  });
});
