using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups; // Додали для клавіатури

namespace LibraryBot.Services
{
    public static class NotificationService
    {
        public static async Task CheckDeadlinesAsync(ITelegramBotClient botClient, CancellationToken cancellationToken)
        {
            var borrowings = await GoogleSheetsService.GetAllBorrowingsAsync();
            if (borrowings == null || borrowings.Count == 0) return;

            DateTime today = DateTime.Now.Date;

            // Використовуємо for замість foreach, щоб знати номер рядка в таблиці
            for (int i = 0; i < borrowings.Count; i++)
            {
                var row = borrowings[i];

                // ... початок циклу, перевірка row.Count < 8 та перевірка дати повернення row[5] (індекс 5) ...
                if (row.Count < 8 || !string.IsNullOrWhiteSpace(row.Count > 5 ? row[5]?.ToString() : ""))
                    continue;

                string bookTitle = row[0]?.ToString() ?? "Книга";
                string chatIdStr = row[6]?.ToString() ?? ""; // Змінили на індекс 6 (Колонка G)
                string dueDateStr = row[7]?.ToString() ?? ""; // Змінили на індекс 7 (Колонка H)

                // Перевіряємо колонку I (індекс 8)
                string extendedFlag = row.Count > 8 ? row[8]?.ToString() ?? "" : "";
                bool isExtended = extendedFlag.Equals("Так", StringComparison.OrdinalIgnoreCase);
                // ... далі вся логіка днів та надсилання повідомлень залишається без змін ...

                if (long.TryParse(chatIdStr, out long chatId) &&
                    DateTime.TryParseExact(dueDateStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                {
                    int daysLeft = (dueDate.Date - today).Days;

                    string? notificationMessage = null;
                    InlineKeyboardMarkup? keyboard = null; // Змінна для кнопки

                    if (daysLeft == 3)
                    {
                        notificationMessage = $"⏳ **Час спливає!**\nЧерез 3 дні вам потрібно повернути книгу **{bookTitle}**.\n📅 Дедлайн: {dueDate:dd.MM.yyyy}";
                    }
                    else if (daysLeft == 0)
                    {
                        if (!isExtended)
                        {
                            // Якщо ще не подовжували — даємо кнопку
                            notificationMessage = $"🚨 **Сьогодні останній день!**\nСьогодні вам потрібно повернути книгу **{bookTitle}** до бібліотеки.\n\nЯкщо ви не встигаєте, можете одноразово подовжити термін на 1 місяць 👇";

                            int sheetRowIndex = i + 2; // +2 бо індекс масиву починається з 0, і 1-й рядок це шапка таблиці
                            var button = InlineKeyboardButton.WithCallbackData("⏳ Подовжити на 1 місяць", $"act_ext_{sheetRowIndex}");
                            keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                        }
                        else
                        {
                            // Якщо вже подовжували — просто вимагаємо повернути
                            notificationMessage = $"🚨 **Сьогодні останній день!**\nСьогодні вам потрібно повернути книгу **{bookTitle}** до бібліотеки.\nВи вже використали своє право на подовження, тому чекаємо на вас!";
                        }
                    }
                    else if (daysLeft < 0 && Math.Abs(daysLeft) % 7 == 0)
                    {
                        notificationMessage = $"🆘 **ПРОТЕРМІНУВАННЯ!**\nВи мали повернути книгу **{bookTitle}** ще {Math.Abs(daysLeft)} днів тому!\nБудь ласка, терміново поверніть її до бібліотеки.";
                    }

                    if (notificationMessage != null)
                    {
                        try
                        {
                            // Відправляємо повідомлення (кнопка keyboard буде null для всіх днів, крім 0)
                            await botClient.SendMessage(chatId, notificationMessage, parseMode: ParseMode.Markdown, replyMarkup: keyboard, cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Не вдалося відправити нагадування користувачу {chatId}: {ex.Message}");
                        }
                    }
                }
            }
        }
    }
}