import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";
import { describe, expect, it } from "vitest";

describe("offline admin shell", () => {
  it("precaches every local JavaScript dependency, including transitive imports", async () => {
    const worker = await readFile(new URL("../public/service-worker.js", import.meta.url), "utf8");
    const shell: string[] = Array.from(runInNewContext(`${worker}\nAPP_SHELL`, {
      self: { addEventListener() {} },
    }));
    const visited = new Set<string>();
    const queue = ["/admin-static/admin.js"];
    while (queue.length) {
      const pathname = queue.shift()!;
      if (visited.has(pathname)) continue;
      visited.add(pathname);
      expect(shell, `Missing offline dependency: ${pathname}`).toContain(pathname);
      const source = await readFile(new URL(`../public/${pathname.replace("/admin-static/", "")}`, import.meta.url), "utf8");
      for (const match of source.matchAll(/\bimport\s+(?:[^;]*?\s+from\s+)?["'](\.[^"']+)["']/g)) {
        queue.push(new URL(match[1], `https://example.test${pathname}`).pathname);
      }
    }
    expect(visited.has("/admin-static/windows.js")).toBe(true);
    expect(visited.has("/admin-static/live-details.js")).toBe(true);
  });
});
