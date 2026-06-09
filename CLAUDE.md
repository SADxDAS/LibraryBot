# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
dotnet build
dotnet run
```

**One-shot run modes (handled in `Program.cs` before the bot starts):**
```powershell
dotnet run -- dbtest    # probe the PostgreSQL connection (prints full error chain), then exit
dotnet run -- migrate   # one-time data migration from old Google Sheets → PostgreSQL, then exit
```
`migrate` reads tabs `Каталог!A2:F`, `Видачі!A2:I`, `Обмін!A2:D` into memory first (so a read failure never touches the DB), then **wipes and reloads** `Books`/`Borrowings`/`ExchangeLogs` in one transaction (`Services/SheetsMigrationService.cs`). Re-running is safe.

**EF Core migrations:**
```powershell
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

## Required Environment Variables

Create a `.env` file locally (loaded by DotNetEnv at startup):

```
TELEGRAM_BOT_TOKEN=...
ADMIN_IDS=123456789,987654321   # comma-separated Telegram chat IDs
SPREADSHEET_ID=...              # legacy check — still required by startup validation even though Google Sheets is no longer used (also used by the `migrate` mode)

# PostgreSQL — pick ONE form (see connection resolution below):
DATABASE_PUBLIC_URL=postgresql://postgres:PASS@HOST.proxy.rlwy.net:PORT/railway   # preferred; copy from Railway dashboard
# …or individual vars:
PGHOST=...
PGPORT=...
PGDATABASE=railway
PGUSER=postgres
PGPASSWORD=...
# PG_SSL_MODE=Require   # optional override: Disable | Prefer | Require (default Require)
```

`GOOGLE_CREDENTIALS_JSON` (env var) or a `credentials.json` file must also be present — another legacy startup check. On Railway, set the env var; locally, keep the file.

**PostgreSQL connection resolution** (`Data/AppDbContext.BuildConnectionString`), in priority order:
1. `DATABASE_PUBLIC_URL` / `DATABASE_URL` (a `postgresql://user:pass@host:port/db` URL — parsed via `TryBuildFromUrl`).
2. Individual `PGHOST`/`PGPORT`/`PGDATABASE`/`PGUSER`/`PGPASSWORD` vars.
3. Fallback to the old hardcoded proxy (`viaduct.proxy.rlwy.net:53358`).

The connection always appends `Ssl Mode=$PG_SSL_MODE (default Require);Trust Server Certificate=true;Pooling=true;Minimum Pool Size=0;Maximum Pool Size=20;Connection Idle Lifetime=30;Connection Pruning Interval=10;Keepalive=30;Timeout=30;Command Timeout=60` and enables EF Core `EnableRetryOnFailure` (5 retries) for transient Railway proxy drops. **Connection pooling is ON** — it's the main perf lever (opening a fresh remote+SSL connection costs ~2s; pooling reuses warm ones). `Connection Idle Lifetime=30` closes idle connections before the Railway proxy kills them, so the pool never hands out a dead one. **Note:** Railway proxy host+port change on redeploy — prefer `DATABASE_PUBLIC_URL` from the dashboard over hardcoded values, which go stale.

A static ctor in `AppDbContext` sets `Npgsql.EnableLegacyTimestampBehavior=true` because the whole codebase writes `DateTime.Now` (Kind=Local) into `timestamptz` columns; without it Npgsql 6+ throws on every borrow/return.

## Architecture

**Entry point flow:** `Program.cs` validates env vars → calls `GoogleSheetsService.Initialize()` (DB connectivity check) → starts `TelegramBotClient` polling + `DailyReminderService`.

**Update routing (`Handlers/UpdateHandler.cs`):**
1. Inline button presses → `CallbackHandler.HandleAsync`
2. Text matching a registered command trigger → the matching `ICommand.ExecuteAsync`
3. Active `UserState` + admin → `AdminStateHandler.HandleAsync`
4. Active `UserState` → `UserStateHandler.HandleAsync`
5. Fallback → fuzzy book search via `LibraryDisplayService.SearchBooksAsync`

**Command pattern:** Every user action is an `ICommand` (in `Commands/ICommand.cs`). All commands live in `Commands/AppCommands.cs` and are registered in the `_commands` list in `UpdateHandler`. Adding a new command means implementing `ICommand` and appending it to that list.

**Message formatting:** All bot messages use `ParseMode.Html` (not Markdown — legacy Markdown can't reliably escape user input). **Any user-supplied value** (book title, author, genre, reader name, contact) interpolated into an HTML message **must** be wrapped in `TextUtils.EscapeHtml(...)`, otherwise a value containing `<`, `>`, or `&` makes Telegram reject the whole message ("can't parse entities"). `LibraryDisplayService` escapes title/author/genre at extraction. Numbers and static text don't need escaping.

**State machine:** Multi-step flows (borrow, add book, etc.) use the `UserState` enum (`Models/BotModels.cs`). `SessionManager` holds the per-user state and transient session objects (`BorrowSessions`, `AdminBookSessions`, etc.) in `ConcurrentDictionary`s. Session state is in-memory and lost on restart.

**Admin vs user:** `SessionManager.AdminIds` is populated from the `ADMIN_IDS` env var (written once at startup before polling begins, read-only afterward). `KeyboardHelper.GetMenu` returns different keyboards per role. `UpdateHandler` routes admin states to `AdminStateHandler` before falling through to `UserStateHandler`.

**Concurrency, anti-flood & thread safety:** Multiple users are handled safely in parallel; only contended resources serialize.
- `Services/AsyncKeyedLock.cs` — keyed async critical sections (`using (await AsyncKeyedLock.LockAsync(key))`). Same key = serialized, different keys = parallel. Ref-counted so per-key semaphores are freed when idle (no leak from unique keys like request GUIDs).
- `UpdateHandler.HandleUpdateAsync` wraps each update in a **per-user lock** (`user:{chatId}`) so one user's rapid taps/messages are ordered (protects their session state), while different users run concurrently.
- Book-availability mutations run inside a `book:{rowIndex}` critical section; **borrow uses `GoogleSheetsService.TryDecrementAvailableAsync` (atomic check-and-decrement)** to prevent over-lending the last copy. Request approvals run inside a `request:{reqId}` critical section to prevent double-processing (two admins / double-tap).
- `Services/RateLimiter.cs` — application-level anti-flood: per-user token bucket (burst 8, ~1.5 req/s sustained) with escalating cooldowns, plus a global token-bucket backstop. Checked at the top of `HandleUpdateAsync` before any DB work; admins are exempt. First breach → one warning, then silent drop. (Network-layer DDoS is an infra concern, not handled here.)
- `Services/RequestCoalescer.cs` — single-flight de-duplication: identical requests from the same user (same callback data / message text) that arrive while one is in-flight (or within 1s after it finished) are dropped, so a burst of duplicate taps yields **one** response. Pipeline in `HandleUpdateAsync`: rate-limit → coalesce (`TryEnter`/`Exit` in `finally`) → per-user lock → route.
- **Thread-safety invariants:** no mutable static/shared fields hold per-request state (all per-user state lives in `SessionManager`'s `ConcurrentDictionary`s or method locals); every DB call creates its own `AppDbContext` (never shared across threads); `ICommand` singletons are stateless; session reads in double-tap-prone callbacks use `TryGetValue` (not the throwing indexer).

**Data layer:** Despite being named `GoogleSheetsService`, the service now uses EF Core + PostgreSQL exclusively. `Data/AppDbContext.cs` defines the DB context; `Data/DbModels.cs` has the entities (`DbBook`, `DbBorrowing`, `DbPendingRequest`, `DbExchangeLog`). Row indexing uses `rowIndex - 2` throughout to convert from a legacy 1-based spreadsheet row convention.

**Deployment:** Dockerfile builds with `mcr.microsoft.com/dotnet/sdk:10.0` and runs on `mcr.microsoft.com/dotnet/runtime:10.0`. Hosted on Railway; `credentials.json` is written from the `GOOGLE_CREDENTIALS_JSON` env var at startup.
