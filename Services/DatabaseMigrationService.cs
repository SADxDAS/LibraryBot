using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LibraryBot.Data;
using Npgsql;

namespace LibraryBot.Services
{
    public static class DatabaseMigrationService
    {
        public static async Task RunMigrationAsync()
        {
            // ⚠️ ВСТАВЬТЕ СЮДА URL СТАРОЙ БАЗЫ ДАННЫХ RAILWAY (ГДЕ ЛЕЖАТ ВАШИ ДАННЫЕ)
            string oldUrl = "";

            Console.WriteLine("🔄 Начинаем безопасный перенос данных...");

            // Подключаемся к новой базе (использует .env)
            using var newDb = new AppDbContext();

            // Подключаемся к старой базе напрямую для чтения
            string oldConnStr = BuildNpgsqlConnectionString(oldUrl);
            using var oldConn = new NpgsqlConnection(oldConnStr);
            await oldConn.OpenAsync();

            Console.WriteLine("📥 1/4 Читаем книги из старой базы...");
            var titleToNewBookMap = new Dictionary<string, DbBook>();

            using (var cmd = new NpgsqlCommand("SELECT \"Title\", \"Author\", \"Genre\", \"ExchangeStatus\", \"AvailableCount\", \"TotalCount\" FROM \"Books\"", oldConn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var book = new DbBook
                    {
                        Title = reader.GetString(0),
                        Author = reader.GetString(1),
                        Genre = reader.GetString(2),
                        ExchangeStatus = reader.GetString(3),
                        AvailableCount = reader.GetInt32(4),
                        TotalCount = reader.GetInt32(5)
                    };
                    newDb.Books.Add(book);
                    titleToNewBookMap[book.Title] = book; // Запоминаем для связи
                }
            }

            await newDb.SaveChangesAsync(); // Книги получают новые правильные ID
            Console.WriteLine($"✅ Книги перенесены ({titleToNewBookMap.Count} шт.)");

            Console.WriteLine("📥 2/4 Переносим выдачи...");
            int borrowingsCount = 0;
            using (var cmd = new NpgsqlCommand("SELECT \"BookTitle\", \"RealName\", \"TelegramName\", \"Contact\", \"IssueDate\", \"ReturnDate\", \"ChatId\", \"DueDate\", \"IsExtended\" FROM \"Borrowings\"", oldConn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string oldTitle = reader.GetString(0);
                    if (titleToNewBookMap.TryGetValue(oldTitle, out var newBook))
                    {
                        newDb.Borrowings.Add(new DbBorrowing
                        {
                            BookId = newBook.Id, // МАГИЯ СВЯЗИ ПРОИСХОДИТ ЗДЕСЬ!
                            RealName = reader.GetString(1),
                            TelegramName = reader.GetString(2),
                            Contact = reader.GetString(3),
                            IssueDate = reader.GetDateTime(4),
                            ReturnDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                            ChatId = reader.GetInt64(6),
                            DueDate = reader.GetDateTime(7),
                            IsExtended = reader.GetBoolean(8)
                        });
                        borrowingsCount++;
                    }
                }
            }
            await newDb.SaveChangesAsync();
            Console.WriteLine($"✅ Выдачи перенесены ({borrowingsCount} шт.)");

            Console.WriteLine("📥 3/4 Переносим логи обмена...");
            int exchangeCount = 0;
            using (var cmd = new NpgsqlCommand("SELECT \"OldBookTitle\", \"NewBookTitle\", \"TelegramName\", \"ExchangeDate\" FROM \"ExchangeLogs\"", oldConn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    newDb.ExchangeLogs.Add(new DbExchangeLog
                    {
                        OldBookTitle = reader.GetString(0),
                        NewBookTitle = reader.GetString(1),
                        TelegramName = reader.GetString(2),
                        ExchangeDate = reader.GetDateTime(3)
                    });
                    exchangeCount++;
                }
            }
            await newDb.SaveChangesAsync();
            Console.WriteLine($"✅ Логи обмена перенесены ({exchangeCount} шт.)");

            Console.WriteLine("📥 4/4 Переносим заявки...");
            using (var cmd = new NpgsqlCommand("SELECT \"RequestId\", \"Type\", \"UserId\", \"UserName\", \"RealName\", \"Contact\", \"BookTitle\", \"CatalogRowIndex\", \"BorrowDays\", \"NewBookTitle\", \"NewBookAuthor\", \"NewBookGenre\" FROM \"PendingRequests\"", oldConn))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string oldTitle = reader.GetString(6);
                    int? newBookId = titleToNewBookMap.TryGetValue(oldTitle, out var b) ? b.Id : null;

                    newDb.PendingRequests.Add(new DbPendingRequest
                    {
                        RequestId = reader.GetString(0),
                        Type = reader.GetString(1),
                        UserId = reader.GetInt64(2),
                        UserName = reader.GetString(3),
                        RealName = reader.GetString(4),
                        Contact = reader.GetString(5),
                        BookId = newBookId,
                        CatalogRowIndex = reader.GetInt32(7),
                        BorrowDays = reader.GetInt32(8),
                        NewBookTitle = reader.GetString(9),
                        NewBookAuthor = reader.GetString(10),
                        NewBookGenre = reader.GetString(11)
                    });
                }
            }
            await newDb.SaveChangesAsync();

            Console.WriteLine("🎉 МИГРАЦИЯ УСПЕШНО ЗАВЕРШЕНА!");
        }

        private static string BuildNpgsqlConnectionString(string url)
        {
            var uri = new Uri(url);
            var userInfo = uri.UserInfo.Split(':', 2);
            return $"Server={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};Database={uri.AbsolutePath.Trim('/')};User Id={Uri.UnescapeDataString(userInfo[0])};Password={(userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "")};Ssl Mode=Require;Trust Server Certificate=true;";
        }
    }
}