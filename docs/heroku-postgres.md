# Heroku + PostgreSQL Deployment

This app should use PostgreSQL in Heroku. Do not use SQLite as the production
database on Heroku because dyno filesystems are ephemeral.

## What Changed

- Heroku/Postgres is detected automatically through `DATABASE_URL`.
- Local development still uses `database/pucfinance.db` unless `DATABASE_URL`,
  `POSTGRES_CONNECTION_STRING`, or `DATABASE_PATH` is set.
- Database tables are created with EF Core `EnsureCreatedAsync()`.
- Initial funds, cash rows, NAV day zero, and assets are seeded through C# code
  so the bootstrap works on both SQLite and PostgreSQL.
- The daily GitHub Actions batch should call the deployed app URL.

## Heroku Setup

Create the app:

```bash
heroku create <app-name>
```

Use the container stack because this repository has a Dockerfile and `heroku.yml`:

```bash
heroku stack:set container -a <app-name>
```

Add PostgreSQL:

```bash
heroku addons:create heroku-postgresql:essential-0 -a <app-name>
```

Heroku will set `DATABASE_URL` automatically. The app reads this variable at
startup and uses PostgreSQL.

Set a token for the daily batch endpoint:

```bash
heroku config:set BATCH_TOKEN=<strong-random-token> -a <app-name>
```

Deploy:

```bash
git push heroku main
```

Open the app:

```bash
heroku open -a <app-name>
```

Check logs:

```bash
heroku logs --tail -a <app-name>
```

## GitHub Actions Batch

The daily workflow calls:

```text
POST <APP_BASE_URL>/api/batch/run
```

Set this repository variable in GitHub:

```text
APP_BASE_URL=https://<app-name>.herokuapp.com
```

Set this repository secret in GitHub with the same value used in Heroku:

```text
BATCH_TOKEN=<strong-random-token>
```

The cron is scheduled at 21:00 UTC, which is 18:00 BRT, Monday through Friday.

## Migrating Existing SQLite Data

The current production source should become PostgreSQL. For a clean start, the
app can create and seed a fresh Postgres database automatically on first boot.

If the current SQLite file contains real trades or NAV history that must be
preserved, export/import the data before opening the Heroku app to users. A
safe migration flow is:

1. Back up `database/pucfinance.db`.
2. Provision Heroku Postgres.
3. Export SQLite tables to CSV or SQL.
4. Import into Heroku Postgres.
5. Run the app and verify:
   - `GET /api/funds`
   - `GET /api/assets`
   - `GET /api/trades/fund/{fundId}`
   - `POST /api/batch/run`

For this project, the tables that represent live state/history are:

- `funds`
- `cash`
- `positions`
- `trades`
- `nav_history`
- `prices`
- `benchmarks`
- `metrics`
- `realized_pnl`
- `assets`
- `position_history`

## Important Production Notes

- Heroku Postgres is the source of truth in production.
- Do not commit `.db`, `.db-wal`, or `.db-shm` files.
- Use Heroku PG backups before risky releases:

```bash
heroku pg:backups:capture -a <app-name>
```

- `POST /api/batch/run` is protected when `BATCH_TOKEN` is set.
- This app still needs user authentication before being exposed broadly. The
  current API still has public write endpoints for funds, trades, and trade
  deletion.
