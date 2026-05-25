using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LibraryBot.Handlers
{
    public static class UserStateHandler
    {
        public static async Task<bool> HandleAsync(ITelegramBotClient botClient, Message message, UserState currentState, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            string messageText = message.Text?.Trim() ?? "";
            if (currentState == UserState.WaitingForSearchQuery)
            {
                if (messageText == "❌ Скасувати дію" || messageText.ToLower() == "/cancel")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "❌ Пошук скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return true;
                }

                string telegramName = message.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(message.Chat.Username))
                {
                    telegramName += $" (@{message.Chat.Username})";
                }

                await LibraryDisplayService.SearchBooksAsync(botClient, chatId, messageText, telegramName, cancellationToken);
                return true;
            }
            // --- ОБРОБКА ЗАПИТУ НА ОБМІН ВІД КОРИСТУВАЧА ---
            if (currentState == UserState.WaitingForUserExchangeTitle)
            {
                if (messageText == "❌ Скасувати дію" || messageText.ToLower() == "/cancel")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "❌ Обмін скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return true;
                }
                SessionManager.UserExchangeSessions[chatId].Title = messageText;
                SessionManager.UserStates[chatId] = UserState.WaitingForUserExchangeAuthor;
                await botClient.SendMessage(chatId, "👤 Введіть <b>АВТОРА</b> цієї книги (або надішліть `-`, якщо невідомий):", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return true;
            }

            if (currentState == UserState.WaitingForUserExchangeAuthor)
            {
                SessionManager.UserExchangeSessions[chatId].Author = messageText;
                SessionManager.UserStates[chatId] = UserState.WaitingForUserExchangeGenre;
                await botClient.SendMessage(chatId, "🎭 Введіть <b>ЖАНР</b> цієї книги (або надішліть `-`):", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return true;
            }

            if (currentState == UserState.WaitingForUserExchangeGenre)
            {
                SessionManager.UserExchangeSessions[chatId].Genre = messageText;

                // Перемикаємо стан на пошук книги в бібліотеці
                SessionManager.UserStates[chatId] = UserState.WaitingForUserExchangeSearchQuery;

                await botClient.SendMessage(chatId, "🔍 <b>Крок 4 з 4: Вибір книги натомість</b>\n\nВведіть назву книги або автора з бібліотеки, яку ви хочете <b>забрати собі</b>:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                return true;
            }

            // ОБРОБКА ВВЕДЕНОГО ЗАПИТУ ПОШУКУ ДЛЯ ОБМІНУ
            if (currentState == UserState.WaitingForUserExchangeSearchQuery)
            {
                if (messageText == "❌ Скасувати дію" || messageText.ToLower() == "/cancel")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "❌ Обмін скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return true;
                }

                // Викликаємо спеціальний метод відображення результатів пошуку для обміну
                await LibraryDisplayService.SearchBooksForUserExchangeAsync(botClient, chatId, messageText, cancellationToken);
                return true;
            }

            // КРОК 1: Користувач ввів реальне ім'я
            if (currentState == UserState.WaitingForBorrowRealName)
            {
                SessionManager.BorrowSessions[chatId].RealName = messageText;
                SessionManager.UserStates[chatId] = UserState.WaitingForBorrowContactMethod;

                // Створюємо ТИМЧАСОВУ нижню клавіатуру для запиту контакту
                var contactKeyboard = new ReplyKeyboardMarkup(new[]
                {
                    new[] { KeyboardButton.WithRequestContact("📱 Поділитися номером") },
                    new[] { new KeyboardButton("✈️ Мій Telegram"), new KeyboardButton("📸 Instagram") },
                    new[] { new KeyboardButton("✍️ Інше"), new KeyboardButton("❌ Скасувати дію") }
                })
                { ResizeKeyboard = true };

                await botClient.SendMessage(chatId, "📞 Оберіть зручний спосіб зв'язку:", replyMarkup: contactKeyboard, cancellationToken: cancellationToken);
                return true;
            }

            // КРОК 2: Користувач натиснув одну з кнопок контакту
            if (currentState == UserState.WaitingForBorrowContactMethod)
            {
                var session = SessionManager.BorrowSessions[chatId];

                // Якщо людина відправила КОНТАКТ (натиснула "Поділитися номером")
                if (message.Type == MessageType.Contact && message.Contact != null)
                {
                    session.Contact = $"Телефон: {message.Contact.PhoneNumber}";
                    return await ProceedToPeriodSelection(botClient, chatId, cancellationToken);
                }

                if (messageText == "✈️ Мій Telegram")
                {
                    if (string.IsNullOrEmpty(message.Chat.Username))
                    {
                        await botClient.SendMessage(chatId, "У вас не встановлено @username в Telegram. Будь ласка, оберіть '✍️ Інше' та введіть номер вручну.", cancellationToken: cancellationToken);
                        return true;
                    }
                    session.Contact = $"Telegram: @{message.Chat.Username}";
                    return await ProceedToPeriodSelection(botClient, chatId, cancellationToken);
                }

                if (messageText == "📸 Instagram")
                {
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowContactInput;
                    session.ContactMethod = "Instagram";
                    // Прибираємо клавіатуру, щоб не заважала вводити текст
                    await botClient.SendMessage(chatId, "Введіть ваш нікнейм в Instagram (наприклад, @paper_life):", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return true;
                }

                if (messageText == "✍️ Інше")
                {
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowContactInput;
                    session.ContactMethod = "Інше";
                    await botClient.SendMessage(chatId, "Введіть ваші контактні дані (номер телефону, Viber, тощо):", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return true;
                }

                // Якщо ввели якусь дурницю замість натискання кнопки
                await botClient.SendMessage(chatId, "Будь ласка, скористайтеся кнопками меню нижче 👇", cancellationToken: cancellationToken);
                return true;
            }

            // КРОК 3: Ручне введення (Instagram або Інше)
            if (currentState == UserState.WaitingForBorrowContactInput)
            {
                var session = SessionManager.BorrowSessions[chatId];
                session.Contact = $"{session.ContactMethod}: {messageText}";
                return await ProceedToPeriodSelection(botClient, chatId, cancellationToken);
            }
            if (currentState == UserState.WaitingForPageNumber)
            {
                if (int.TryParse(messageText, out int pageNumber) && pageNumber > 0)
                {
                    SessionManager.UserStates[chatId] = UserState.None;
                    await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, chatId, pageNumber - 1, null, cancellationToken);
                }
                else
                {
                    await botClient.SendMessage(chatId, "❌ Будь ласка, введіть коректне число.", cancellationToken: cancellationToken);
                }
                return true;
            }
            return false;
        }
        // Допоміжний метод, щоб не дублювати код переходу до вибору періоду
        private static async Task<bool> ProceedToPeriodSelection(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
        {
            SessionManager.UserStates[chatId] = UserState.WaitingForBorrowPeriod;

            // 1. Повертаємо нормальне головне меню вниз!
            await botClient.SendMessage(chatId, "✅ Контакт успішно збережено.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);

            // 2. Відправляємо інлайн-кнопки з періодами
            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("⏳ 2 тижні (14 днів)", "period_14") },
                new[] { InlineKeyboardButton.WithCallbackData("🗓 1 місяць (30 днів)", "period_30") },
                new[] { InlineKeyboardButton.WithCallbackData("📚 2 місяці (60 днів)", "period_60") }
            });

            await botClient.SendMessage(chatId, "👇 На який термін ви берете книгу?", replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);
            return true;
        }
    }
}