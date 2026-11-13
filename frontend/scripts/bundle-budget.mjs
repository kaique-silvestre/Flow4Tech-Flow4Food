// Bundle budget (F4-0110, F4-0111): measures the gzip size of the built JS and CSS of an app and fails above the limits of its bundle-budget.json.
// The app is split by route (F4-0111): the ENTRY chunk (the script index.html loads) is what every first visit downloads before anything shows, so it has its own limit;
// the total is what the service worker precaches (all chunks), and the largest file keeps any one chunk (entry or screen) from growing back into a monolith.
// Usage (from the app folder, after `vite build`): node ../scripts/bundle-budget.mjs
import { appendFileSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { gzipSync } from "node:zlib";

const dist = join(process.cwd(), "dist");
const budget = JSON.parse(readFileSync(join(process.cwd(), "bundle-budget.json"), "utf8"));

function arquivos(dir) {
  return readdirSync(dir).flatMap((nome) => {
    const caminho = join(dir, nome);
    return statSync(caminho).isDirectory() ? arquivos(caminho) : [caminho];
  });
}

const medidas = arquivos(dist)
  .filter((f) => /\.(js|css)$/.test(f))
  .map((f) => {
    const bytes = readFileSync(f);
    return {
      arquivo: f.slice(dist.length + 1),
      tipo: f.endsWith(".css") ? "css" : "js",
      bruto: bytes.length,
      gzip: gzipSync(bytes, { level: 9 }).length,
    };
  });

const kb = (n) => `${(n / 1024).toFixed(1)} kB`;
const soma = (tipo) => medidas.filter((m) => m.tipo === tipo).reduce((total, m) => total + m.gzip, 0);
const maiorJs = Math.max(0, ...medidas.filter((m) => m.tipo === "js").map((m) => m.gzip));

const html = readFileSync(join(dist, "index.html"), "utf8");
const entradaSrc = /<script[^>]*type="module"[^>]*src="\/([^"]+)"/.exec(html)?.[1];
const entrada = medidas.find((m) => m.arquivo === entradaSrc);
if (!entrada) {
  console.error("Chunk de entrada nao encontrado em dist/index.html.");
  process.exit(1);
}

const checagens = [
  { nome: "JS de entrada (gzip)", valor: entrada.gzip, limite: budget.jsEntradaGzipKb * 1024 },
  { nome: "JS total (gzip)", valor: soma("js"), limite: budget.jsTotalGzipKb * 1024 },
  { nome: "maior arquivo JS (gzip)", valor: maiorJs, limite: budget.jsMaiorArquivoGzipKb * 1024 },
  { nome: "CSS total (gzip)", valor: soma("css"), limite: budget.cssTotalGzipKb * 1024 },
];

const linhas = [
  "| Medida | Atual | Limite |",
  "|---|---|---|",
  ...checagens.map((c) => `| ${c.nome} | ${kb(c.valor)} | ${kb(c.limite)}${c.valor > c.limite ? " ESTOUROU" : ""} |`),
];
console.log(linhas.join("\n"));
console.log("\nArquivos:");
for (const m of medidas.sort((a, b) => b.gzip - a.gzip))
  console.log(`  ${m.arquivo}: ${kb(m.bruto)} bruto, ${kb(m.gzip)} gzip`);

if (process.env.GITHUB_STEP_SUMMARY)
  appendFileSync(process.env.GITHUB_STEP_SUMMARY, `### Orcamento de bundle\n\n${linhas.join("\n")}\n`);
writeFileSync(join(dist, "bundle-report.json"), JSON.stringify({ medidas, checagens }, null, 2));

const estouros = checagens.filter((c) => c.valor > c.limite);
if (estouros.length > 0) {
  console.error(
    `\nOrcamento estourado: ${estouros.map((e) => e.nome).join(", ")}. Reduza o bundle (divisao por rota, import dinamico) ou, com motivo, suba o limite em bundle-budget.json.`,
  );
  process.exit(1);
}
