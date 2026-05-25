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
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;

                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    SessionManager.BorrowSessions[chatId] = new UserBorrowingSession { BookTitle = title, CatalogRowIndex = rowIndex };
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowRealName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"📖 Ви обрали книгу:\n<b>{title}</b>.\n\n👤 Будь ласка, введіть ваше **Справжнє Ім'я та Прізвище**:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_r_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));
                string tgName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username))
                    tgName += $" (@{callbackQuery.Message.Chat.Username})";

                var userBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(tgName);
                if (!userBooks.Any(b => b.CatalogRowIndex == rowIndex))
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Ви не можете повернути цю книгу, бо вона записана не на вас.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    return;
                }

                var books = await GoogleSheetsService.GetBooksAsync();
                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    var request = new PendingRequest
                    {
                        Type = RequestType.Return,
                        UserId = chatId,
                        UserName = tgName,
                        BookTitle = title,
                        CatalogRowIndex = rowIndex
                    };

                    // ДОДАЄМО ЗАПИТ У GOOGLE ТАБЛИЦЮ!
                    await GoogleSheetsService.AddPendingRequestAsync(request);

                    var adminKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Прийняти", $"req_apr_{request.RequestId}"),
                        InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                    });

                    string adminMsg = $"📥 **ЗАПИТ НА ПОВЕРНЕННЯ**\n\n👤 Читач: {tgName}\n📖 Книга: **{title}**";
                    foreach (var adminId in SessionManager.AdminIds)
                    {
                        try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Markdown, replyMarkup: adminKeyboard); }
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

                // Отримуємо запит із бази даних таблиць!
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

                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n🔄 <b>Визначте статус обміну для цієї нової книги:</b>", parseMode: ParseMode.Html, replyMarkup: choiceKeyboard, cancellationToken: cancellationToken);
                        return; // Запит видалимо на наступному кроці (userex_yes/no)
                    }

                    // Для звичайних операцій - видаляємо запит і виконуємо дію
                    await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                    if (request.Type == RequestType.Borrow)
                    {
                        DateTime dueDate = DateTime.Now.AddDays(request.BorrowDays);
                        await GoogleSheetsService.AddBorrowingAsync(request.BookTitle, request.RealName, request.UserName, request.Contact ?? "", request.UserId, dueDate);
                        await GoogleSheetsService.ChangeAvailableCountAsync(request.CatalogRowIndex, -1);
                        await botClient.SendMessage(request.UserId, $"🎉 **Ваш запит схвалено!**\nКнигу **{request.BookTitle}** закріплено за вами.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
                    else if (request.Type == RequestType.Return)
                    {
                        await GoogleSheetsService.ChangeAvailableCountAsync(request.CatalogRowIndex, 1);
                        await GoogleSheetsService.LogReturnDateAsync(request.BookTitle, request.UserId);
                        await botClient.SendMessage(request.UserId, $"✅ **Повернення підтверджено!**\nДякуємо, що повернули книгу **{request.BookTitle}**.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }

                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n✅ **СХВАЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
                else // Відхилено
                {
                    await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                    string rejectMsg = request.Type == RequestType.UserExchange
                        ? $"❌ Ваш запит на обмін книги\n<b>{request.BookTitle}</b>\nбуло відхилено адміністратором."
                        : (request.Type == RequestType.Borrow
                            ? $"❌ Ваш запит на книгу\n{request.BookTitle}\nбуло відхилено адміністратором."
                            : $"❌ Ваш запит на повернення книги\n{request.BookTitle}\nвідхилено.");

                    await botClient.SendMessage(request.UserId, rejectMsg, parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n❌ **ВІДХИЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("userex_yes_") || callbackQuery.Data.StartsWith("userex_no_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                bool canExchange = callbackQuery.Data.StartsWith("userex_yes_");
                string reqId = callbackQuery.Data.Substring(11);

                var request = await GoogleSheetsService.GetPendingRequestAsync(reqId);
                if (request == null)
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Запит уже оброблено.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    return;
                }

                await GoogleSheetsService.DeletePendingRequestAsync(reqId);

                string exchangeStatus = canExchange ? "Так" : "Ні";

                await GoogleSheetsService.AddBookToCatalogAsync(request.NewBookTitle, request.NewBookAuthor, request.NewBookGenre, "Доступна", exchangeStatus, 1);
                await GoogleSheetsService.ProcessExchangeOutgoingBookAsync(request.CatalogRowIndex, request.BookTitle);
                await GoogleSheetsService.AddExchangeLogAsync(request.BookTitle, request.NewBookTitle, request.UserName);

                await botClient.SendMessage(request.UserId, $"🎉 <b>Ваш запит на обмін схвалено!</b>\n\nВи можете забрати книгу <b>{request.BookTitle}</b>, а свою передати баристі. Дякуємо за обмін! 💚", parseMode: ParseMode.Html, cancellationToken: cancellationToken);

                string originalText = callbackQuery.Message!.Text ?? "";
                if (originalText.Contains("\n\n🔄 Визначте статус обміну"))
                {
                    originalText = originalText.Split(new[] { "\n\n🔄 Визначте статус обміну" }, StringSplitOptions.None)[0];
                }

                string finalStatusText = canExchange ? "СХВАЛЕНО (З можливістю обміну)" : "СХВАЛЕНО (Без подальшого обміну)";
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + $"\n\n✅ <b>{finalStatusText}</b>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data.StartsWith("userex_sel_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(11));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;
                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Цю книгу вже встигли забрати.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string libBookTitle = books[rowIndex - 2][0]?.ToString() ?? "";
                    var session = SessionManager.UserExchangeSessions[chatId];
                    session.LibraryBookTitle = libBookTitle;
                    session.LibraryBookRowIndex = rowIndex;

                    string telegramName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                    if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username)) telegramName += $" (@{callbackQuery.Message.Chat.Username})";

                    var request = new PendingRequest
                    {
                        Type = RequestType.UserExchange,
                        UserId = chatId,
                        UserName = telegramName,
                        BookTitle = libBookTitle,
                        CatalogRowIndex = rowIndex,
                        NewBookTitle = session.Title,
                        NewBookAuthor = session.Author,
                        NewBookGenre = session.Genre
                    };

                    // ДОДАЄМО ЗАПИТ У GOOGLE ТАБЛИЦЮ!
                    await GoogleSheetsService.AddPendingRequestAsync(request);

                    var adminKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                        InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                    });

                    string adminMsg = $"📩 <b>ЗАПИТ НА ОБМІН (БУККРОСИНГ)</b>\n\n" +
                                      $"👤 Читач: {telegramName}\n" +
                                      $"📥 <b>Принесе в бібліотеку:</b>\n📖 «{session.Title}» ({session.Author} / Жанр: {session.Genre})\n\n" +
                                      $"📤 <b>Хоче забрати натомість:</b>\n📖 «{libBookTitle}»";

                    foreach (var adminId in SessionManager.AdminIds)
                    {
                        try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Html, replyMarkup: adminKeyboard); }
                        catch (Exception ex) { Console.WriteLine($"[Telegram API] Не вдалося відправити запит адміну {adminId}: {ex.Message}"); }
                    }

                    SessionManager.ClearSession(chatId);
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"⏳ Запит на обмін успішно надіслано адміністраторам!\n\nВи віддаєте: <b>{session.Title}</b>\nЗабираєте: <b>{libBookTitle}</b>\n\nДочекайтеся підтвердження баристи.", parseMode: ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_ext_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(8));
                bool success = await GoogleSheetsService.ExtendBorrowingAsync(rowIndex);

                if (success)
                {
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, "✅ **Термін здачі успішно подовжено на 1 місяць (30 днів)!**\nНаступного разу ви не зможете скористатися цією функцією. Приємного читання!", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
                else
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Помилка подовження. Зверніться до адміністратора.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                }
            }
            else if (callbackQuery.Data.StartsWith("act_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(8));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "Невідома книга";
                    var confirmKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new InlineKeyboardButton[] { InlineKeyboardButton.WithCallbackData("✅ Так, видалити", $"conf_del_{rowIndex}"), InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"canc_del_{rowIndex}") }
                    });
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⚠️ Ви дійсно хочете безповоротно видалити книгу **{title}** з каталогу?", parseMode: ParseMode.Markdown, replyMarkup: confirmKeyboard, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("conf_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";
                    bool deleted = await GoogleSheetsService.DeleteBookFromCatalogAsync(title);
                    if (deleted)
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"✅ Книгу **{title}** успішно видалено з каталогу.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    else
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"❌ Сталася помилка при видаленні книги **{title}**.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("canc_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, "❌ Видалення скасовано.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }
            else if (callbackQuery.Data.StartsWith("act_edit_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string oldTitle = books[rowIndex - 2].Count > 0 ? books[rowIndex - 2][0]?.ToString() ?? "" : "";
                    string oldAuthor = books[rowIndex - 2].Count > 1 ? books[rowIndex - 2][1]?.ToString() ?? "" : "";
                    string oldGenre = books[rowIndex - 2].Count > 2 ? books[rowIndex - 2][2]?.ToString() ?? "" : "";
                    string oldExchange = books[rowIndex - 2].Count > 3 ? books[rowIndex - 2][3]?.ToString()?.Trim() ?? "Так" : "Так";

                    int oldAvailable = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int ava) ? ava : 0 : 0;
                    int oldTotal = books[rowIndex - 2].Count > 5 ? int.TryParse(books[rowIndex - 2][5]?.ToString(), out int tot) ? tot : 1 : 1;

                    SessionManager.AdminBookSessions[chatId] = new AdminBookSession
                    {
                        EditRowIndex = rowIndex,
                        Title = oldTitle,
                        Author = oldAuthor,
                        Genre = oldGenre,
                        ExchangeStatus = oldExchange,
                        CurrentAvailable = oldAvailable,
                        CurrentTotal = oldTotal
                    };

                    SessionManager.UserStates[chatId] = UserState.WaitingForEditBookTitle;
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"✏️ Редагуємо книгу: **{oldTitle}**\n\nВведіть НОВУ НАЗВУ (або відправте `-` щоб залишити без змін):", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_exch_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;
                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги зараз на руках. Обмін неможливий.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    SessionManager.AdminExchangeSessions[chatId] = new AdminExchangeSession { OldBookRowIndex = rowIndex, OldBookTitle = title };
                    SessionManager.UserStates[chatId] = UserState.WaitingForExchangeReaderName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"Selected: **{title}**.\n👤 Введіть Ім'я в Telegram (або контакт) читача, який робить обмін:", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_man_b_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;

                    if (disponible <= 0)
                    {
                        try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                        return;
                    }

                    SessionManager.AdminSessions[chatId] = new ManualBorrowingSession { BookId = title, CatalogRowIndex = rowIndex };
                    SessionManager.UserStates[chatId] = UserState.WaitingForManualReaderName;

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"📖 Обрано: **{title}**.\n👤 Введіть ПІБ читача (офлайн користувача), якому видається книга:", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("act_man_r_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    await GoogleSheetsService.ChangeAvailableCountAsync(rowIndex, 1);
                    await GoogleSheetsService.LogReturnDateAsync(title);

                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch (Exception ex) { Console.WriteLine($"[Telegram API] {ex.Message}"); }
                    await botClient.SendMessage(chatId, $"✅ Книгу '<b>{title}</b>' успішно повернуто вручну від офлайн-користувача! Статус оновлено.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            else if (callbackQuery.Data.StartsWith("period_"))
            {
                int days = int.Parse(callbackQuery.Data.Split('_')[1]);
                var session = SessionManager.BorrowSessions[chatId];

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

                // ДОДАЄМО ЗАПИТ У GOOGLE ТАБЛИЦЮ!
                await GoogleSheetsService.AddPendingRequestAsync(request);

                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                });

                string adminMsg = $"📩 **НОВИЙ ЗАПИТ НА ВИДАЧУ**\n\n👤 Читач: **{realName}**\n🔗 Профіль: {telegramName}\n📞 Контакт: {contact}\n📖 Книга: **{bookTitle}**\n⏳ Термін: {days} днів";
                foreach (var adminId in SessionManager.AdminIds)
                {
                    try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Markdown, replyMarkup: adminKeyboard); }
                    catch (Exception ex) { Console.WriteLine($"[Telegram API] Не вдалося відправити запит адміну {adminId}: {ex.Message}"); }
                }

                SessionManager.ClearSession(chatId);
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⏳ Запит на книгу **{bookTitle}** (на {days} днів) відправлено. Очікуйте!", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }

            try { await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken); }
            catch (Exception ex) { Console.WriteLine($"[Telegram API] Помилка AnswerCallbackQuery: {ex.Message}"); }
        }
    }
}