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
    public static class CallbackHandler
    {
        public static async Task HandleAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
        {
            if (callbackQuery.Data == null) return;
            long chatId = callbackQuery.Message!.Chat.Id;

            if (callbackQuery.Data.StartsWith("page_"))
            {
                int page = int.Parse(callbackQuery.Data.Split('_')[1]);
                await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, chatId, page, callbackQuery.Message.MessageId, cancellationToken);
            }
            else if (callbackQuery.Data == "close_catalog")
            {
                await botClient.DeleteMessage(chatId, callbackQuery.Message.MessageId, cancellationToken);
                await botClient.SendMessage(chatId, "Каталог закрито 📕.", cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data == "jump_page")
            {
                SessionManager.UserStates[chatId] = UserState.WaitingForPageNumber;
                await botClient.SendMessage(chatId, "🔢 Введіть номер сторінки:", cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data.StartsWith("act_b_"))
            {
                int bookId = int.Parse(callbackQuery.Data.Substring(6));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    int disponible = row.Count > GoogleSheetsService.COL_CATALOG_AVAILABLE
                        ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0
                        : 0;

                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";

                    SessionManager.BorrowSessions[chatId] = new UserBorrowingSession { BookTitle = title, CatalogRowIndex = bookId };
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowRealName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"📖 Ви обрали книгу:\n<b>{title}</b>.\n\n👤 Будь ласка, введіть ваше **Справжнє Ім'я та Прізвище**:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_r_"))
            {
                int bookId = int.Parse(callbackQuery.Data.Substring(6));
                string tgName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username))
                    tgName += $" (@{callbackQuery.Message.Chat.Username})";

                var userBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(tgName);
                if (!userBooks.Any(b => b.BookId == bookId))
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Ви не можете повернути цю книгу, бо вона записана не на вас.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    return;
                }

                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);
                if (row != null)
                {
                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";

                    var request = new PendingRequest
                    {
                        Type = RequestType.Return,
                        UserId = chatId,
                        UserName = tgName,
                        BookTitle = title,
                        CatalogRowIndex = bookId
                    };

                    await GoogleSheetsService.AddPendingRequestAsync(request);

                    var adminKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Прийняти", $"req_apr_{request.RequestId}"),
                        InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                    });

                    string adminMsg = $"📥 <b>ЗАПИТ НА ПОВЕРНЕННЯ</b>\n\n👤 Читач: {TextUtils.EscapeHtml(tgName)}\n📖 Книга: <b>{TextUtils.EscapeHtml(title)}</b>";
                    foreach (var adminId in SessionManager.AdminIds)
                    {
                        try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Html, replyMarkup: adminKeyboard); }
                        catch (Exception ex) { Console.WriteLine($"[Telegram API] Не вдалося відправити запит адміну {adminId}: {ex.Message}"); }
                    }

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"⏳ Запит на повернення книги '<b>{title}</b>' відправлено. Дочекайтеся підтвердження.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("req_apr_") || callbackQuery.Data.StartsWith("req_rej_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                bool isApprove = callbackQuery.Data.StartsWith("req_apr_");
                string reqId = callbackQuery.Data.Substring(8);

                // Критична секція по запиту: два адміни (або подвійний тап) не оброблять його двічі.
                using var reqLock = await AsyncKeyedLock.LockAsync($"request:{reqId}", cancellationToken);

                var request = await GoogleSheetsService.GetPendingRequestAsync(reqId);
                if (request == null)
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Цей запит вже був оброблений іншим адміном або скасований.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    return;
                }

                string originalText = callbackQuery.Message!.Text ?? "Запит";

                if (isApprove)
                {
                    if (request.Type == RequestType.UserExchange)
                    {
                        var choiceKeyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🟢 Так, обмінюється", $"userex_yes_{reqId}"),
                                InlineKeyboardButton.WithCallbackData("🔴 Ні, не обмінюється", $"userex_no_{reqId}")
                            }
                        });

                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, TextUtils.EscapeHtml(originalText) + "\n\n🔄 <b>Визначте статус обміну для цієї нової книги:</b>", parseMode: ParseMode.Html, replyMarkup: choiceKeyboard, cancellationToken: cancellationToken);
                        return;
                    }

                    await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                    if (request.Type == RequestType.Borrow)
                    {
                        // Критична секція по книзі + перевірка наявності В МОМЕНТ схвалення:
                        // поки запит чекав, примірники могли розібрати — тоді видачу не виконуємо.
                        using var bookLock = await AsyncKeyedLock.LockAsync($"book:{request.CatalogRowIndex}", cancellationToken);
                        if (await GoogleSheetsService.TryDecrementAvailableAsync(request.CatalogRowIndex))
                        {
                            DateTime dueDate = DateTime.Now.AddDays(request.BorrowDays);
                            await GoogleSheetsService.AddBorrowingAsync(request.BookTitle, request.RealName, request.UserName, request.Contact ?? "", request.UserId, dueDate);
                            await botClient.SendMessage(request.UserId, $"🎉 <b>Ваш запит схвалено!</b>\nКнигу <b>{TextUtils.EscapeHtml(request.BookTitle)}</b> закріплено за вами.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        }
                        else
                        {
                            await botClient.SendMessage(request.UserId, $"😔 На жаль, поки ваш запит на книгу <b>{TextUtils.EscapeHtml(request.BookTitle)}</b> розглядався, всі примірники вже розібрали.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                            try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Немає вільних примірників — видачу не виконано.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        }
                    }
                    else if (request.Type == RequestType.Return)
                    {
                        using var bookLock = await AsyncKeyedLock.LockAsync($"book:{request.CatalogRowIndex}", cancellationToken);
                        await GoogleSheetsService.ChangeAvailableCountAsync(request.CatalogRowIndex, 1);
                        await GoogleSheetsService.LogReturnDateAsync(request.BookTitle, request.UserId);
                        await botClient.SendMessage(request.UserId, $"✅ <b>Повернення підтверджено!</b>\nДякуємо, що повернули книгу <b>{TextUtils.EscapeHtml(request.BookTitle)}</b>.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    }

                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, TextUtils.EscapeHtml(originalText) + "\n\n✅ <b>СХВАЛЕНО</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                else
                {
                    await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                    string safeTitle = TextUtils.EscapeHtml(request.BookTitle);
                    string rejectMsg = request.Type == RequestType.UserExchange
                        ? $"❌ Ваш запит на обмін книги\n<b>{safeTitle}</b>\nбуло відхилено адміністратором."
                        : (request.Type == RequestType.Borrow
                            ? $"❌ Ваш запит на книгу\n{safeTitle}\nбуло відхилено адміністратором."
                            : $"❌ Ваш запит на повернення книги\n{safeTitle}\nвідхилено.");

                    await botClient.SendMessage(request.UserId, rejectMsg, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, TextUtils.EscapeHtml(originalText) + "\n\n❌ <b>ВІДХИЛЕНО</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // ФІКС ТУТ: Розумне вирахування довжини текстового тригера кнопки статусу
            else if (callbackQuery.Data.StartsWith("userex_yes_") || callbackQuery.Data.StartsWith("userex_no_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                bool canExchange = callbackQuery.Data.StartsWith("userex_yes_");

                // Якщо увімкнено так - зрізаємо 11 літер, якщо ні - 10 літер
                string reqId = canExchange ? callbackQuery.Data.Substring(11) : callbackQuery.Data.Substring(10);

                // Критична секція по запиту: один запит обробляється лише раз.
                using var reqLock = await AsyncKeyedLock.LockAsync($"request:{reqId}", cancellationToken);

                var request = await GoogleSheetsService.GetPendingRequestAsync(reqId);
                if (request == null)
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Запит уже оброблено.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    return;
                }

                await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                string exchangeStatus = canExchange ? "Так" : "Ні";

                await GoogleSheetsService.AddBookToCatalogAsync(request.NewBookTitle, request.NewBookAuthor, request.NewBookGenre, exchangeStatus, 1);
                using (await AsyncKeyedLock.LockAsync($"book:{request.CatalogRowIndex}", cancellationToken))
                {
                    await GoogleSheetsService.ProcessExchangeOutgoingBookAsync(request.CatalogRowIndex, request.BookTitle);
                }
                await GoogleSheetsService.AddExchangeLogAsync(request.BookTitle, request.NewBookTitle, request.UserName);

                await botClient.SendMessage(request.UserId, $"🎉 <b>Ваш запит на обмін схвалено!</b>\n\nВи можете забрати книгу <b>{TextUtils.EscapeHtml(request.BookTitle)}</b>, а свою передати баристі. Дякуємо за обмін! 💚", parseMode: ParseMode.Html, cancellationToken: cancellationToken);

                string originalText = callbackQuery.Message!.Text ?? "";
                if (originalText.Contains("\n\n🔄 Визначте статус обміну"))
                {
                    originalText = originalText.Split(new[] { "\n\n🔄 Визначте статус обміну" }, StringSplitOptions.None)[0];
                }

                string finalStatusText = canExchange ? "СХВАЛЕНО (З можливістю обміну)" : "СХВАЛЕНО (Без подальшого обміну)";
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, TextUtils.EscapeHtml(originalText) + $"\n\n✅ <b>{finalStatusText}</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data.StartsWith("userex_sel_"))
            {
                int bookId = int.Parse(callbackQuery.Data.Substring(11));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    int disponible = row.Count > GoogleSheetsService.COL_CATALOG_AVAILABLE
                        ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0
                        : 0;

                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Цю книгу вже встигли забрати.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string libBookTitle = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";
                    if (!SessionManager.UserExchangeSessions.TryGetValue(chatId, out var session))
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Сесію обміну вже завершено або застаріло. Почніть заново.", showAlert: true, cancellationToken: cancellationToken); } catch { }
                        return;
                    }
                    session.LibraryBookTitle = libBookTitle;
                    session.LibraryBookRowIndex = bookId;

                    string telegramName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                    if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username)) telegramName += $" (@{callbackQuery.Message.Chat.Username})";

                    var request = new PendingRequest
                    {
                        Type = RequestType.UserExchange,
                        UserId = chatId,
                        UserName = telegramName,
                        BookTitle = libBookTitle,
                        CatalogRowIndex = bookId,
                        NewBookTitle = session.Title,
                        NewBookAuthor = session.Author,
                        NewBookGenre = session.Genre
                    };

                    await GoogleSheetsService.AddPendingRequestAsync(request);

                    var adminKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                        InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                    });

                    string adminMsg = $"📩 <b>ЗАПИТ НА ОБМІН (БУККРОСИНГ)</b>\n\n" +
                                      $"👤 Читач: {TextUtils.EscapeHtml(telegramName)}\n" +
                                      $"📥 <b>Принесе в бібліотеку:</b>\n📖 «{TextUtils.EscapeHtml(session.Title)}» ({TextUtils.EscapeHtml(session.Author)} / Жанр: {TextUtils.EscapeHtml(session.Genre)})\n\n" +
                                      $"📤 <b>Хоче забрати натомість:</b>\n📖 «{TextUtils.EscapeHtml(libBookTitle)}»";

                    foreach (var adminId in SessionManager.AdminIds)
                    {
                        try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Html, replyMarkup: adminKeyboard); }
                        catch (Exception ex) { Console.WriteLine($"[Telegram API] Не вдалося відправити запит адміну {adminId}: {ex.Message}"); }
                    }

                    SessionManager.ClearSession(chatId);
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"⏳ Запит на обмін успішно надіслано адміністраторам!\n\nВи віддаєте: <b>{TextUtils.EscapeHtml(session.Title)}</b>\nЗабираєте: <b>{TextUtils.EscapeHtml(libBookTitle)}</b>\n\nДочекайтеся підтвердження баристи.", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_ext_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(8));
                bool success = await GoogleSheetsService.ExtendBorrowingAsync(rowIndex);

                if (success)
                {
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, "✅ <b>Термін здачі успішно подовжено на 1 місяць (30 днів)!</b>\nНаступного разу ви не зможете скористатися цією функцією. Приємного читання!", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
                else
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Помилка подовження. Зверніться до адміністратора.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                }
            }
            else if (callbackQuery.Data.StartsWith("act_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(8));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "Невідома книга";
                    var confirmKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new InlineKeyboardButton[] { InlineKeyboardButton.WithCallbackData("✅ Так, видалити", $"conf_del_{bookId}"), InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"canc_del_{bookId}") }
                    });
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⚠️ Ви дійсно хочете безповоротно видалити книгу <b>{TextUtils.EscapeHtml(title)}</b> з каталогу?", parseMode: ParseMode.Html, replyMarkup: confirmKeyboard, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("conf_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";

                    // Не даємо списати книгу, якщо її примірники ще на руках у читачів.
                    int onLoan = await GoogleSheetsService.CountActiveBorrowingsAsync(title);
                    if (onLoan > 0)
                    {
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"🚫 Не можна списати книгу <b>{TextUtils.EscapeHtml(title)}</b>: {onLoan} прим. зараз на руках у читачів.\nСпочатку дочекайтеся повернення всіх примірників.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                        return;
                    }

                    bool deleted = await GoogleSheetsService.DeleteBookByIdAsync(bookId);
                    if (deleted)
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"✅ Книгу <b>{TextUtils.EscapeHtml(title)}</b> успішно видалено з каталогу.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    else
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"❌ Сталася помилка при видаленні книги <b>{TextUtils.EscapeHtml(title)}</b>.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("canc_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, "❌ Видалення скасовано.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data.StartsWith("act_edit_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    string oldTitle = row.Count > GoogleSheetsService.COL_CATALOG_TITLE ? row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "" : "";
                    string oldAuthor = row.Count > GoogleSheetsService.COL_CATALOG_AUTHOR ? row[GoogleSheetsService.COL_CATALOG_AUTHOR]?.ToString() ?? "" : "";
                    string oldGenre = row.Count > GoogleSheetsService.COL_CATALOG_GENRE ? row[GoogleSheetsService.COL_CATALOG_GENRE]?.ToString() ?? "" : "";
                    string oldExchange = row.Count > GoogleSheetsService.COL_CATALOG_EXCHANGE ? row[GoogleSheetsService.COL_CATALOG_EXCHANGE]?.ToString()?.Trim() ?? "Так" : "Так";

                    int oldAvailable = row.Count > GoogleSheetsService.COL_CATALOG_AVAILABLE ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_AVAILABLE]?.ToString(), out int ava) ? ava : 0 : 0;
                    int oldTotal = row.Count > GoogleSheetsService.COL_CATALOG_TOTAL ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_TOTAL]?.ToString(), out int tot) ? tot : 1 : 1;

                    SessionManager.AdminBookSessions[chatId] = new AdminBookSession
                    {
                        EditRowIndex = bookId,
                        Title = oldTitle,
                        Author = oldAuthor,
                        Genre = oldGenre,
                        ExchangeStatus = oldExchange,
                        CurrentAvailable = oldAvailable,
                        CurrentTotal = oldTotal
                    };

                    SessionManager.UserStates[chatId] = UserState.WaitingForEditBookTitle;
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"✏️ Редагуємо книгу: <b>{TextUtils.EscapeHtml(oldTitle)}</b>\n\nВведіть НОВУ НАЗВУ (або відправте <code>-</code> щоб залишити без змін):", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_exch_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    int disponible = row.Count > GoogleSheetsService.COL_CATALOG_AVAILABLE ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0 : 0;
                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги зараз на руках. Обмін неможливий.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";

                    SessionManager.AdminExchangeSessions[chatId] = new AdminExchangeSession { OldBookRowIndex = bookId, OldBookTitle = title };
                    SessionManager.UserStates[chatId] = UserState.WaitingForExchangeReaderName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"Selected: <b>{TextUtils.EscapeHtml(title)}</b>.\n👤 Введіть Ім'я в Telegram (або контакт) читача, який робить обмін:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data == "view_overdue")
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                var overdueList = await GoogleSheetsService.GetOverdueBorrowingsAsync();

                if (overdueList.Count == 0)
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "✅ Наразі боржників немає!", showAlert: true, cancellationToken: cancellationToken); } catch { }
                    return;
                }

                string msg = "🚨 <b>СПИСОК БОРЖНИКІВ</b>\n\n";
                int count = 1;
                foreach (var item in overdueList)
                {
                    // Екрануємо дані користувача — імена/назви можуть містити < > & і ламати розмітку.
                    msg += $"👤 <b>{count}. {TextUtils.EscapeHtml(item.Name)}</b>\n";
                    msg += $"📖 Книга: {TextUtils.EscapeHtml(item.Title)}\n";
                    msg += $"📞 Контакт: <code>{TextUtils.EscapeHtml(item.Contact)}</code>\n";
                    msg += $"📅 Дедлайн був: {TextUtils.EscapeHtml(item.DueDate)}\n";
                    msg += "➖➖➖➖➖➖➖➖\n";
                    count++;

                    // Захист від занадто довгого повідомлення (ліміт Телеграму)
                    if (msg.Length > 3800)
                    {
                        msg += "<i>(Показано не всіх боржників через ліміт символів)</i>";
                        break;
                    }
                }

                await botClient.SendMessage(chatId, msg, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                try { await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken); } catch { }
            }
            // Гортання сторінок для списку видач
            else if (callbackQuery.Data.StartsWith("borrow_page_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int page = int.Parse(callbackQuery.Data.Replace("borrow_page_", ""));

                var activeList = await GoogleSheetsService.GetActiveBorrowingsAsync();
                if (activeList.Count == 0) return;

                int pageSize = 10;
                int totalPages = (int)Math.Ceiling(activeList.Count / (double)pageSize);

                var pageItems = activeList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

                string text = $"📋 <b>СПИСОК ВИДАНИХ КНИГ (Сторінка {page} з {totalPages})</b>\n\n";
                int count = (page - 1) * pageSize + 1;

                foreach (var item in pageItems)
                {
                    text += $"👤 <b>{count}. {TextUtils.EscapeHtml(item.Name)}</b>\n";
                    text += $"📖 Книга: {TextUtils.EscapeHtml(item.Title)}\n";
                    text += $"📞 Контакт: <code>{TextUtils.EscapeHtml(item.Contact)}</code>\n";
                    text += $"📅 Повернути до: {TextUtils.EscapeHtml(item.DueDate)}\n";
                    text += "➖➖➖➖➖➖➖➖\n";
                    count++;
                }

                // Використовуємо наш допоміжний метод з команди для генерації кнопок
                var inlineKeyboard = LibraryBot.Commands.AdminBorrowingsListCommand.GetPaginationKeyboard(page, totalPages);                // Оновлюємо текст поточного повідомлення
                await botClient.EditMessageText(
                    chatId: chatId,
                    messageId: callbackQuery.Message.MessageId,
                    text: text,
                    parseMode: Telegram.Bot.Types.Enums.ParseMode.Html,
                    replyMarkup: inlineKeyboard,
                    cancellationToken: cancellationToken
                );

                try { await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken); } catch { }
            }
            else if (callbackQuery.Data.StartsWith("act_man_b_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";
                    int disponible = row.Count > GoogleSheetsService.COL_CATALOG_AVAILABLE ? int.TryParse(row[GoogleSheetsService.COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0 : 0;

                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    SessionManager.AdminSessions[chatId] = new ManualBorrowingSession { BookId = title, CatalogRowIndex = bookId };
                    SessionManager.UserStates[chatId] = UserState.WaitingForManualReaderName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"📖 Обрано: <b>{TextUtils.EscapeHtml(title)}</b>.\n👤 Введіть ПІБ читача (офлайн користувача), якому видається книга:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_man_r_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int bookId = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();
                var row = FindBookById(books, bookId);

                if (row != null)
                {
                    string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";

                    using (await AsyncKeyedLock.LockAsync($"book:{bookId}", cancellationToken))
                    {
                        await GoogleSheetsService.ChangeAvailableCountAsync(bookId, 1);
                        await GoogleSheetsService.LogReturnDateAsync(title);
                    }

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"✅ Книгу '<b>{title}</b>' успішно повернуто вручну від офлайн-користувача! Статус оновлено.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("period_"))
            {
                int days = int.Parse(callbackQuery.Data.Split('_')[1]);
                // Захист від подвійного натискання: перший дотик уже міг очистити сесію.
                if (!SessionManager.BorrowSessions.TryGetValue(chatId, out var session))
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Цю дію вже оброблено або сесію завершено. Оберіть книгу заново.", showAlert: true, cancellationToken: cancellationToken); } catch { }
                    return;
                }

                string bookTitle = session.BookTitle ?? "Невідома книга";
                string contact = session.Contact ?? "Не вказано";
                string realName = session.RealName ?? "Без імені";
                string telegramName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username)) telegramName += $" (@{callbackQuery.Message.Chat.Username})";

                var request = new PendingRequest
                {
                    Type = RequestType.Borrow,
                    UserId = chatId,
                    UserName = telegramName,
                    RealName = realName,
                    Contact = contact,
                    BookTitle = bookTitle,
                    CatalogRowIndex = session.CatalogRowIndex,
                    BorrowDays = days
                };

                await GoogleSheetsService.AddPendingRequestAsync(request);

                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                });

                string adminMsg = $"📩 <b>НОВИЙ ЗАПИТ НА ВИДАЧУ</b>\n\n👤 Читач: <b>{TextUtils.EscapeHtml(realName)}</b>\n🔗 Профіль: {TextUtils.EscapeHtml(telegramName)}\n📞 Контакт: {TextUtils.EscapeHtml(contact)}\n📖 Книга: <b>{TextUtils.EscapeHtml(bookTitle)}</b>\n⏳ Термін: {days} днів";
                foreach (var adminId in SessionManager.AdminIds)
                {
                    try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Html, replyMarkup: adminKeyboard); }
                    catch (Exception ex) { Console.WriteLine($"[Telegram API] Не вдалося відправити запит адміну {adminId}: {ex.Message}"); }
                }

                SessionManager.ClearSession(chatId);
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⏳ Запит на книгу <b>{TextUtils.EscapeHtml(bookTitle)}</b> (на {days} днів) відправлено. Очікуйте!", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }

            try { await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken); }
            catch (Exception ex) { Console.WriteLine($"[Telegram API] Помилка AnswerCallbackQuery: {ex.Message}"); }
        }

        /// <summary>
        /// Знаходить рядок книги за стабільним Id (DbBook.Id), а не за позицією в каталозі.
        /// Так дія завжди потрапляє в потрібну книгу, навіть якщо каталог змінився між
        /// показом і натисканням (книгу видалили/обміняли).
        /// </summary>
        private static IList<object>? FindBookById(IList<IList<object>>? books, int bookId)
            => books?.FirstOrDefault(b =>
                b.Count > GoogleSheetsService.COL_CATALOG_ID
                && System.Convert.ToInt32(b[GoogleSheetsService.COL_CATALOG_ID]) == bookId);
    }
}