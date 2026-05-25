using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LibraryBot.Handlers
{
    public static class UpdateHandler
    {
        public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Направляємо кліки по кнопках у CallbackHandler
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await CallbackHandler.HandleAsync(botClient, update.CallbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { } message)
                return;

            await HandleMessageAsync(botClient, message, cancellationToken);
        }

        private static async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            string messageText = message.Text?.Trim() ?? "";

            // --- БРОНЕБІЙНІ СИСТЕМНІ КОМАНДИ ---
            if (messageText == "/myid")
            {
                await botClient.SendMessage(chatId, $"Ваш Telegram ID: {chatId}", cancellationToken: cancellationToken);
                return;
            }

            if (messageText == "/start")
            {
                SessionManager.ClearSession(chatId);
                string startText = "👋 <b>Привіт! Це бібліотека «Паперового Життя»</b> 📚\n\n" +
                                   "Тут ви можете:\n" +
                                   "📥 <b>Взяти книгу</b> на прочитання\n" +
                                   "📤 <b>Повернути ту</b>, що вже прочитали\n" +
                                   "🤝 <b>Обміняти буккросингом</b> свої книги з нашими 💚\n\n" +
                                   "<i>Оберіть потрібну дію в меню нижче 👇</i>";
                await botClient.SendMessage(chatId, startText, parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            if (messageText == "/test_reminders")
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;
                await botClient.SendMessage(chatId, "🔄 Запускаю перевірку бази та розсилку дедлайнів...", cancellationToken: cancellationToken);
                await NotificationService.CheckDeadlinesAsync(botClient, cancellationToken);
                await botClient.SendMessage(chatId, "✅ Тестова перевірка завершена!", cancellationToken: cancellationToken);
                return;
            }

            // --- ГЛОБАЛЬНЕ ПЕРЕХОПЛЕННЯ КНОПОК МЕНЮ (ЖЕЛЕЗОБЕТОННА ЛОГІКА) ---
            // Якщо текст - це кнопка головного меню, ми ОЧИЩАЄМО ПАМ'ЯТЬ (ClearSession) і перериваємо будь-які старі дії!
            switch (messageText)
            {
                case "/books":
                case "📚 Каталог":
                    SessionManager.ClearSession(chatId);
                    await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, chatId, 0, null, cancellationToken);
                    return; // return означає, що ми не йдемо далі в обробники станів!

                case "/borrow":
                case "📥 Взяти книгу":
                    SessionManager.ClearSession(chatId);
                    SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
                    await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора, щоб знайти книгу:", cancellationToken: cancellationToken);
                    return;

                case "/return":
                case "📤 Повернути книгу":
                    SessionManager.ClearSession(chatId);
                    string tgName = message.Chat.FirstName ?? "Без імені";
                    if (!string.IsNullOrEmpty(message.Chat.Username)) tgName += $" (@{message.Chat.Username})";
                    await LibraryDisplayService.ShowUserBorrowedBooksAsync(botClient, chatId, tgName, cancellationToken);
                    return;

                case "/search":
                case "🔍 Пошук":
                    SessionManager.ClearSession(chatId);
                    SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
                    await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора (можна частково) для пошуку:", cancellationToken: cancellationToken);
                    return;

                case "/cancel":
                case "❌ Скасувати дію":
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "Дію скасовано. Повернення до головного меню.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return;

                case "/help":
                case "ℹ️ Допомога":
                    SessionManager.ClearSession(chatId);
                    string helpText = "ℹ️ <b>Довідка: як користуватися ботом</b>\n\n" +
                                      "Тут ви можете легко знаходити потрібну літературу.\n\n" +
                                      "📚 <b>Каталог</b> — перегляд повного списку книг бібліотеки.\n" +
                                      "🔍 <b>Пошук</b> — швидкий пошук за назвою книги або автором.\n\n" +
                                      "ℹ️ <i>Щоб взяти або повернути книгу потрібно підтвердження адміністратора.</i>\n" +
                                      "📥 <b>Взяти книгу</b> — оформлення запиту на видачу.\n" +
                                      "📤 <b>Повернути книгу</b> — перегляд ваших книг та надсилання запиту на повернення .\n\n" +
                                      "❌ <b>Скасувати дію</b> — перериває будь-який поточний крок (наприклад, якщо помилилися при введенні).\n\n" +
                                      "💡 <i>Щодо буккросингу: якщо ви принесли свою книгу для обміну, зверніться до баристи для його оформлення!</i>";
                    await botClient.SendMessage(chatId, helpText, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    return;

                case "🤝 Обміняти книгу":
                    SessionManager.ClearSession(chatId);
                    SessionManager.UserStates[chatId] = UserState.WaitingForUserExchangeTitle;
                    // Обов'язково створюємо нову сесію після її очищення рядком вище
                    SessionManager.UserExchangeSessions[chatId] = new UserExchangeSession();
                    await botClient.SendMessage(chatId, "🤝 <b>Оформлення обміну книги</b>\n\nБудь ласка, введіть <b>НАЗВУ</b> книги, яку ви хочете принести до бібліотеки:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    return;

                // --- АДМІНСЬКІ КОМАНДИ МЕНЮ ---
                case "/add":
                case "➕ Додати":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForAddBookTitle;
                        SessionManager.AdminBookSessions[chatId] = new AdminBookSession();
                        await botClient.SendMessage(chatId, "📝 Введіть НАЗВУ нової книги:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;

                case "/delete":
                case "🗑 Видалити":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForDeleteSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАЛИТИ:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;

                case "/edit":
                case "✏️ Редагувати":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForEditSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВІДРЕДАГУВАТИ:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;

                case "/exchange":
                case "🤝 Обмін":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForExchangeSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги з бібліотеки, яку хочете ОБМІНЯТИ:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;

                case "/handgive":
                case "📥 Видати вручну":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForManualSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАТИ ВРУЧНУ:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;

                case "/handreturn":
                case "📤 Повернути вручну":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.ClearSession(chatId);
                        SessionManager.UserStates[chatId] = UserState.WaitingForManualReturnSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку серед книг, які зараз знаходяться НА РУКАХ:", cancellationToken: cancellationToken);
                        return;
                    }
                    break;
            }

            // --- НАПРАВЛЯЄМО В ОБРОБНИКИ СТАНІВ ТІЛЬКИ ЯКЩО ЦЕ БУЛА НЕ КНОПКА МЕНЮ ---
            var currentState = SessionManager.UserStates.GetValueOrDefault(chatId, UserState.None);

            if (currentState != UserState.None && SessionManager.AdminIds.Contains(chatId))
            {
                if (await AdminStateHandler.HandleAsync(botClient, chatId, messageText, currentState, cancellationToken)) return;
            }

            if (currentState != UserState.None)
            {
                if (await UserStateHandler.HandleAsync(botClient, message, currentState, cancellationToken)) return;
            }
        }

        public static Task HandleErrorAsync(ITelegramBotClient botClient, System.Exception exception, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"Помилка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}