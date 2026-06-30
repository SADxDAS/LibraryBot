using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace LibraryBot.Handlers
{
    public static class AdminStateHandler
    {
        public static async Task<bool> HandleAsync(ITelegramBotClient botClient, long chatId, string text, UserState state, CancellationToken cancellationToken, Telegram.Bot.Types.Message? message = null)
        {
            if (!string.IsNullOrEmpty(text) && (text.ToLower() == "/cancel" || text == "❌ Скасувати дію" || text == "❌ Скасувати"))
            {
                SessionManager.ClearSession(chatId);
                // Убираем клавиатуру и помечаем действие как обработанное
                await botClient.SendMessage(chatId, "ОБРОБЛЕНО\nДію скасовано.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                // Показываем главное меню
                await botClient.SendMessage(chatId, "Меню:", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // -------------------------------------------------------------
            // ОБРОБКА ТЕКСТОВИХ ВВЕДЕНЬ ДЛЯ МАЙСТРА ДОДАВАННЯ/РЕДАГУВАННЯ
            // -------------------------------------------------------------

            if (state == UserState.WaitingForAddBookTitle)
            {
                string newTitle = text.Trim();
                var session = SessionManager.AdminBookSessions[chatId];
                session.Title = newTitle;

                if (session.EditRowIndex == -1)
                {
                    var books = await LibraryDbService.GetBooksAsync();
                    var similarBooks = new System.Collections.Generic.List<string>();
                    int firstFoundId = 0;

                    if (books != null)
                    {
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
                                    if (normalizedExisting.Contains(normalizedNew) || normalizedNew.Contains(normalizedExisting) ||
                                        (Math.Abs(normalizedNew.Length - normalizedExisting.Length) <= 5 && ComputeLevenshteinDistance(normalizedNew, normalizedExisting) <= 3))
                                    {
                                        similarBooks.Add(existingTitle);
                                        if (firstFoundId == 0 && row.Count > LibraryDbService.COL_CATALOG_ID)
                                            firstFoundId = Convert.ToInt32(row[LibraryDbService.COL_CATALOG_ID]);
                                    }
                                }
                            }
                        }
                    }

                    if (similarBooks.Count > 0)
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForAddBookDuplicateCheck;
                        session.EditRowIndex = firstFoundId;

                        string warning = $"⚠️ <b>Знайдено схожі книги!</b>\nУ каталозі вже є (або були раніше списані) книги зі схожою назвою:\n\n";
                        foreach (var b in similarBooks.Distinct().Take(5))
                        {
                            warning += $"📖 {TextUtils.EscapeHtml(b)}\n";
                        }
                        warning += "\nВиберіть, що робити далі:";

                        // Строим клавиатуру с отдельными кнопками для увеличения конкретной похожей книги
                        var rows = new System.Collections.Generic.List<KeyboardButton[]>();
                        // Кнопки для конкретных похожих книг (до 5)
                        foreach (var b in similarBooks.Distinct().Take(5))
                        {
                            rows.Add(new KeyboardButton[] { $"⬆️ +1 {b}" });
                        }
                        // Общая кнопка добавить новую и отмена
                        rows.Add(new KeyboardButton[] { "➕ Додати нову" });
                        rows.Add(new KeyboardButton[] { "❌ Скасувати" });

                        var kb = new ReplyKeyboardMarkup(rows.ToArray()) { ResizeKeyboard = true };

                        await botClient.SendMessage(chatId, warning, parseMode: ParseMode.Html, replyMarkup: kb, cancellationToken: cancellationToken);
                        return true;
                    }
                }

                session.CurrentStep = 2;
                await WizardHelper.SendOrUpdateWizardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                return true;
            }

            if (state == UserState.WaitingForAddBookDuplicateCheck)
            {
                if (text == "➕ Додати нову" || text == "➕ Додати ще одну")
                {
                    var session = SessionManager.AdminBookSessions[chatId];
                    session.EditRowIndex = -1;
                    session.CurrentStep = 2;
                    SessionManager.UserStates[chatId] = UserState.None;

                    // Прибираємо Reply-клавіатуру
                    var removeMsg = await botClient.SendMessage(chatId, "⏳ Продовжуємо...", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: cancellationToken);
                    try { await botClient.DeleteMessage(chatId, removeMsg.MessageId, cancellationToken); } catch { }

                    // ДОДАНО: Видаляємо старе вікно майстра, яке "поїхало" вгору
                    try { await botClient.DeleteMessage(chatId, session.WizardMessageId, cancellationToken); } catch { }

                    // ВИПРАВЛЕНО: Передаємо null замість session.WizardMessageId, 
                    // щоб бот надіслав НОВЕ повідомлення Майстра в самий низ чату!
                    await WizardHelper.SendOrUpdateWizardAsync(botClient, chatId, session, null, cancellationToken);
                    return true;
                }
                else if (text.StartsWith("⬆️ +1 "))
                {
                    // Пользователь выбрал конкретную похожую книгу для увеличения количества
                    string chosenTitle = text.Substring("⬆️ +1 ".Length);
                    var session = SessionManager.AdminBookSessions[chatId];
                    var books = await LibraryDbService.GetBooksAsync();
                    var row = books?.FirstOrDefault(b => b.Count > 0 && (b[0]?.ToString() ?? "") == chosenTitle);

                    if (row != null)
                    {
                        int bookId = row.Count > LibraryDbService.COL_CATALOG_ID ? Convert.ToInt32(row[LibraryDbService.COL_CATALOG_ID]) : 0;
                        string t = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
                        string a = row.Count > 1 ? row[1]?.ToString() ?? "" : "";
                        string g = row.Count > 2 ? row[2]?.ToString() ?? "" : "";
                        string e = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "Так" : "Так";
                        int ava = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int da) ? da : 0 : 0;
                        int tot = row.Count > 5 ? int.TryParse(row[5]?.ToString(), out int dt) ? dt : 1 : 1;

                        bool updated = await LibraryDbService.UpdateBookInCatalogAsync(bookId, t, a, g, e, ava + 1, tot + 1);
                        try { await botClient.DeleteMessage(chatId, session.WizardMessageId, cancellationToken); } catch { }

                        if (updated)
                            await botClient.SendMessage(chatId, $"✅ Кількість книги <b>{TextUtils.EscapeHtml(t)}</b> успішно збільшено!\n📊 Новий баланс: {tot + 1} шт. (Доступно: {ava + 1})", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
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
                else if (text == "⬆️ Збільшити кількість на 1 у вже існуючої")
                {
                    var session = SessionManager.AdminBookSessions[chatId];
                    int bookId = session.EditRowIndex;

                    if (bookId == 0)
                    {
                        // Если нет конкретного id, предложим выбрать из похожих
                        await botClient.SendMessage(chatId, "❌ Не вдалося визначити конкретну книгу. Будь ласка, оберіть одну з перерахованих у списку, або додайте нову.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                        SessionManager.ClearSession(chatId);
                        return true;
                    }

                    var books = await LibraryDbService.GetBooksAsync();
                    var row = books?.FirstOrDefault(b => b.Count > LibraryDbService.COL_CATALOG_ID && Convert.ToInt32(b[LibraryDbService.COL_CATALOG_ID]) == bookId);

                    if (row != null)
                    {
                        string t = row.Count > 0 ? row[0]?.ToString() ?? "" : "";
                        string a = row.Count > 1 ? row[1]?.ToString() ?? "" : "";
                        string g = row.Count > 2 ? row[2]?.ToString() ?? "" : "";
                        string e = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "Так" : "Так";
                        int ava = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int da) ? da : 0 : 0;
                        int tot = row.Count > 5 ? int.TryParse(row[5]?.ToString(), out int dt) ? dt : 1 : 1;

                        bool updated = await LibraryDbService.UpdateBookInCatalogAsync(bookId, t, a, g, e, ava + 1, tot + 1);

                        // Видаляємо старого майстра, щоб не засмічувати чат
                        try { await botClient.DeleteMessage(chatId, session.WizardMessageId, cancellationToken); } catch { }

                        if (updated)
                            await botClient.SendMessage(chatId, $"✅ Кількість книги <b>{TextUtils.EscapeHtml(t)}</b> успішно збільшено!\n📊 Новий баланс: {tot + 1} шт. (Доступно: {ava + 1})", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
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
                else if (text == "❌ Скасувати")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "Дію скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
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
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (text != "-") session.Author = text;
                    session.CurrentStep = 3;
                    await WizardHelper.SendOrUpdateWizardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            // --- НОВАЯ ЛОГИКА ТЕКСТОВОГО ВВОДА ДЛЯ МЕНЮ РЕДАКТИРОВАНИЯ ---
            if (state == UserState.WaitingForEditBookTitle)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    session.Title = text;
                    SessionManager.UserStates[chatId] = UserState.None; // Сбрасываем ожидание текста
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForEditBookAuthor)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    session.Author = text;
                    SessionManager.UserStates[chatId] = UserState.None;
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForAddBookGenre)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (text != "-") session.Genre = text;
                    session.CurrentStep = 4;
                    await WizardHelper.SendOrUpdateWizardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            // --- ПОВЕРНУТИЙ БЛОК: Обробка ручного введення кількості в Мастері ---
            if (state == UserState.WaitingForAddBookQuantity)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (int.TryParse(text, out int qty) && qty > 0)
                    {
                        session.CurrentAvailable = qty;
                        session.CurrentTotal = qty;
                        SessionManager.UserStates[chatId] = UserState.None;
                        // Підтвердження змін: оновлюємо інтерфейс або показуємо повідомлення адміну
                        await botClient.SendMessage(chatId, $"✅ Кількість встановлено: {qty} шт.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    }
                    else
                    {
                        var msg = await botClient.SendMessage(chatId, "❌ Введіть коректне число (більше нуля).", cancellationToken: cancellationToken);
                        _ = Task.Run(async () => { await Task.Delay(3000); try { await botClient.DeleteMessage(chatId, msg.MessageId, CancellationToken.None); } catch { } });
                    }
                }
                return true;
            }
            if (state == UserState.WaitingForAddBookQuantity)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    session.Genre = text;
                    SessionManager.UserStates[chatId] = UserState.None;
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForEditBookQuantity)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    // Обработка +1, -2 или точного числа
                    if (text.StartsWith("+") || text.StartsWith("-"))
                    {
                        if (int.TryParse(text, out int diff))
                        {
                            session.CurrentTotal += diff;
                            session.CurrentAvailable = Math.Max(0, session.CurrentAvailable + diff);
                            SessionManager.UserStates[chatId] = UserState.None;
                        }
                    }
                    else if (int.TryParse(text, out int exactQty) && exactQty >= 0)
                    {
                        int diff = exactQty - session.CurrentTotal;
                        session.CurrentTotal = exactQty;
                        session.CurrentAvailable = Math.Max(0, session.CurrentAvailable + diff);
                        SessionManager.UserStates[chatId] = UserState.None;
                    }

                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }

                    if (SessionManager.UserStates[chatId] == UserState.None)
                    {
                        await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                    }
                    else
                    {
                        var msg = await botClient.SendMessage(chatId, "❌ Введіть коректне число (наприклад `5` або `+1`).", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        _ = Task.Run(async () => { await Task.Delay(3000); try { await botClient.DeleteMessage(chatId, msg.MessageId, CancellationToken.None); } catch { } });
                    }
                }
                return true;
            }

            // --- РЕДАКТИРОВАНИЕ ЧЕРЕЗ МЕНЮ (новые обработчики) ---
            if (state == UserState.WaitingForEditMenuTitleInput)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (text != "-") session.Title = text;
                    SessionManager.UserStates[chatId] = UserState.None;
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForEditMenuAuthorInput)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (text != "-") session.Author = text;
                    SessionManager.UserStates[chatId] = UserState.None;
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForEditMenuGenreInput)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    if (text != "-") session.Genre = text;
                    SessionManager.UserStates[chatId] = UserState.None;
                    try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                }
                return true;
            }

            if (state == UserState.WaitingForEditMenuQuantityInput)
            {
                if (SessionManager.AdminBookSessions.TryGetValue(chatId, out var session))
                {
                    bool isValid = false;

                    // Обработка +1, -2 или точного числа
                    if (text.StartsWith("+") || text.StartsWith("-"))
                    {
                        if (int.TryParse(text, out int diff))
                        {
                            session.CurrentTotal += diff;
                            session.CurrentAvailable = Math.Max(0, session.CurrentAvailable + diff);
                            isValid = true;
                        }
                    }
                    else if (int.TryParse(text, out int exactQty) && exactQty >= 0)
                    {
                        int diff = exactQty - session.CurrentTotal;
                        session.CurrentTotal = exactQty;
                        session.CurrentAvailable = Math.Max(0, session.CurrentAvailable + diff);
                        isValid = true;
                    }

                    if (isValid)
                    {
                        SessionManager.UserStates[chatId] = UserState.None;
                        try { if (message != null) await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                        await WizardHelper.SendOrUpdateEditDashboardAsync(botClient, chatId, session, session.WizardMessageId, cancellationToken);
                    }
                    else
                    {
                        var msg = await botClient.SendMessage(chatId, "❌ Введіть коректне число (наприклад `5` або `+1`).", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        _ = Task.Run(async () => { await Task.Delay(3000); try { await botClient.DeleteMessage(chatId, msg.MessageId, CancellationToken.None); } catch { } });
                    }
                }
                return true;
            }

            // -------------------------------------------------------------
            // ІНШІ СТАНДАРТНІ КОМАНДИ (Видалення, Обмін, Ручна видача)
            // -------------------------------------------------------------

            if (state == UserState.WaitingForDeleteSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForDeletionAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            if (state == UserState.WaitingForEditSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForEditingAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

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

                SessionManager.UserStates[chatId] = UserState.WaitingForExchangeExchangeStatus;
                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "🔄 Чи буде ЦЯ НОВА книга доступна для обміну в майбутньому?", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForExchangeExchangeStatus)
            {
                string exchangeStatus = (text.ToLower() == "ні") ? "Ні" : "Так";
                var session = SessionManager.AdminExchangeSessions[chatId];

                // Виправлено можливий NULL
                await LibraryDbService.AddExchangeLogAsync(session.OldBookTitle ?? "Невідомо", session.NewBookTitle ?? "Невідомо", session.ReaderName ?? "Невідомо");

                using (await AsyncKeyedLock.LockAsync($"book:{session.OldBookRowIndex}", cancellationToken))
                {
                    await LibraryDbService.ProcessExchangeOutgoingBookAsync(session.OldBookRowIndex, session.OldBookTitle ?? "");
                }

                // Виправлено можливий NULL
                await LibraryDbService.AddBookToCatalogAsync(
                    session.NewBookTitle ?? "Без назви",
                    session.NewBookAuthor ?? "Невідомий автор",
                    session.NewBookGenre ?? "Не вказано",
                    exchangeStatus,
                    1
                );

                SessionManager.ClearSession(chatId);

                string exchText = exchangeStatus == "Ні" ? "Не обмінюється" : "Можна обмінювати";
                string successText = $"✅ <b>Обмін успішно завершено!</b>\n\n📖 Стара книга '<b>{TextUtils.EscapeHtml(session.OldBookTitle ?? "")}</b>' списана з балансу.\n✨ Нова книга '<b>{TextUtils.EscapeHtml(session.NewBookTitle ?? "")}</b>' додана до каталогу.\n📊 Статус обміну: <i>{exchText}</i>\n🗂 Запис внесено в базу.";

                await botClient.SendMessage(chatId, successText, parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

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

                using (await AsyncKeyedLock.LockAsync($"book:{session.CatalogRowIndex}", cancellationToken))
                {
                    if (await LibraryDbService.TryDecrementAvailableAsync(session.CatalogRowIndex))
                    {
                        // Виправлено можливий NULL
                        await LibraryDbService.AddBorrowingAsync(
                            session.BookId ?? "0",
                            session.ReaderName ?? "Невідомий читач",
                            "Офлайн читач",
                            session.ReaderContact ?? "Не вказано",
                            0,
                            DateTime.Now.AddDays(30)
                        );
                        SessionManager.ClearSession(chatId);
                        await botClient.SendMessage(chatId, $"✅ Готово! Книга '<b>{TextUtils.EscapeHtml(session.BookId.ToString())}</b>' видана читачу <b>{TextUtils.EscapeHtml(session.ReaderName ?? "")}</b> ({TextUtils.EscapeHtml(session.ReaderContact ?? "")}).", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    }
                    else
                    {
                        SessionManager.ClearSession(chatId);
                        await botClient.SendMessage(chatId, $"😔 Усі примірники книги '<b>{TextUtils.EscapeHtml(session.BookId.ToString())}</b>' вже на руках — видачу не виконано.", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    }
                }
                return true;
            }
            if (state == UserState.WaitingForManualReturnSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForManualReturnAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            return false;
        }

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
                    v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
                }
                for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
            }
            return v1[t.Length];
        }
    }
}