# Backups and restore runbook (F4-0097)

What exists, what is NOT verified yet, and how to recover. Written 2026-11-07 from the Railway CLI (read-only); items marked **TO CONFIRM** need the dashboard because the CLI has no backup command.

## What we have (confirmed with `railway volume -p <project> -e <environment> list --json`)
| Environment | Postgres service | Volume | Mount | Used / size |
|---|---|---|---|---|
| staging | `Postgres` | `postgres-volume` | `/var/lib/postgresql/data` | ~156 MB / 5000 MB |
| production | `Postgres-10ae` | `postgres-volume-bIPy` | `/var/lib/postgresql/data` | ~155 MB / 5000 MB |

Project `flow4tech-v2`. The database lives on ONE volume per environment. A volume is not a backup: deleting the service or the volume, a bad migration, or a mistaken `DELETE` is replicated instantly and lost with it.

## What Railway offers (CONFIRMED in the dashboard, `Postgres-10ae` > Backups, 2026-11-07)
- The workspace is NOT on the Pro plan. The Backups tab says: "You can view, restore, and delete existing backups. Upgrade to Pro to create new backups, set up automated schedules, or enable point-in-time recovery." and shows "No Backups".
- So today **production has no backup at all**: not a manual one, not a schedule, no point-in-time recovery. The only copy of the data is the live volume.
- Pro would give volume snapshots (manual and scheduled) and point-in-time recovery. A volume snapshot is a whole-volume rollback (no single-table or single-organization recovery) and lives with the same provider.
- Nothing in this repository configures backups either: `.railway/railway.ts` only declares the API service (the databases are `managed: false`).

## Decision needed (owner): how production gets a backup
| Option | Cost | Notes |
|---|---|---|
| **A. Upgrade the workspace to Pro** and turn on daily snapshots (+ PITR if wanted) | Pro subscription | Least work, best RPO with PITR. Still one provider: keep a logical dump too once real customers exist. |
| **B. Scheduled logical dump to a bucket outside Railway** (GitHub Actions cron: `pg_dump` with the OWNER credential over the Postgres public TCP proxy, encrypted with `age`, uploaded to Cloudflare R2 / Backblaze B2 / S3, retention by lifecycle rule) | free or cents per month | Works on the free plan, survives losing the Railway project, allows single-table recovery. Needs: public networking enabled on the production Postgres, three secrets in GitHub (owner URL, `age` public key, bucket credentials), the `age` private key kept offline by the owner. |
| C. Nothing yet | 0 | Acceptable only while production holds no real data. Say so here and revisit with the F4-0096 triggers. |

**Decision (owner, 2026-11-07): option C, nothing for now.** Until A or B is done, production has **RPO = unbounded**.

**Revisit when ANY of these happens (then do B at least, A when paying customers exist):** the first real customer or real data in production, a destructive migration planned, a second API instance (F4-0096 trigger), a paid plan being adopted for another reason (turn on Railway backups then), or a quarterly review (every 3 months from 2026-11-07). Never store a dump or the `age` key in the repository; a GitHub artifact of a PUBLIC repository is downloadable by anyone, so an unencrypted dump must never be an artifact.

## Recovery objectives (proposal, the owner confirms)
- **RPO** (how much data we may lose): target 24 h. NOT met today (no backup exists, see above); met by option A or B with a daily run.
- **RTO** (how long to be back): 4 h, restoring by hand with this runbook.
- Revisit both when real customers arrive (together with F4-0096 triggers).

## Production minimum (tick here when done)
- [x] Plan checked: Pro is required for Railway backups (screenshot of the Backups tab, 2026-11-07).
- [x] Option chosen: **C, nothing for now** (owner decision, 2026-11-07): production holds no real data yet and the project is small. Production has NO backup and the RPO is unbounded; accepted knowingly.
- [ ] Daily backup running in production and one restore proved from it. Retention in use: `____`.
- [ ] Take one MANUAL backup (or dump) before each production delivery that contains a migration (promotion `dev-v2` -> `main-v2`, see deploy.md).

## Restore procedures
**A. Roll the whole production database back (volume backup, only if Pro is on and a copy exists)**: dashboard > `Postgres-10ae` > Backups > pick the copy > restore. Stop the API first (or accept errors during the window), then redeploy so the pre-deploy migrator reruns; migrations already applied stay recorded in `schema_migrations`, so restoring an OLDER snapshot with a NEWER code version makes the migrator apply the missing files again (they are idempotent per file by name and hash). After restoring, run the checks below.

**B. Recover into a separate database to inspect or copy data out (preferred when only some data is lost)**: restore the backup into a NEW Postgres service (or a local container), never over production, then copy the rows needed. Tenant tables are protected by RLS and `FORCE`: read them with the OWNER credential inside a tenant context, or use `psql` as owner for a read-only look.

**C. Logical dump (works anywhere)**, with the OWNER credential (never the app credential):
```
pg_dump --format=custom --no-owner --file=flow4tech.dump "$MIGRATION_DATABASE_URL"
createdb flow4tech_restore
pg_restore --no-owner --dbname=flow4tech_restore flow4tech.dump
```
Roles: `flow4tech_app` is created by migration 0001 and gets its login from the migrator, so on a restored database run the migrator (`dotnet Flow4Tech.Migrator.dll`) with `APP_DB_PASSWORD` to recreate the login and verify the app credential is restricted.

## Checks after ANY restore
1. `GET /health/ready` is green and `commit` is the expected one.
2. Migrator ran clean: `schema_migrations` lists every file in `backend/migrations` with the same SHA-256.
3. RLS intact: the app credential sees zero rows without a tenant context; `RlsArchitectureIntegrationTests` passes against the restored schema (run it pointed at the restored database, or the SQL it uses).
4. Audit chain: `AuditChainVerifier.VerifyAsync(organization)` per organization reports valid. A restore to an older point shows as `Truncated` against the stored anchor: expected, record the incident.
5. Log in with the owner of one organization and open the cash register and a comanda.
6. Record in the audit/incident notes: when, who, which copy, how much data was lost.

## Open items (F4-0098)
- Choose option A, B or C (decision table above) and implement it; with B, the scheduled dump job and its secrets.
- Automated restore test in CI: weekly job that seeds a database, runs `pg_dump`/`pg_restore` into a second database, runs the migrator and the checks 2 to 4. Proves the PROCEDURE; restoring a real production copy remains a manual exercise, schedule one every quarter.
- Off-Railway copy of a logical dump (encrypted, with a retention rule), decision pending.
- Secrets in dumps: a dump contains password hashes, session hashes and audit data. Encrypt it and keep it out of the repository and CI logs.
