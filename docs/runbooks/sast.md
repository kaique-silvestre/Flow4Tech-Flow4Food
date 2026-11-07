# SAST runbook (F4-0086)

One scanner at a time, chosen by repository visibility. Both are static analysis; running both only doubles noise and CI time. Much of what a generic scanner would flag is already blocked by `backend/tests/Architecture` (raw SQL, key literals, `new OrganizationContext(`, profile branching).

| Repository | Scanner | Cost |
|---|---|---|
| Public (today) | **CodeQL** (`codeql` job in `.github/workflows/ci.yml`, C# and JavaScript/TypeScript, `build-mode: none`) | free |
| Private | **Semgrep** (OSS CLI, job snippet below) | free; CodeQL on private repos needs GitHub Advanced Security, paid per committer |

## How it is wired
- `codeql` runs only when `github.event.repository.private == false`. When the repo is private the job is skipped and the `gates` aggregator accepts `skipped` (no other job is conditional, so nothing else can hide behind it).
- `codeql-private-notice` prints a `::warning::` on every run while the repo is private, so the lost coverage is visible.
- Findings land in GitHub's Security tab (Code scanning). The job does not fail on findings; triage there. To make findings block merges, add a code scanning rule in the branch ruleset (repo setting, not code).
- First weeks: read the alerts, dismiss false positives with a reason, fix real ones. Only then consider making them blocking.

## If the repository becomes private
1. Confirm the warning appears in CI.
2. Add this job to `ci.yml` (and to `gates.needs`), then delete or leave the `codeql` job (it will skip by itself):

```yaml
  semgrep:
    name: sast (semgrep)
    runs-on: ubuntu-latest
    timeout-minutes: 10
    container: semgrep/semgrep:1.x   # pin an exact version when adopting
    steps:
      - uses: actions/checkout@v4
      - run: semgrep scan --config p/csharp --config p/typescript --config p/owasp-top-ten --error --metrics=off
```

3. Triage the first run before keeping `--error`; use `# nosemgrep: <rule-id> reason` for justified exceptions.
4. Only if a project rule cannot be expressed as an architecture test (for example `dangerouslySetInnerHTML` in the front), add a small custom rules file and pass it with another `--config`.
5. Alternative if GitHub Advanced Security is bought: nothing to change, CodeQL runs again once the repo is private and licensed (remove the `private == false` condition).

## Notes
- Actions are pinned by commit SHA (F4-0091), see "Pinned actions" below.
- CodeQL on pull requests from forks has a read-only token and cannot upload results; it does not apply to this repo's own PRs.

## Trivy (container and configuration, F4-0087)
Job `trivy` in `ci.yml`, independent of repo visibility (free everywhere).
- `trivy config`: Dockerfile and `docker-compose.yml` misconfiguration, HIGH and CRITICAL fail.
- `trivy image`: builds the deployed image (last stage of `Dockerfile`) and scans OS and .NET packages, HIGH and CRITICAL, `--ignore-unfixed` (a CVE with no fix does not block; re-check when a fix ships).
- The e2e seed target and the front are not covered: the seed never ships, the front is static files on Vercel (npm packages go through `pnpm audit`).
- A failing scan caused by the base image (`mcr.microsoft.com/dotnet/aspnet`) is usually fixed by rebuilding: the tag moves to patched layers. If it persists, wait for the Microsoft image update.
- Exception: add the ID to `.trivyignore` with a reason and `exp:` date. Do not lower the severity filter to pass.
- Version is pinned in the job (`TRIVY_VERSION`); bump it deliberately, together with the first green run.
- Not validated locally when added (no Docker daemon); the first CI run is the real test, expect to adjust `.trivyignore` or the job on it.

## SBOM (F4-0089)
SBOM = lista de tudo que vai dentro da imagem (pacotes do SO e do .NET, com versao). O job `trivy` gera `sbom.cdx.json` (formato CycloneDX) e o guarda como artefato `sbom-cyclonedx` por 90 dias em cada execucao.
- Para que serve: quando sair um CVE novo, responder "estamos afetados?" lendo o arquivo da versao em producao (`GIT_SHA` do `/health/ready` -> execucao do CI daquele commit), sem reconstruir a imagem. Tambem serve se um cliente ou auditoria pedir a lista de componentes.
- O que NAO e: nao protege nada sozinho, nao e gate, nao assina nem atesta a origem da imagem (isso seria assinatura/proveniencia, ainda nao adotada).
- Cobre a imagem da API e do migrator (mesma imagem). O front NAO entra ainda: a hospedagem esta indefinida (F4-0084). Pendencia registrada em F4-0090 (`.scratch/governanca/pendencias.md`).
- Como incluir o front depois (o SBOM funciona tambem para o front):
  - Estatico (Vercel ou bucket, sem imagem): `trivy fs --format cyclonedx --output sbom-front.cdx.json .` le o `pnpm-lock.yaml`; guardar como artefato. Para vulnerabilidades, `trivy fs --scanners vuln --severity HIGH,CRITICAL --exit-code 1 .` (complementa o `pnpm audit`).
  - Container (ex.: nginx no Railway): o Dockerfile do front entra em `trivy config`, `trivy image` e no SBOM da imagem, como a API.

## Pinned actions (F4-0091)
Every `uses:` in `.github/workflows` points to a full commit SHA with the version in a trailing comment (`uses: actions/checkout@<sha> # v4.4.0`). A tag can be moved by whoever controls the action's repository (this is how a supply-chain attack on an action works); a SHA cannot.
- Never write `@v4` or `@main` again. Review rule: a `uses:` without a 40-character SHA is a defect.
- Upgrade: find the release tag, resolve its commit (`gh api repos/<owner>/<repo>/commits/<tag> -q .sha`), replace SHA and comment together in every workflow file (`grep -rn "<action>@" .github`). Do it deliberately, with a green CI run.
- Pins are exact versions inside the same major as before. Moving to Node 24 versions is F4-0082 and should be done as part of this procedure.
- `codeql-action` is on v3; GitHub has announced v4, plan the bump with F4-0082.
- Automatic bump PRs: `.github/dependabot.yml` already covers `github-actions`, `nuget`, `npm` and `docker` weekly; it understands SHA plus version comment and image digests. Review those PRs against the CI result, do not auto-merge.

## Pinned images and binaries (F4-0092)
- `Dockerfile`: the three `FROM mcr.microsoft.com/dotnet/...:10.0` lines carry `@sha256:<digest>` (the multi-arch index digest). Same bytes on every build; Dependabot (`docker`) proposes new digests. Note: a pinned digest stops receiving base-image security patches until bumped, so merge those PRs promptly; `trivy image` fails the build if the pinned layers carry a fixable HIGH/CRITICAL.
- Refresh by hand: `curl -sI -H 'Accept: application/vnd.oci.image.index.v1+json' https://mcr.microsoft.com/v2/dotnet/aspnet/manifests/10.0 | grep -i docker-content-digest` (same for `sdk`).
- gitleaks and Trivy are downloaded by version and verified with `sha256sum -c` against the hash published in the release `*_checksums.txt`. To bump: change `*_VERSION` and `*_SHA256` together (hash from the release checksums file, line of the Linux 64-bit tarball). Not verified: the release signature (Trivy publishes a sigstore bundle); the checksum protects against a swapped tarball only if the release itself is trusted.
- Trivy `0.65.0` (first version of the job) did not exist; the job now uses `0.76.0`. Waiting 2+ weeks before adopting a brand-new release is deliberate.

## DAST with OWASP ZAP (F4-0093)
SAST reads the code; DAST attacks the RUNNING app from outside, like a client would. Job `dast` starts the real API with `docker compose`, then runs `zap-api-scan.py` (ZAP image pinned by digest) with the committed `backend/src/Api/openapi.json` as the list of routes.
- **Safe mode (`-S`)**: passive checks only (headers, cookies, CORS, error pages, information leaks). No attack payloads, no writes. An active scan would hammer login (lockouts, CAPTCHA) and mutate the throwaway database; do it deliberately, never by default in CI.
- **Unauthenticated**: it exercises public routes and the 401/403 answers. It does not see behaviour behind a session. Authenticated scanning means logging in with the e2e seed (`docker compose --profile e2e run --rm seed`), and passing the token header to ZAP (`-z "-config replacer..."`); not done yet.
- **Result**: exit 0 clean, 2 warnings (job passes, read the `zap-report` artifact: `report.html`/`report.json`), 1 a rule configured as FAIL, 3 scan error. To make a finding blocking or silence a false positive, add a rules file (`-c .zap/rules.tsv`, lines `<rule id><TAB>FAIL|WARN|IGNORE<TAB>comment`) to the command in `ci.yml`.
- First weeks: read the reports, fix real findings (missing `X-Content-Type-Options`, `Cache-Control` on sensitive responses, verbose errors), then promote the stable ones to FAIL.
- Cost: builds the image and the stack once more (about 3 to 5 minutes). The Playwright job shares nothing with it.
- Not validated when added (no Docker daemon locally); the first CI run is the real test. `--network host` makes ZAP reach the API on `127.0.0.1:8080` on the GitHub runner (Linux).

## Security headers (F4-0094)
**API** (`backend/src/Api/SecurityHeaders.cs`, first middleware of the host, covers every response including 401/403/404/500; test `SecurityHeadersTests`):
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Permissions-Policy` with camera, microphone, geolocation and payment off.
- `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`: the API serves JSON only, so nothing may be loaded or framed from it.
- `Cache-Control: no-store` unless a route sets its own (responses carry tokens and tenant data).
- `Strict-Transport-Security: max-age=31536000` only when the request is HTTPS. Behind Railway the host sees plain HTTP unless `Flow4Tech__Rede__ProxyConfiavel=true` (then `X-Forwarded-Proto` is honored): confirm after the first deploy with `curl -sI https://<api>/version | grep -i strict`. Deliberately no `includeSubDomains` and no `preload`: they commit the whole domain and wait for the domain decision (F4-0084).
- The dev OpenAPI document (`/openapi/v1.json`) is JSON too, so the same policy applies.
- A route that must be cached sets `Cache-Control` itself (the middleware only adds it when absent). A route that needs a different CSP (none today) would have to be an explicit exception in the middleware, with a test.

**Front** (`frontend/app/vercel.json` and `frontend/control/vercel.json`): `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy: strict-origin-when-cross-origin`, `Permissions-Policy`, and a CSP that only has `frame-ancestors 'none'`. Vercel adds HSTS on its own domains. The full CSP (`script-src`, `connect-src`...) waits for the hosting and API origin decision: F4-0095 in `.scratch/governanca/pendencias.md`. If the front moves to another host, copy the same headers to its configuration.
- ZAP (`dast` job) reads these headers on the API; a regression shows as a warning in the report, the unit test fails earlier.

### Trivy exceptions in force
- `CVE-2026-109518` (apt in the aspnet base image), until 2026-12-07: first CI run (2026-11-07) failed on it; the pinned digest is the newest MCR had and carries no fix. Not exploitable at runtime (apt never runs). Drop the exception once a bumped digest passes.
