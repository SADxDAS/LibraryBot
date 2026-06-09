using Microsoft.EntityFrameworkCore;
using System;

namespace LibraryBot.Data
{
    public class AppDbContext : DbContext
    {
        public DbSet<DbBook> Books { get; set; }
        public DbSet<DbBorrowing> Borrowings { get; set; }
        public DbSet<DbPendingRequest> PendingRequests { get; set; }
        public DbSet<DbExchangeLog> ExchangeLogs { get; set; }

        static AppDbContext()
        {
            // Npgsql 6+ за замовчуванням пише в 'timestamp with time zone' лише DateTime з Kind=Utc,
            // а весь код проєкту використовує DateTime.Now (Kind=Local). Legacy-режим повертає стару
            // поведінку: Local конвертується в UTC, Unspecified трактується як UTC. Інакше бот падав би
            // на першій же видачі/поверненні книги. Має бути викликано ДО першої операції Npgsql.
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        public AppDbContext() { }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                // Завантажуємо файл .env
                DotNetEnv.Env.Load();

                string conn = BuildConnectionString();

                optionsBuilder.UseNpgsql(conn, npgsql =>
                {
                    // Railway-проксі час від часу розриває TCP-з'єднання (SocketException).
                    // Стратегія повторних спроб EF Core автоматично повторює такі транзієнтні збої.
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                });
            }
        }

        /// <summary>
        /// Збирає рядок підключення. Пріоритет джерел (Railway-сумісно):
        ///   1. DATABASE_PUBLIC_URL / DATABASE_URL  (postgresql://user:pass@host:port/db) — найнадійніше, копіюється з дашборду Railway.
        ///   2. Окремі змінні PGHOST/PGPORT/PGDATABASE/PGUSER/PGPASSWORD.
        ///   3. Старі захардкоджені значення проксі (fallback для сумісності).
        /// Хост/порт проксі Railway змінюються при передеплої — тому їх краще тримати в .env, а не в коді.
        /// </summary>
        private static string BuildConnectionString()
        {
            // SSL-режим можна перевизначити через PG_SSL_MODE (Disable/Prefer/Require). За замовчуванням Require.
            string sslMode = Environment.GetEnvironmentVariable("PG_SSL_MODE") ?? "Require";

            // ПУЛІНГ УВІМКНЕНО — головний прискорювач: без нього КОЖЕН запит відкривав нове
            // фізичне з'єднання до віддаленого проксі Railway (+ SSL-рукостискання). Тепер з'єднання
            // переюзуються. "Connection Idle Lifetime=30" закриває простої РАНІШЕ, ніж їх уб'є проксі
            // Railway, тож із пулу не дістанеться "мертве" з'єднання; Keepalive тримає активні живими,
            // а EnableRetryOnFailure + ретраї страхують від рідкісного розриву.
            string common = $"Ssl Mode={sslMode};Trust Server Certificate=true;" +
                            "Pooling=true;Minimum Pool Size=0;Maximum Pool Size=20;" +
                            "Connection Idle Lifetime=30;Connection Pruning Interval=10;" +
                            "Keepalive=30;Timeout=30;Command Timeout=60;";

            string? url = Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL")
                       ?? Environment.GetEnvironmentVariable("DATABASE_URL");

            if (!string.IsNullOrWhiteSpace(url) && TryBuildFromUrl(url, out string fromUrl))
                return fromUrl + common;

            string host = Environment.GetEnvironmentVariable("PGHOST") ?? "viaduct.proxy.rlwy.net";
            string port = Environment.GetEnvironmentVariable("PGPORT") ?? "53358";
            string database = Environment.GetEnvironmentVariable("PGDATABASE") ?? "railway";
            string user = Environment.GetEnvironmentVariable("PGUSER") ?? "postgres";
            string pass = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "";

            return $"Server={host};Port={port};Database={database};User Id={user};Password={pass};" + common;
        }

        /// <summary>Парсить postgres-URL (postgresql://user:pass@host:port/db) у базовий Npgsql-рядок.</summary>
        private static bool TryBuildFromUrl(string url, out string connection)
        {
            connection = "";
            try
            {
                if (!url.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)) return false;

                var uri = new Uri(url);
                var userInfo = uri.UserInfo.Split(':', 2);
                string user = Uri.UnescapeDataString(userInfo[0]);
                string pass = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
                string host = uri.Host;
                int port = uri.Port > 0 ? uri.Port : 5432;
                string database = uri.AbsolutePath.Trim('/');
                if (string.IsNullOrEmpty(database)) database = "railway";

                connection = $"Server={host};Port={port};Database={database};User Id={user};Password={pass};";
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}