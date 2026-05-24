using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;

namespace LibraryBot.Services
{
    public static class DailyReminderService
    {
        // Метод, який запускає наш таймер у фоновому потоці (не блокуючи бота)
        public static void Start(ITelegramBotClient botClient, CancellationToken cancellationToken)
        {
            Task.Run(() => RunDailyLoopAsync(botClient, cancellationToken), cancellationToken);
        }

        private static async Task RunDailyLoopAsync(ITelegramBotClient botClient, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime now = DateTime.Now;

                // 🕒 Встановлюємо час розсилки (наприклад, 10:00 ранку)
                DateTime nextRun = now.Date.AddHours(10);

                // Якщо сьогодні вже більше ніж 10:00, плануємо на завтра
                if (now > nextRun)
                {
                    nextRun = nextRun.AddDays(1);
                }

                TimeSpan delay = nextRun - now;
                Console.WriteLine($"[Таймер] Наступна розсилка дедлайнів запланована на: {nextRun} (через {delay.TotalHours:F1} годин)");

                try
                {
                    // Бот "засинає" в цьому потоці до настання часу nextRun
                    await Task.Delay(delay, cancellationToken);

                    // Час настав! Виконуємо перевірку і розсилку
                    Console.WriteLine($"[Таймер] 10:00! Починаю розсилку нагадувань...");
                    await NotificationService.CheckDeadlinesAsync(botClient, cancellationToken);
                    Console.WriteLine($"[Таймер] Розсилку успішно завершено.");
                }
                catch (TaskCanceledException)
                {
                    // Бот зупиняється (натиснули Enter в консолі)
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Таймер] Помилка: {ex.Message}");
                    // Чекаємо хвилину перед повторною спробою, щоб не спамити помилками
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
                }
            }
        }
    }
}