/**
 * Regras de arquitetura do front (F4-0114), o espelho de backend/tests/Architecture.
 * Rodar na raiz: `pnpm arch:front`. Detalhe e como declarar excecao: docs/agents/ci-gates.md.
 * Excecao so em `forbidden[].to.pathNot`/`from.pathNot` com o motivo ao lado; nunca desligar a regra.
 */
const testes = "\\.(test|spec)\\.(ts|tsx)$|/(e2e|test-setup|test-utils)[./]|/vitest\\.config\\.|/vite\\.config\\.";

/** @type {import('dependency-cruiser').IConfiguration} */
module.exports = {
  forbidden: [
    {
      name: "sem-ciclos",
      severity: "error",
      comment: "Dependencia circular entre arquivos: extraia o que e comum para um terceiro arquivo.",
      from: {},
      to: { circular: true },
    },
    {
      name: "pacote-nao-importa-app",
      severity: "error",
      comment: "design-system e api-client sao base: nunca importam de app nem de control.",
      from: { path: "^frontend/(design-system|packages/api-client)/src" },
      to: { path: "^frontend/(app|control)/" },
    },
    {
      name: "api-client-nao-importa-design-system",
      severity: "error",
      comment: "api-client (contrato gerado) nao conhece UI.",
      from: { path: "^frontend/packages/api-client/src" },
      to: { path: "^frontend/design-system/" },
    },
    {
      name: "app-e-control-isolados",
      severity: "error",
      comment: "app e control sao aplicacoes separadas; o que for comum vai para um pacote.",
      from: { path: "^frontend/app/" },
      to: { path: "^frontend/control/" },
    },
    {
      name: "control-nao-importa-app",
      severity: "error",
      comment: "Ver app-e-control-isolados.",
      from: { path: "^frontend/control/" },
      to: { path: "^frontend/app/" },
    },
    {
      name: "feature-nao-importa-o-shell",
      severity: "error",
      comment: "Rotas e shell importam as features, nunca o contrario.",
      from: { path: "^frontend/app/src/features/" },
      to: { path: "^frontend/app/src/(router|main|Shell)\\.tsx?$" },
    },
    {
      name: "sessao-e-base",
      severity: "error",
      comment: "features/sessao e a base de identidade: todas as features dependem dela, ela de nenhuma.",
      from: { path: "^frontend/app/src/features/sessao/", pathNot: testes },
      to: { path: "^frontend/app/src/features/(?!sessao/)" },
    },
    {
      name: "producao-nao-importa-teste",
      severity: "error",
      comment: "Codigo de producao nao importa arquivo de teste nem helper de teste.",
      from: { path: "^frontend/[^/]+(/packages)?/[^/]*/?src", pathNot: testes },
      to: { path: testes },
    },
    {
      name: "producao-nao-usa-devdependency",
      severity: "error",
      comment: "Pacote de desenvolvimento no bundle de producao.",
      // .d.ts (vite-env) so declara tipos; react no design-system e peer + dev (instalado so para desenvolver).
      from: { path: "/src/", pathNot: [testes, "\\.d\\.ts$"] },
      to: { dependencyTypes: ["npm-dev"], dependencyTypesNot: ["npm-peer"], pathNot: "node_modules/@types/" },
    },
    {
      name: "sem-dependencia-npm-nao-declarada",
      severity: "error",
      comment: "Todo import de pacote precisa estar no package.json de quem importa.",
      from: {},
      to: { dependencyTypes: ["npm-no-pkg", "npm-unknown"] },
    },
    {
      name: "sem-import-irresolvel",
      severity: "error",
      comment: "Import que nao resolve para nada (caminho errado ou pacote nao instalado).",
      from: {},
      to: { couldNotResolve: true, pathNot: "^virtual:" },
    },
  ],
  options: {
    doNotFollow: { path: "node_modules" },
    exclude: { path: "^frontend/(packages/)?[^/]+/(dist|coverage|playwright-report|test-results)/|^frontend/packages/api-client/src/schema\\.ts$" },
    tsPreCompilationDeps: true,
    combinedDependencies: true,
    tsConfig: { fileName: "frontend/app/tsconfig.json" },
    enhancedResolveOptions: { exportsFields: ["exports"], conditionNames: ["import", "require", "node", "default", "types"] },
    reporterOptions: { text: { highlightFocused: true } },
  },
};
