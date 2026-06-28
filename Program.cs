using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using LibraryBot.Handlers;
using LibraryBot.Services;

namespace LibraryBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. Завантажуємо локальний .env (якщо він є. На Railway його немає - і це ок)
            try { Env.Load(); } catch { }

            // 0. РЕЖИМ МІГРАЦІЇ: dotnet run -- migrate
            //    Одноразово переносить дані зі старих Google Sheets у PostgreSQL і виходить.
            if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
            {
                await SheetsMigrationService.RunAsync();
                return;
            }

            // РЕЖИМ ПЕРЕВІРКИ БД: dotnet run -- dbtest
            //    Швидко перевіряє з'єднання з PostgreSQL (без читання таблиць) і виходить.
            if (args.Length > 0 && args[0].Equals("dbtest", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    int n;
                    using (var db = new LibraryBot.Data.AppDbContext())
                        n = await db.Books.CountAsync();
                    long cold = sw.ElapsedMilliseconds;
                    Console.WriteLine($"✅ З'єднання з PostgreSQL працює. Книг у БД: {n}");
                    Console.WriteLine($"   Перший запит (холодний, +відкриття з'єднання): {cold} мс");

                    // Кілька "теплих" запитів — мають переюзати з'єднання з пулу (швидко).
                    const int warmRuns = 10;
                    sw.Restart();
                    for (int i = 0; i < warmRuns; i++)
                        using (var db = new LibraryBot.Data.AppDbContext())
                            await db.Books.CountAsync();
                    long warmTotal = sw.ElapsedMilliseconds;
                    Console.WriteLine($"   {warmRuns} теплих запитів: {warmTotal} мс (≈ {warmTotal / (double)warmRuns:F1} мс/запит з пулом)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ З'єднання не вдалося: {ex.Message}");
                    for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                        Console.WriteLine($"   ↳ {inner.GetType().Name}: {inner.Message}");
                }
                return;
            }

            Console.WriteLine("🔄 Перевірка налаштувань (Environment Variables)...");

            // 2. ПЕРЕВІРКА ТОКЕНА ТЕЛЕГРАМ
            string? botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            if (string.IsNullOrWhiteSpace(botToken))
            {
                Console.WriteLine("❌ КРИТИЧНА ПОМИЛКА: Змінна TELEGRAM_BOT_TOKEN порожня або відсутня.");
                Console.WriteLine("Бот безпечно зупинено. Додайте змінну і перезапустіть.");
                return; // М'яко виходимо з програми
            }

            // Вантажимо адмінів зі змінної ADMIN_IDS
            SessionManager.LoadAdminsFromEnv();

            // 3. ПЕРЕВІРКА З'ЄДНАННЯ З БД.
            //    Google у рантаймі більше не потрібен — credentials.json / SPREADSHEET_ID
            //    потрібні лише одноразовій команді `migrate` (вона перевіряє їх сама).
            try
            {
                GoogleSheetsService.Initialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ПОМИЛКА ПІДКЛЮЧЕННЯ ДО БД: {ex.Message}");
                return;
            }

            // 6. БЕЗПЕЧНИЙ ЗАПУСК БОТА
            try
            {
                var botClient = new TelegramBotClient(botToken);
                using var cts = new CancellationTokenSource();

                var receiverOptions = new ReceiverOptions
                {
                    AllowedUpdates = Array.Empty<UpdateType>()
                };

                botClient.StartReceiving(
                    UpdateHandler.HandleUpdateAsync,
                    UpdateHandler.HandleErrorAsync,
                    receiverOptions,
                    cancellationToken: cts.Token
                );

                DailyReminderService.Start(botClient, cts.Token);
                var me = await botClient.GetMe();

                Console.WriteLine("✅=====================================✅");
                Console.WriteLine($"   BOT @{me.Username} SUCCESFULY LAUNCH!   ");
                Console.WriteLine("✅=====================================✅");

                // Спеціальна команда для Railway: тримає програму запущеною вічно, не вимагаючи клавіатури
                await Task.Delay(-1, cts.Token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ КРИТИЧНА ПОМИЛКА ЗАПУСКУ TELEGRAM БОТА: {ex.Message}");
            }
        }
    }
}