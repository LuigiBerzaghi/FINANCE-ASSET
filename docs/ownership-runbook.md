# Ownership and Replication Runbook

This document maps what is required to take full ownership of this project and
replicate it under a new owner account.

## Current Architecture

- Backend: ASP.NET Core 8 Web API in `src/PUCFinance.AssetManagement`.
- Frontend: React/Vite in `frontend`.
- Database: SQLite.
- Deployment target: Docker on Railway, configured by `Dockerfile` and `railway.toml`.
- External data sources:
  - Yahoo Finance through `OoplesFinance.YahooFinanceAPI`.
  - Banco Central do Brasil public SGS API for CDI.
- Automation: GitHub Actions workflow at `.github/workflows/daily_update.yml`.

No application secrets or API keys are currently required by the codebase.

## Ownership Surfaces

To have full control, the new owner must control all of these surfaces:

1. GitHub repository
   - Current remote: `https://github.com/ThomasJKobayashi/PUCFinance-AssetManagement.git`.
   - Transfer this repository to the new GitHub owner, or create a new repository
     and update the local `origin`.
   - Enable GitHub Actions on the new repository.

2. Railway project
   - Create a Railway project under the new owner account.
   - Connect it to the new GitHub repository.
   - Deploy from the repository root using the existing `Dockerfile`.
   - Create a persistent Railway volume mounted at `/data`.

3. SQLite production database
   - In Railway, the app uses `/data/pucfinance.db` when `RAILWAY_ENVIRONMENT`
     is present.
   - The `/data` directory must be backed by a persistent volume.
   - Back up this file before any migration, redeploy strategy change, or transfer.

4. Local database
   - Locally, `Program.cs` expects `database/pucfinance.db`.
   - The current repository has `database/schema.sql` and `database/seed.sql`.
   - The committed SQLite file is currently at `src/database/pucfinance.db`.
   - This mismatch should be corrected before treating the repository as
     reproducible.

5. Domain and access
   - If a public URL is needed, assign the Railway domain or configure a custom
     domain under the new owner account.
   - The API currently has no authentication, so public write endpoints should
     not be exposed without an access-control change.

## Known Takeover Risks

### Database path mismatch

The application and automation refer to `database/pucfinance.db`, but the tracked
database file currently lives at `src/database/pucfinance.db`.

Evidence:

- `Program.cs` local path: `database/pucfinance.db`.
- GitHub Actions commit step: `git add database/pucfinance.db`.
- Tracked file: `src/database/pucfinance.db`.
- `database/` currently contains only `schema.sql` and `seed.sql`.

Required fix:

- Pick one canonical local path, preferably `database/pucfinance.db`.
- Move or recreate the database there.
- Keep WAL/SHM files out of Git.
- Update documentation and automation to use the same path.

### Batch workflow mismatch

The workflow runs:

```bash
dotnet run --project src/PUCFinance.AssetManagement -- --batch
```

Current `Program.cs` does not branch on `--batch`, so this command starts the
web application instead of running the batch and exiting.

Required fix:

- Add CLI handling for `--batch`, resolve `BatchService`, run
  `RunDailyUpdateAsync()`, then exit with an appropriate status code.
- Or change the workflow to call the deployed API endpoint `POST /api/batch/run`
  with authentication.

### Missing automatic seed

The app currently calls `EnsureCreatedAsync()`, but does not execute
`database/seed.sql`.

Required fix:

- Add a controlled seed/bootstrap path, or create a documented one-command
  database initialization procedure.
- Ensure fresh Railway volumes and fresh local clones produce the expected funds,
  cash rows, NAV day zero, and asset catalog.

### Public write access

The API currently allows unauthenticated write operations:

- `POST /api/funds`
- `POST /api/trades`
- `DELETE /api/trades/{id}`
- `POST /api/batch/run`

Required fix before public production:

- Add authentication/authorization for write and batch endpoints.
- At minimum, protect administrative endpoints with a server-side token.

## Recommended Replication Procedure

1. Transfer or fork the repository into the new owner account.
2. Update local Git remote:

```bash
git remote set-url origin https://github.com/<new-owner>/<new-repo>.git
```

3. Fix the canonical SQLite path:

```text
database/pucfinance.db
```

4. Ensure a fresh local clone can initialize the database from schema and seed.
5. Fix the daily batch workflow so it runs and exits.
6. Add authentication for public write endpoints.
7. Create a new Railway project under the new owner.
8. Connect Railway to the new repository.
9. Add a persistent volume mounted at `/data`.
10. Deploy the Dockerfile build.
11. Verify the deployed API:

```text
GET /api/funds
GET /api/assets
POST /api/batch/run
```

12. Verify persistence:
    - Create or modify data.
    - Redeploy.
    - Confirm the data remains present.

13. Enable or test the GitHub Actions daily workflow.

## Minimum Production Checklist

- [ ] GitHub repository is owned by the new owner.
- [ ] `origin` points to the new repository.
- [ ] Railway project is owned by the new owner.
- [ ] Railway deploys from the new repository.
- [ ] Railway persistent volume is mounted at `/data`.
- [ ] Production database exists at `/data/pucfinance.db`.
- [ ] Local database path is consistent with code and workflow.
- [ ] Fresh clone can create and seed the database.
- [ ] Daily batch runs successfully and exits.
- [ ] Public write endpoints are protected.
- [ ] Database backup process is defined.
- [ ] README reflects the real setup and deploy flow.

## Backup Guidance

SQLite ownership depends on the `.db` file, not on an external managed database
service. Back up the production file regularly.

For Railway, export or copy:

```text
/data/pucfinance.db
```

Do not treat `pucfinance.db-wal` or `pucfinance.db-shm` as source files in Git.
They are SQLite runtime files and should be excluded from version control.

