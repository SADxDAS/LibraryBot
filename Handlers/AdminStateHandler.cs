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
                string newTitle = text.Trim();
                var session = SessionManager.AdminBookSessions[chatId];
                session.Title = newTitle;
                session.EditRowIndex = 0; // Скидаємо індекс перед пошуком

                // Завантажуємо існуючі книги для перевірки на дублікати
                var books = await GoogleSheetsService.GetBooksAsync();
                var similarBooks = new System.Collections.Generic.List<string>();

                if (books != null)
                {
                    // Нормалізуємо введену назву
                    string normalizedNew = newTitle.ToLower().Replace(" ", "").Replace("-", "").Replace("'", "").Replace("\"", "").Replace("«", "").Replace("»", "");

                    for (int i = 0; i < books.Count; i++)
                    {
                        var row = books[i];
                        if (row.Count > 0)
                        {
                            string existingTitle = row[0]?.ToString() ?? "";
                            string normalizedExisting = existingTitle.ToLower().Replace(" ", "").Replace("-", "").Replace("'", "").Replace("\"", "").Replace("«", "").Replace("»", "");

                            if (!string.IsNullOrEmpty(normalizedExisting) && !string.IsNullOrEmpty(normalizedNew))
                            {
                                // Критерії схожості
                                if (normalizedExisting.Contains(normalizedNew) || normalizedNew.Contains(normalizedExisting) ||
                                    (System.Math.Abs(normalizedNew.Length - normalizedExisting.Length) <= 5 && ComputeLevenshteinDistance(normalizedNew, normalizedExisting) <= 3))
                                {
                                    similarBooks.Add(existingTitle);
                                    // Зберігаємо індекс найпершого знайденого збігу, щоб потім зробити йому +1
                                    if (session.EditRowIndex == 0) session.EditRowIndex = i + 2;
                                }
                            }
                        }
                    }
                }

                if (similarBooks.Count > 0)
                {
                    SessionManager.UserStates[chatId] = UserState.WaitingForAddBookDuplicateCheck;

                    string warning = $"⚠️ **Знайдено схожі книги!**\nУ каталозі вже є книги з ідентичною або дуже схожою назвою:\n\n";

                    // Беремо унікальні та залишаємо перші 5
                    foreach (var b in System.Linq.Enumerable.Take(System.Linq.Enumerable.Distinct(similarBooks), 5))
                    {
                        warning += $"📖 {b}\n";
                    }
                    warning += "\nВиберіть, що робити далі:";

                    // Створюємо 3 вертикальні кнопки за твоїм шаблоном
                    var kb = new ReplyKeyboardMarkup(new[] {
                        new KeyboardButton[] { "➕ Додати нову" },
                        new KeyboardButton[] { "⬆️ Збільшити кількість на 1 у вже існуючої" },
                        new KeyboardButton[] { "❌ Скасувати" }
                    })
                    { ResizeKeyboard = true };

                    await botClient.SendMessage(chatId, warning, parseMode: ParseMode.Markdown, replyMarkup: kb, cancellationToken: cancellationToken);
                    return true;
                }

                // Якщо схожих немає - йдемо далі
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;
                await botClient.SendMessage(chatId, "👤 Введіть АВТОРА книги (або відправте `-`, якщо невідомий):", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                return true;
            }

            // НОВИЙ БЛОК: Вирішення конфлікту дублікатів
            if (state == UserState.WaitingForAddBookDuplicateCheck)
            {
                if (text == "➕ Додати нову")
                {
                    SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;
                    await botClient.SendMessage(chatId, "👤 Введіть АВТОРА книги (або відправте `-`, якщо невідомий):", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return true;
                }
                else if (text == "⬆️ Збільшити кількість на 1 у вже існуючої")
                {
                    var session = SessionManager.AdminBookSessions[chatId];
                    int rowIndex = session.EditRowIndex;

                    if (rowIndex == 0)
                    {
                        await botClient.SendMessage(chatId, "❌ Помилка: не вдалося знайти оригінальну книгу.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                        SessionManager.ClearSession(chatId);
                        return true;
                    }

                    var books = await GoogleSheetsService.GetBooksAsync();
                    if (books != null && books.Count >= rowIndex - 1)
                    {
                        var row = books[rowIndex - 2];

                        // Зчитуємо всі поточні дані з рядка
                        string t = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
                        string a = row.Count > 1 ? row[1]?.ToString() ?? "" : "";
                        string g = row.Count > 2 ? row[2]?.ToString() ?? "" : "";
                        string e = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "Так" : "Так";
                        int ava = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int da) ? da : 0 : 0;
                        int tot = row.Count > 5 ? int.TryParse(row[5]?.ToString(), out int dt) ? dt : 1 : 1;

                        // Перезаписуємо рядок, роблячи +1 до обох колонок (Доступно і Всього)
                        bool updated = await GoogleSheetsService.UpdateBookInCatalogAsync(rowIndex, t, a, g, e, ava + 1, tot + 1);

                        if (updated)
                            await botClient.SendMessage(chatId, $"✅ Кількість книги **{t}** успішно збільшено!\n📊 Новий баланс: {tot + 1} шт. (Доступно: {ava + 1})", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                        else
                            await botClient.SendMessage(chatId, $"❌ Помилка оновлення таблиці.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.SendMessage(chatId, $"❌ Не вдалося зчитати дані з таблиці.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    }

                    SessionManager.ClearSession(chatId);
                    return true;
                }
                else if (text == "❌ Скасувати" || text.ToLower() == "/cancel" || text == "❌ Скасувати дію")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "❌ Додавання книги скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return true;
                }
                else
                {
                    await botClient.SendMessage(chatId, "❌ Будь ласка, скористайтеся кнопками нижче:", cancellationToken: cancellationToken);
                    return true;
                }
            }
            // НОВИЙ БЛОК: Вирішення конфлікту дублікатів
            if (state == UserState.WaitingForAddBookDuplicateCheck)
            {
                if (text == "➕ Додати ще одну")
                {
                    SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;
                    // Прибираємо кнопки "Додати/Скасувати", щоб було зручно вводити автора
                    await botClient.SendMessage(chatId, "👤 Введіть АВТОРА книги (або відправте `-`, якщо невідомий):", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    return true;
                }
                else
                {
                    await botClient.SendMessage(chatId, "❌ Будь ласка, скористайтеся кнопками нижче:", cancellationToken: cancellationToken);
                    return true;
                }
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

                // Переводимо у новий стан очікування відповіді щодо обміну
                SessionManager.UserStates[chatId] = UserState.WaitingForExchangeExchangeStatus;

                // Виводимо клавіатуру з кнопками вибору
                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "🔄 Чи буде ЦЯ НОВА книга доступна для обміну в майбутньому?", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }

            // НОВИЙ БЛОК: Фіналізація обміну з урахуванням вибору статусу
            if (state == UserState.WaitingForExchangeExchangeStatus)
            {
                string exchangeStatus = (text.ToLower() == "ні") ? "Ні" : "Так";
                var session = SessionManager.AdminExchangeSessions[chatId];

                // 1. Записуємо в лог обміну
                await GoogleSheetsService.AddExchangeLogAsync(session.OldBookTitle, session.NewBookTitle, session.ReaderName);

                // 2. Списуємо стару книгу з балансу за точним індексом рядка
                await GoogleSheetsService.ProcessExchangeOutgoingBookAsync(session.OldBookRowIndex, session.OldBookTitle);

                // 3. Додаємо нову книгу (передаємо вибраний exchangeStatus замість захардкодженного "Так", кількість 1 шт)
                await GoogleSheetsService.AddBookToCatalogAsync(session.NewBookTitle, session.NewBookAuthor, session.NewBookGenre, "Доступна", exchangeStatus, 1);

                SessionManager.ClearSession(chatId);

                string exchText = exchangeStatus == "Ні" ? "Не обмінюється" : "Можна обмінювати";
                string successText = $"✅ **Обмін успішно завершено!**\n\n📖 Стара книга '**{session.OldBookTitle}**' списана з балансу.\n✨ Нова книга '**{session.NewBookTitle}**' додана до каталогу.\n📊 Статус обміну: *{exchText}*\n🗂 Запис внесено в аркуш 'Обмін'.";

                await botClient.SendMessage(chatId, successText, parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }            // 5. РУЧНА ВИДАЧА ТА ПОВЕРНЕННЯ
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

                await GoogleSheetsService.AddBorrowingAsync(session.BookId!, session.ReaderName!, "Офлайн читач", session.ReaderContact!, 0, DateTime.Now.AddDays(30));

                // БУЛО: await GoogleSheetsService.UpdateBookStatusAsync(session.BookId!, "Читають");
                // СТАЛО: Робимо -1 до доступних книг!
                await GoogleSheetsService.ChangeAvailableCountAsync(session.CatalogRowIndex, -1);

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


        // Розумний алгоритм для пошуку помилок та одруківок
        private static int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;
            int[] v0 = new int[t.Length + 1];
            int[] v1 = new int[t.Length + 1];
            for (int i = 0; i < v0.Length; i++) v0[i] = i;
            for (int i = 0; i < s.Length; i++)
            {
                v1[0] = i + 1;
                for (int j = 0; j < t.Length; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    v1[j + 1] = System.Math.Min(System.Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
                }
                for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
            }
            return v1[t.Length];
        }
    }
}