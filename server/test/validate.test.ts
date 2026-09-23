import { describe, it, expect } from "vitest";
import { validateDays, defaultDays } from "../src/types.js";

describe("validateDays", () => {
  it("accepts default 1440 no windows", () => {
    expect(validateDays(defaultDays())).toBeNull();
  });
  it("accepts windows", () => {
    const d = defaultDays();
    d["3"] = { limit_min: 120, windows: [{ from: "16:00", to: "20:00" }] };
    expect(validateDays(d)).toBeNull();
  });
  it("rejects overnight", () => {
    const d = defaultDays();
    d["3"] = { limit_min: 120, windows: [{ from: "22:00", to: "01:00" }] };
    expect(validateDays(d)).toMatch(/from < to/);
  });
  it("rejects overlap", () => {
    const d = defaultDays();
    d["1"] = { limit_min: 120, windows: [{ from: "10:00", to: "12:00" }, { from: "11:30", to: "13:00" }] };
    expect(validateDays(d)).toMatch(/overlap/);
  });
  it("rejects bad limit", () => {
    const d = defaultDays();
    d["1"] = { limit_min: 2000, windows: [] };
    expect(validateDays(d)).toMatch(/limit_min/);
  });
});
