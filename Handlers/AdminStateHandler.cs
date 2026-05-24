using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LibraryBot.Handlers
{
    public static class AdminStateHandler
    {
        public static async Task<bool> HandleAsync(ITelegramBotClient botClient, long chatId, string text, UserState state, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(text) && (text.ToLower() == "/cancel" || text == "❌ Скасувати дію"))
            {
                SessionManager.ClearSession(chatId);
                await botClient.SendMessage(chatId, "Дію скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // 1. ДОДАВАННЯ КНИГИ
            // 1. ДОДАВАННЯ КНИГИ
            if (state == UserState.WaitingForAddBookTitle)
            {
                SessionManager.AdminBookSessions[chatId].Title = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;
                await botClient.SendMessage(chatId, "👤 Введіть АВТОРА книги (або відправте `-`, якщо невідомий):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookAuthor)
            {
                SessionManager.AdminBookSessions[chatId].Author = text; // Якщо ввели "-", збережеться "-"
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookGenre;
                await botClient.SendMessage(chatId, "🎭 Введіть ЖАНР книги (або відправте `-`, якщо немає):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookGenre)
            {
                SessionManager.AdminBookSessions[chatId].Genre = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookQuantity;
                await botClient.SendMessage(chatId, "🔢 Введіть КІЛЬКІСТЬ примірників цієї книги (тільки цифру, наприклад 1, 2, 5):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookQuantity)
            {
                if (!int.TryParse(text, out int qty) || qty <= 0)
                {
                    await botClient.SendMessage(chatId, "❌ Будь ласка, введіть коректне число більше нуля (наприклад: 1):", cancellationToken: cancellationToken);
                    return true;
                }

                SessionManager.AdminBookSessions[chatId].Quantity = qty;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookExchangeStatus;

                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "🔄 Чи доступна ця книга для обміну?", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookExchangeStatus)
            {
                string exchangeStatus = (text.ToLower() == "ні") ? "Ні" : "Так";
                var session = SessionManager.AdminBookSessions[chatId];
                int qty = session.Quantity > 0 ? session.Quantity : 1;

                // Передаємо зібрану кількість у метод
                await GoogleSheetsService.AddBookToCatalogAsync(session.Title!, session.Author!, session.Genre!, "Доступна", exchangeStatus, qty);
                SessionManager.ClearSession(chatId);

                string exchText = exchangeStatus == "Ні" ? "Не обмінюється" : "Можна обмінювати";
                await botClient.SendMessage(chatId, $"✅ Книгу **{session.Title}** ({qty} шт.) успішно додано!\nСтатус: {exchText}", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // 2. ВИДАЛЕННЯ КНИГИ
            if (state == UserState.WaitingForDeleteSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForDeletionAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            // 3. РЕДАГУВАННЯ КНИГИ
            if (state == UserState.WaitingForEditSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForEditingAsync(botClient, chatId, text, cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForEditBookTitle)
            {
                if (text != "-") SessionManager.AdminBookSessions[chatId].Title = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForEditBookAuthor;
                string oldAuthor = SessionManager.AdminBookSessions[chatId].Author ?? "Невідомий";
                await botClient.SendMessage(chatId, $"Введіть НОВОГО АВТОРА (або `-` щоб залишити: {oldAuthor}):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForEditBookAuthor)
            {
                if (text != "-") SessionManager.AdminBookSessions[chatId].Author = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForEditBookGenre;
                string oldGenre = SessionManager.AdminBookSessions[chatId].Genre ?? "Не вказано";
                await botClient.SendMessage(chatId, $"Введіть НОВИЙ ЖАНР (або `-` щоб залишити: {oldGenre}):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForEditBookGenre)
            {
                if (text != "-") SessionManager.AdminBookSessions[chatId].Genre = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForEditBookQuantity;

                var session = SessionManager.AdminBookSessions[chatId];

                // Створюємо зручну нижню клавіатуру з твоїми кнопками
                var kb = new ReplyKeyboardMarkup(new[] 
                { 
                    new KeyboardButton[] { "-1", "залишити", "+1" }, 
                    new KeyboardButton[] { "❌ Скасувати дію" } 
                }) { ResizeKeyboard = true };

                await botClient.SendMessage(chatId, $"🔢 **Редагування кількості примірників**\nЗараз у бібліотеці всього: **{session.CurrentTotal}** шт. (з них доступно для видачі: {session.CurrentAvailable})\n\nОберіть дію за допомогою кнопок або введіть точну загальну кількість вручну:", parseMode: ParseMode.Markdown, replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }
            // НОВИЙ БЛОК: Обробка кнопок кількості
            if (state == UserState.WaitingForEditBookQuantity)
            {
                var session = SessionManager.AdminBookSessions[chatId];
                int newTotal = session.CurrentTotal;
                int newAvailable = session.CurrentAvailable;

                if (text == "+1")
                {
                    newTotal += 1;
                    newAvailable += 1; // Нова книга одразу стає доступною
                }
                else if (text == "-1")
                {
                    if (session.CurrentTotal > 0)
                    {
                        newTotal -= 1;
                        // Зменшуємо доступні, але стежимо, щоб не пішло в мінус
                        newAvailable = Math.Max(0, newAvailable - 1); 
                    }
                }
                else if (text.ToLower() == "залишити" || text == "-")
                {
                    // Нічого не міняємо, залишаються старі значення
                }
                else // Якщо адмін ввів конкретну цифру руками (наприклад "5")
                {
                    if (!int.TryParse(text, out int parsedTotal) || parsedTotal < 0)
                    {
                        await botClient.SendMessage(chatId, "❌ Будь ласка, оберіть дію з кнопок або введіть коректне число примірників:", cancellationToken: cancellationToken);
                        return true;
                    }

                    // Рахуємо різницю, щоб коректно посунути доступні книги
                    int diff = parsedTotal - session.CurrentTotal;
                    newTotal = parsedTotal;
                    newAvailable = Math.Max(0, session.CurrentAvailable + diff);
                }

                // Зберігаємо прораховані значення в сесію
                session.CurrentTotal = newTotal;
                session.CurrentAvailable = newAvailable;

                // Переходимо до фінального кроку — статус обміну
                SessionManager.UserStates[chatId] = UserState.WaitingForEditBookExchangeStatus;
                string oldExch = session.ExchangeStatus ?? "Так";
                string currentChoice = oldExch == "Ні" ? "Ні" : "Так";

                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні", "-" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, $"🔄 Чи доступна ця книга для обміну? (Зараз: {currentChoice})\nОберіть `Так`, `Ні` або `-` щоб залишити без змін:", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }

            if (state == UserState.WaitingForEditBookExchangeStatus)
            {
                var session = SessionManager.AdminBookSessions[chatId];

                string finalExch = session.ExchangeStatus ?? "Так";
                if (text.ToLower() == "так") finalExch = "Так";
                else if (text.ToLower() == "ні") finalExch = "Ні";

                // Викликаємо наш новий метод і передаємо текстові поля + прораховані кількості
                bool updated = await GoogleSheetsService.UpdateBookInCatalogAsync(
                    session.EditRowIndex, 
                    session.Title!, 
                    session.Author!, 
                    session.Genre!, 
                    finalExch,
                    session.CurrentAvailable,
                    session.CurrentTotal
                );

                SessionManager.ClearSession(chatId);

                if (updated) 
                    await botClient.SendMessage(chatId, $"✅ Книгу **{session.Title}** успішно оновлено!\n📊 Новий баланс: {session.CurrentTotal} шт. (Доступно: {session.CurrentAvailable})", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                else 
                    await botClient.SendMessage(chatId, $"❌ Помилка оновлення таблиці.", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // 4. ОБМІН КНИГИ
            if (state == UserState.WaitingForExchangeSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForExchangeAsync(botClient, chatId, text, cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForExchangeReaderName)
            {
                SessionManager.AdminExchangeSessions[chatId].ReaderName = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForExchangeNewTitle;
                await botClient.SendMessage(chatId, "📝 Введіть НАЗВУ нової книги, яку принесли (Обов'язково):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForExchangeNewTitle)
            {
                if (text == "-")
                {
                    await botClient.SendMessage(chatId, "❌ Назва нової книги є обов'язковою! Будь ласка, введіть назву:", cancellationToken: cancellationToken);
                    return true;
                }
                SessionManager.AdminExchangeSessions[chatId].NewBookTitle = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForExchangeNewAuthor;
                await botClient.SendMessage(chatId, "👤 Введіть АВТОРА нової книги (Опціонально, або `-` щоб пропустити):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForExchangeNewAuthor)
            {
                SessionManager.AdminExchangeSessions[chatId].NewBookAuthor = (text == "-") ? "Невідомий автор" : text;
                SessionManager.UserStates[chatId] = UserState.WaitingForExchangeNewGenre;
                await botClient.SendMessage(chatId, "🎭 Введіть ЖАНР нової книги (Опціонально, або `-` щоб пропустити):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForExchangeNewGenre)
            {
                var session = SessionManager.AdminExchangeSessions[chatId];
                session.NewBookGenre = (text == "-") ? "Не вказано" : text;

                // 1. Записуємо в лог обміну
                await GoogleSheetsService.AddExchangeLogAsync(session.OldBookTitle, session.NewBookTitle, session.ReaderName);

                // 2. РОЗУМНЕ СПИСАННЯ СТАРОЇ КНИГИ (видалення або -1)
                await GoogleSheetsService.ProcessExchangeOutgoingBookAsync(session.OldBookTitle);

                // 3. Додаємо нову книгу
                await GoogleSheetsService.AddBookToCatalogAsync(session.NewBookTitle, session.NewBookAuthor, session.NewBookGenre, "Доступна", "Так");

                SessionManager.ClearSession(chatId);

                string successText = $"✅ **Обмін успішно завершено!**\n\n📖 Стара книга '**{session.OldBookTitle}**' списана з балансу (або зменшено її кількість).\n✨ Нова книга '**{session.NewBookTitle}**' додана та доступна для читачів.\n🗂 Запис внесено в аркуш 'Обмін'.";
                await botClient.SendMessage(chatId, successText, parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }
            // 5. РУЧНА ВИДАЧА ТА ПОВЕРНЕННЯ
            if (state == UserState.WaitingForManualSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForManualBorrowAsync(botClient, chatId, text, cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForManualReaderName)
            {
                SessionManager.AdminSessions[chatId].ReaderName = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForManualReaderContact;
                await botClient.SendMessage(chatId, "📞 Введіть контакт читача (номер телефону, клас або інші дані):", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForManualReaderContact)
            {
                var session = SessionManager.AdminSessions[chatId];
                session.ReaderContact = text;

                // Передаємо: Назву, Ім'я офлайн-читача, замість нікнейму пишемо "Офлайн", контакт, chatId = 0, термін 30 днів
                await GoogleSheetsService.AddBorrowingAsync(session.BookId!, session.ReaderName!, "Офлайн читач", session.ReaderContact!, 0, DateTime.Now.AddDays(30));
                await GoogleSheetsService.UpdateBookStatusAsync(session.BookId!, "Читають");

                SessionManager.ClearSession(chatId);
                await botClient.SendMessage(chatId, $"✅ Готово! Книга '**{session.BookId}**' видана читачу **{session.ReaderName}** ({session.ReaderContact}).", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForManualReturnSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForManualReturnAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            return false;
        }
    }
}