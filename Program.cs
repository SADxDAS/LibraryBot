using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using DotNetEnv;
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

            Console.WriteLine("🔄 Перевірка налаштувань (Environment Variables)...");

            // 2. ПЕРЕВІРКА ТОКЕНА ТЕЛЕГРАМ
            string? botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            if (string.IsNullOrWhiteSpace(botToken))
            {
                Console.WriteLine("❌ КРИТИЧНА ПОМИЛКА: Змінна TELEGRAM_BOT_TOKEN порожня або відсутня.");
                Console.WriteLine("Бот безпечно зупинено. Додайте змінну і перезапустіть.");
                return; // М'яко виходимо з програми
            }

            // 3. ПЕРЕВІРКА ТА СТВОРЕННЯ КЛЮЧІВ GOOGLE
            string? googleCreds = Environment.GetEnvironmentVariable("GOOGLE_CREDENTIALS_JSON");
            if (!string.IsNullOrWhiteSpace(googleCreds))
            {
                // Якщо є змінна Railway - створюємо файл
                File.WriteAllText("credentials.json", googleCreds);
            }
            else if (!File.Exists("credentials.json"))
            {
                // Якщо і змінної немає, і файлу локально немає
                Console.WriteLine("❌ КРИТИЧНА ПОМИЛКА: Немає файлу 'credentials.json' і змінна GOOGLE_CREDENTIALS_JSON порожня.");
                Console.WriteLine("Бот безпечно зупинено. Додайте ключ доступу Google.");
                return;
            }

            // 4. ПЕРЕВІРКА ID ТАБЛИЦІ
            string? spreadsheetId = Environment.GetEnvironmentVariable("SPREADSHEET_ID");
            if (string.IsNullOrWhiteSpace(spreadsheetId))
            {
                Console.WriteLine("❌ КРИТИЧНА ПОМИЛКА: Змінна SPREADSHEET_ID порожня або відсутня.");
                return;
            }

            // Якщо всі перевірки пройдені — вантажимо адмінів
            SessionManager.LoadAdminsFromEnv();

            // 5. БЕЗПЕЧНЕ ПІДКЛЮЧЕННЯ ДО GOOGLE SHEETS
            try
            {
                GoogleSheetsService.Initialize();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ПОМИЛКА ПІДКЛЮЧЕННЯ ДО GOOGLE ТАБЛИЦЬ: {ex.Message}");
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