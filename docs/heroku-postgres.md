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
- The daily batch runs inside the app (`BatchSchedulerService`): once on startup and at 19:00 Brasilia time, Monday through Friday.

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

## Daily Batch

The app schedules the daily batch itself (`BatchSchedulerService`), so no external cron is needed:

- once about 2 minutes after startup (catches up if the dyno restarted or missed the time);
- every weekday at 19:00 Brasilia time.

Business days without NAV (e.g. while the app was down) are filled from historical closes on the next run.

Optional config var:

```bash
heroku config:set BATCH_SCHEDULE="0 19 * * 1-5" -a <app-name>   # cron, Brasilia time
heroku config:set BATCH_SCHEDULE=off -a <app-name>              # disable
```

This requires a dyno that does not sleep (Basic or higher). `POST /api/batch/run` still runs it on demand
(the "Run Batch" button, or `X-Batch-Token: <BATCH_TOKEN>` when that config var is set).

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
