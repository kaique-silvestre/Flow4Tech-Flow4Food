import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

const raiz = resolve(__dirname, "../../..");
const vercel = JSON.parse(readFileSync(resolve(raiz, "frontend/app/vercel.json"), "utf8")) as {
  rewrites: { source: string; destination: string }[];
};
const openapi = JSON.parse(readFileSync(resolve(raiz, "backend/src/Api/openapi.json"), "utf8")) as {
  paths: Record<string, unknown>;
};

describe("vercel.json routes the API before the catch-all", () => {
  const indiceDoCoringa = vercel.rewrites.findIndex((r) => r.destination === "/index.html");
  const prefixos = [...new Set(Object.keys(openapi.paths).map((p) => p.split("/")[1] ?? ""))].filter(Boolean);

  it("has a catch-all, and every API rewrite comes before it", () => {
    expect(indiceDoCoringa).toBeGreaterThan(0);
    const daApi = vercel.rewrites.filter((r) => r.destination.startsWith("https://"));
    expect(daApi.length).toBeGreaterThan(0);
    expect(vercel.rewrites.indexOf(daApi.at(-1) as never)).toBeLessThan(indiceDoCoringa);
  });

  it.each(prefixos)("rewrites /%s to the API (the path and its sub-paths)", (prefixo) => {
    const antes = vercel.rewrites.slice(0, indiceDoCoringa);
    const cobre = (caminho: string) =>
      antes.some((r) => {
        const grupo = /^\/\(([^)]*)\)/.exec(r.source)?.[1]?.split("|") ?? [];
        const comSub = r.source.endsWith("/:path*");
        return grupo.includes(prefixo) && (caminho === `/${prefixo}` ? !comSub : comSub);
      });
    expect(cobre(`/${prefixo}`)).toBe(true);
    expect(cobre(`/${prefixo}/x`)).toBe(true);
  });
});
