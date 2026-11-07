import { defineRailway, github, project, service } from "railway/iac";
// This repository manages only its own resources in the environment. Other
// repositories export their own partial name.
// See https://docs.railway.com/infrastructure-as-code#multi-repo-projects
export const partial = "flow4tech-api";
export default defineRailway((ctx) => {
  // staging follows dev-v2; production follows main-v2 (ADR-016). Railway waits for the GitHub checks (the ci `gates` job) before deploying.
  const branch = ctx.isEnvironment("production") ? "main-v2" : "dev-v2";
  const flow4techApi = service("flow4tech-api", {
    source: github("kaique-silvestre/Flow4Tech-Flow4Food", { branch, checkSuites: true }),
    build: { buildEnvironment: "V3", builder: "DOCKERFILE", dockerfilePath: "Dockerfile" },
    healthcheck: "/health/ready",
    healthcheckTimeout: 60,
    preDeploy: "dotnet Flow4Tech.Migrator.dll",
    replicas: { "iad": 1 },
  });

  return project("flow4tech-v2", {
    variables: { managed: false },
    resources: [flow4techApi],
  });
});
