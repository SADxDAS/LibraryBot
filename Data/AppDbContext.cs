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
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        }

        public AppDbContext() { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Настройка связи для таблицы Выдач (DbBorrowing)
            modelBuilder.Entity<DbBorrowing>()
                .HasOne(b => b.Book)
                .WithMany(book => book.Borrowings)
                .HasForeignKey(b => b.BookId)
                .OnDelete(DeleteBehavior.Restrict); // Запретит удалить книгу из каталога, если она сейчас у кого-то на руках

            // Настройка связи для активных Запросов (DbPendingRequest)
            modelBuilder.Entity<DbPendingRequest>()
                .HasOne(r => r.Book)
                .WithMany(book => book.PendingRequests)
                .HasForeignKey(r => r.BookId)
                .OnDelete(DeleteBehavior.SetNull); // Если книга удалена, ссылка в запросе занулится, но запрос останется
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                DotNetEnv.Env.Load();
                string conn = BuildConnectionString();
                optionsBuilder.UseNpgsql(conn, npgsql =>
                {
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorCodesToAdd: null);
                });
            }
        }

        private static string BuildConnectionString()
        {
            string sslMode = Environment.GetEnvironmentVariable("PG_SSL_MODE") ?? "Require";
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
            catch { return false; }
        }
    }
}