# pg-copy

A fast desktop tool (and CLI) that replicates **remote PostgreSQL databases onto your own Windows machine** —
the whole database: schemas, types, tables, partitions, indexes, constraints, views, materialized views,
functions, triggers, policies, sequences, extensions, large objects and data. It is built for the
"give me a local copy of the dev database" workflow and optimises for one thing: wall-clock time.

* **Desktop app** (Avalonia, Windows) — pick a saved connection, tick databases, watch live progress.
* **`pgcopy` CLI** — the same engine, scriptable.

![Clone tab during a replication](docs/clone-progress.png)

*The Clone tab mid-run (rendered headlessly by the UI tests with sample data).*

## Why it is fast

`pg_dump -Fd -j N | pg_restore -j N` writes every row to disk and reads it back, parallelises only
*between* tables, and starts building indexes only after all data is loaded. pg-copy instead:

| Technique | Effect |
|---|---|
| **Streaming binary `COPY` source → target**, no intermediate files | Rows travel once, never touch disk, no text parsing |
| **Pipelined relay** (read of block *n+1* overlaps write of block *n*) | One stream runs at the speed of the slower side, not the sum of both |
| **Large tables split into `ctid` page ranges**, copied in parallel | The one huge table that dominates most databases uses all cores |
| **Largest-first scheduling** across a worker pool | The long poles start first |
| **Indexes and constraints built the moment their table finishes**, while other tables still copy | Index time hides behind network time |
| **`TRUNCATE` + `COPY … FREEZE` in one transaction** on a `wal_level=minimal` target | Bulk loads write no WAL and rows land pre-frozen |
| **Bulk-load tuning of the managed Docker target** (`fsync`, `full_page_writes`, autovacuum off *during the load only*, then reset + checkpoint) | Removes durability overhead you don't need for a throw-away copy |
| **Trusted foreign keys** — flagged valid without a scan (the rows come from one snapshot where they were already valid) | Turns a full scan per FK into a catalog update |
| **Per-table `ANALYZE` right after indexing**, parallel analyze for the rest | The copy is query-ready immediately |

`pg_dump`/`pg_restore` are used only where they are the reliable tool: the **DDL** (every object type,
exactly as PostgreSQL itself would restore it) and **large objects**.

### Consistency

All workers and `pg_dump` share one exported snapshot (`pg_export_snapshot`), so the clone is a single
point-in-time image even while the source is being written to. The copy is built under a temporary name and
swapped in atomically at the end, so an existing local copy stays usable until the new one is complete and a
failed or cancelled run leaves it untouched.

## Install

Download the zips from the GitHub Releases page (self-contained, no .NET install needed):

* `pg-copy-desktop-<version>-win-x64.zip` → `PgCopy.exe`
* `pg-copy-cli-<version>-win-x64.zip` → `pgcopy.exe`

Requirements: **Docker Desktop** for the managed target (recommended), *or* any local PostgreSQL ≥ the
source's major version. The PostgreSQL client tools (`pg_dump`/`pg_restore`) are auto-detected under
`C:\Program Files\PostgreSQL\*\bin` / `PATH`; if none new enough are found, the copies inside the Docker
container are used.

## Use

### Desktop

1. **Clone** tab → enter the remote host/user/password (save it as a named connection) → **Load databases**.
2. Tick one or more databases (optionally rename the local copy) → **Replicate selected databases**.
3. Passwords are protected with Windows DPAPI (current user). Per-run logs go to
   `%LOCALAPPDATA%\pg-copy\logs`, and the **History** tab shows past runs with per-phase timings.
4. **Snapshots** tab → save/reset instant local save points of a cloned database.

### CLI

```powershell
pgcopy profiles add dev -c "postgres://me:secret@dev-db.corp:5432/postgres?sslmode=require"
pgcopy databases --source profile:dev
pgcopy clone --source profile:dev --db tenant_acme --db tenant_globex      # → managed Docker target
pgcopy clone --source profile:dev --all --target "postgres://postgres:pw@localhost:5432/postgres"
pgcopy clone --source profile:dev --db tenant_acme --as acme_local --exclude-table-data "audit.*"
pgcopy docker up 16                                                        # start the container, print its connection URI
pgcopy snapshot save acme_local fresh -t "postgres://postgres:pw@localhost:5432/postgres"
pgcopy snapshot restore acme_local fresh -t "postgres://postgres:pw@localhost:5432/postgres"
```

| Option | Default | Meaning |
|---|---|---|
| `--jobs` | cores (4–8) | Parallel COPY streams. More than your core count only helps on slow/high-latency links |
| `--split-size` | `128MB` | Tables above this are split into parallel ctid ranges (`off` disables) |
| `--index-jobs` | ½ target CPUs | Parallel index builds |
| `--no-binary` | | Force text COPY (binary is used only for types whose wire format is portable) |
| `--no-early-indexes` | | Build all indexes after the copy instead of during it |
| `--validate-fks` | | Fully validate foreign keys instead of trusting the source |
| `--safe-load` | | Keep `fsync` on during the load (managed Docker target) |
| `--skip-matview-refresh` | | Create materialized views without populating them |
| `--exclude-table-data` | | Skip rows of tables matching a glob (`schema.table`); definitions are still created |
| `--ignore-restore-errors` | | Treat `pg_restore` errors as warnings (default: any error fails the clone) |
| `--no-snapshot` | | Skip the consistent snapshot (required behind transaction poolers) |
| `--image` | `postgres:<major>` | Docker image for the managed target (e.g. `postgis/postgis:16-3.4`) |

### Managed Docker target

One container per major version, `pgcopy-pg<major>`, created on first use with the same major as the source:
bound to `127.0.0.1` only, data in a named volume (never an NTFS bind mount), `--shm-size=1g`,
`wal_level=minimal`, large checkpoint intervals and memory settings derived from Docker's limits. The
superuser password is generated and stored (DPAPI). The app shows the connection string after every clone,
`pgcopy clone` prints it, and `pgcopy docker up 16` prints it on demand. Load-mode settings are reset (and a `CHECKPOINT` issued) when a clone finishes or fails, and
again defensively at the start of the next run.

## What is and isn't copied

Copied: everything in the database — schemas, tables (incl. partitions, inheritance, unlogged), column
defaults/identity/generated columns, all index types, constraints (incl. exclusion and deferrable),
sequences with their current values, enums/domains/composite/range types, views, materialized views
(populated), functions/procedures/triggers/rules/event triggers, row-level-security policies, comments,
extensions, large objects, database-level `ALTER DATABASE … SET` settings, and collation/encoding.

By design **not** copied: roles, passwords, ownership and grants (everything is owned by the local user, like
`--no-owner --no-acl`; placeholder `NOLOGIN` roles are created only where RLS policies reference them),
tablespaces, publications/subscriptions, user mappings and security labels.

## Things to know

* **The source user** needs `SELECT` on everything (and `BYPASSRLS` if row-level security is on). pg-copy
  checks up front and fails with a clear message instead of silently skipping data
  (`--exclude-table-data` skips tables explicitly).
* **Extensions** used by the source must exist on the target. The stock `postgres` image has the contrib set;
  for PostGIS/pgvector etc. set a different image. pg-copy checks this before copying anything.
* **Open snapshot transaction**: while a clone runs, the source holds back vacuum cleanup for that
  period (same as a long `pg_dump`).
* **Poolers**: PgBouncer in transaction mode can't carry the exported snapshot; connect to the database
  directly, or use `--no-snapshot` if a point-in-time-consistent copy isn't required.
* **Over a VPN the link is usually the limit.** PostgreSQL's wire protocol has no compression, so no client
  can beat the bandwidth; parallel streams and splitting large tables are what keep that link full.

## Benchmark

pgbench scale 100 (1.5 GB, one 1.3 GB table + 10 M rows), PostgreSQL 16 source and target on the **same
4-vCPU machine** (so source, target and tool compete for the same cores — a CPU-bound worst case that
flatters nobody), same target configuration for both:

| Method | Time |
|---|---|
| `pg_dump -Fd -j8` + `pg_restore -j8` | 19.3 s (dump 4.2 s + restore 15.0 s) |
| `pgcopy clone` — 8 jobs, 256 MB split | 15.7 s |
| `pgcopy clone` — 4 jobs, 128 MB split | 12.7 s |
| `pgcopy clone` — 6 jobs, 128 MB split (defaults tuned from this) | **12.0 s** |

These are loopback numbers: no network latency or bandwidth was involved, so they say how much CPU/IO the
pipeline wastes, not how it behaves on your VPN. Reproduce with `pgbench -i -s 100` and the commands above.

## Development

```powershell
dotnet tool restore
dotnet build
dotnet test tests/PgCopy.Core.Tests          # unit tests
dotnet test tests/PgCopy.Desktop.Tests       # headless Avalonia UI tests
dotnet csharpier check .                      # formatting (CI enforces it)
```

Integration tests need two real servers (a throw-away source and a target):

```bash
export PGCOPY_IT_SOURCE=postgres://postgres:pw@127.0.0.1:5433/postgres
export PGCOPY_IT_TARGET=postgres://postgres:pw@127.0.0.1:5434/postgres
dotnet test tests/PgCopy.IntegrationTests
```

They seed a "schema zoo" (`tests/PgCopy.IntegrationTests/Seed/zoo.sql`: enums, domains, composites, ranges,
extensions, partitions, inheritance, RLS, circular FKs, EXCLUDE constraints, generated/identity columns,
matviews, large objects, quoted identifiers, a dropped column, …), clone it with several option
combinations and compare source and target with `Scripts/compare.sh` — schema (`pg_dump -s`), per-table row
count + MD5 of every row, sequences, large objects, constraint validity, matview state, database settings.

Layout: `src/PgCopy.Core` (engine), `src/PgCopy.Cli`, `src/PgCopy.Desktop`, `tests/*`.
