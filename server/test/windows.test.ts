import { describe, expect, it } from "vitest";
import { parseWindows } from "../public/windows.js";

describe("admin window input", () => {
  it("pads single-digit hours before saving", () => {
    expect(parseWindows("8:00-10:00")).toEqual([{ from: "08:00", to: "10:00" }]);
    expect(parseWindows("6:00-8:00")).toEqual([{ from: "06:00", to: "08:00" }]);
  });

  it("preserves padded hours and handles multiple windows and whitespace", () => {
    expect(parseWindows(" 08:00 - 10:00, 16:00-18:00 ")).toEqual([
      { from: "08:00", to: "10:00" },
      { from: "16:00", to: "18:00" },
    ]);
    expect(parseWindows(" ")).toEqual([]);
  });

  it.each(["8:0-10:00", "8:60-10:00", "8:00-", "8:00-10:00-12:00"])("rejects malformed input %s", value => {
    expect(() => parseWindows(value)).toThrow("Bad window format");
  });
});
