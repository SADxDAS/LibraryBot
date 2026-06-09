using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using LibraryBot.Data;
using Microsoft.EntityFrameworkCore;

namespace LibraryBot.Services
{
    /// <summary>
    /// Одноразова міграція даних зі старих Google Sheets у PostgreSQL.
    ///
    /// Запуск:  dotnet run -- migrate
    ///
    /// Стратегія: спершу повністю вичитуємо ВСІ дані з таблиці в пам'ять
    /// (щоб збій читання ніколи не зачепив БД), потім очищаємо таблиці
    /// Books/Borrowings/ExchangeLogs і заливаємо заново. Повторний запуск безпечний.
    /// </summary>
    public static class SheetsMigrationService
    {
        // Той самий scope, що працював у старому GoogleSheetsService.
        private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };

        private static readonly string[] DateFormats =
        {
            "dd.MM.yyyy HH:mm", "dd.MM.yyyy H:mm", "dd.MM.yyyy",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"
        };

        public static async Task RunAsync()
        {
            Console.WriteLine("🚚 Міграція Google Sheets → PostgreSQL");

            string? spreadsheetId = Environment.GetEnvironmentVariable("SPREADSHEET_ID");
            if (string.IsNullOrWhiteSpace(spreadsheetId))
            {
                Console.WriteLine("❌ SPREADSHEET_ID порожній або відсутній.");
                return;
            }
            if (!File.Exists("credentials.json"))
            {
                Console.WriteLine("❌ Немає файлу credentials.json.");
                return;
            }

            SheetsService service;
            try
            {
                GoogleCredential credential;
                using (var stream = new FileStream("credentials.json", FileMode.Open, FileAccess.Read))
                    credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);

                service = new SheetsService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "LibraryBot-Migration"
                });
                Console.WriteLine("✅ Підключено до Google Sheets.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Помилка авторизації Google: {ex.Message}");
                return;
            }

            // 1) Читаємо все в пам'ять (БД ще не чіпаємо).
            List<DbBook> books;
            List<DbBorrowing> borrowings;
            List<DbExchangeLog> exchanges;
            try
            {
                books = await ReadBooksAsync(service, spreadsheetId);
                borrowings = await ReadBorrowingsAsync(service, spreadsheetId);
                exchanges = await ReadExchangesAsync(service, spreadsheetId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Помилка читання таблиці (БД не змінено): {ex.Message}");
                return;
            }

            Console.WriteLine($"📥 Прочитано — 📚 Каталог: {books.Count} | 📕 Видачі: {borrowings.Count} | 🔄 Обмін: {exchanges.Count}");

            if (books.Count == 0 && borrowings.Count == 0 && exchanges.Count == 0)
            {
                Console.WriteLine("⚠️ Дані порожні. Скасовано (БД не змінено).");
                return;
            }

            // 2) Очищаємо й заливаємо. Транзакція + стратегія повторів = атомарність
            //    і стійкість до транзієнтних розривів з'єднання Railway.
            try
            {
                using var db = new AppDbContext();
                var strategy = db.Database.CreateExecutionStrategy();
                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync();

                    Console.WriteLine("🧹 Очищення таблиць Books / Borrowings / ExchangeLogs...");
                    await db.Borrowings.ExecuteDeleteAsync();
                    await db.ExchangeLogs.ExecuteDeleteAsync();
                    await db.Books.ExecuteDeleteAsync();

                    await db.Books.AddRangeAsync(books);
                    await db.Borrowings.AddRangeAsync(borrowings);
                    await db.ExchangeLogs.AddRangeAsync(exchanges);
                    await db.SaveChangesAsync();

                    await tx.CommitAsync();
                });

                Console.WriteLine($"✅ Міграцію завершено. Залито: {books.Count} книг, {borrowings.Count} видач, {exchanges.Count} обмінів.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Помилка запису в PostgreSQL (зміни відкочено): {ex.Message}");
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    Console.WriteLine($"   ↳ {inner.GetType().Name}: {inner.Message}");
            }
        }

        // ── Читання вкладок ──────────────────────────────────────────────

        private static async Task<List<DbBook>> ReadBooksAsync(SheetsService service, string spreadsheetId)
        {
            var rows = await GetRowsAsync(service, spreadsheetId, "Каталог!A2:F");
            var list = new List<DbBook>();
            foreach (var row in rows)
            {
                string title = Cell(row, 0);
                if (string.IsNullOrWhiteSpace(title)) continue; // пропускаємо порожні рядки

                string exchange = Cell(row, 3);
                list.Add(new DbBook
                {
                    Title = title,
                    Author = Cell(row, 1),
                    Genre = Cell(row, 2),
                    ExchangeStatus = string.IsNullOrWhiteSpace(exchange) ? "Так" : exchange,
                    AvailableCount = ParseInt(Cell(row, 4), 0),
                    TotalCount = ParseInt(Cell(row, 5), 1)
                });
            }
            return list;
        }

        private static async Task<List<DbBorrowing>> ReadBorrowingsAsync(SheetsService service, string spreadsheetId)
        {
            var rows = await GetRowsAsync(service, spreadsheetId, "Видачі!A2:I");
            var list = new List<DbBorrowing>();
            foreach (var row in rows)
            {
                string title = Cell(row, 0);
                if (string.IsNullOrWhiteSpace(title)) continue;

                DateTime issue = ParseDate(Cell(row, 4)) ?? DateTime.Now;
                DateTime due = ParseDate(Cell(row, 7)) ?? issue;

                list.Add(new DbBorrowing
                {
                    BookTitle = title,
                    RealName = Cell(row, 1),
                    TelegramName = Cell(row, 2),
                    Contact = Cell(row, 3),
                    IssueDate = issue,
                    ReturnDate = ParseDate(Cell(row, 5)), // порожньо → null (книга ще на руках)
                    ChatId = ParseLong(Cell(row, 6), 0),
                    DueDate = due,
                    IsExtended = Cell(row, 8).Equals("Так", StringComparison.OrdinalIgnoreCase)
                });
            }
            return list;
        }

        private static async Task<List<DbExchangeLog>> ReadExchangesAsync(SheetsService service, string spreadsheetId)
        {
            // Вкладки "Обмін" може не бути — тоді просто повертаємо порожній список.
            List<IList<object>> rows;
            try
            {
                rows = await GetRowsAsync(service, spreadsheetId, "Обмін!A2:D");
            }
            catch
            {
                Console.WriteLine("ℹ️ Вкладку \"Обмін\" не знайдено — пропускаємо.");
                return new List<DbExchangeLog>();
            }

            var list = new List<DbExchangeLog>();
            foreach (var row in rows)
            {
                string oldTitle = Cell(row, 0);
                string newTitle = Cell(row, 1);
                if (string.IsNullOrWhiteSpace(oldTitle) && string.IsNullOrWhiteSpace(newTitle)) continue;

                list.Add(new DbExchangeLog
                {
                    OldBookTitle = oldTitle,
                    NewBookTitle = newTitle,
                    TelegramName = Cell(row, 2),
                    ExchangeDate = ParseDate(Cell(row, 3)) ?? DateTime.Now
                });
            }
            return list;
        }

        // ── Допоміжне ────────────────────────────────────────────────────

        private static async Task<List<IList<object>>> GetRowsAsync(SheetsService service, string spreadsheetId, string range)
        {
            var response = await service.Spreadsheets.Values.Get(spreadsheetId, range).ExecuteAsync();
            return (response.Values ?? new List<IList<object>>()).ToList();
        }

        private static string Cell(IList<object> row, int index)
            => index < row.Count ? row[index]?.ToString()?.Trim() ?? "" : "";

        private static int ParseInt(string s, int fallback)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static long ParseLong(string s, long fallback)
            => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : fallback;

        /// <summary>
        /// Парсить дату з таблиці й позначає її як UTC — щоб зберегти "настінний" час
        /// дослівно, незалежно від часового поясу машини, на якій запускають міграцію
        /// (timestamptz приймає Kind=Utc без конвертації). Порожнє значення → null.
        /// </summary>
        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;

            if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt)
                || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return null;
        }
    }
}
