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

            // 1. Обробка гортання сторінок каталогу
            if (callbackQuery.Data.StartsWith("page_"))
            {
                int page = int.Parse(callbackQuery.Data.Split('_')[1]);
                await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, chatId, page, callbackQuery.Message.MessageId, cancellationToken);
            }
            // 2. Обробка закриття каталогу
            else if (callbackQuery.Data == "close_catalog")
            {
                await botClient.DeleteMessage(chatId, callbackQuery.Message.MessageId, cancellationToken);
                await botClient.SendMessage(chatId, "Каталог закрито 📕.", cancellationToken: cancellationToken);
            }
            // 3. Обробка кнопки переходу на конкретну сторінку
            else if (callbackQuery.Data == "jump_page")
            {
                SessionManager.UserStates[chatId] = UserState.WaitingForPageNumber;
                await botClient.SendMessage(chatId, "🔢 Введіть номер сторінки:", cancellationToken: cancellationToken);
            }
            // 4. Обробка натискання кнопки "Взяти книгу"
            // 4. Обробка натискання кнопки "Взяти книгу"
            else if (callbackQuery.Data.StartsWith("act_b_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    // Зчитуємо кількість з 5-ї колонки (індекс 4)
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;

                    if (disponible <= 0)
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken);
                        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                        return;
                    }

                    string title = books[rowIndex - 2][0]?.ToString() ?? "";
                    SessionManager.BorrowSessions[chatId] = new UserBorrowingSession { BookTitle = title };

                    // Відправляємо на крок введення імені
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowRealName;

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"📖 Ви обрали книгу:\n<b>{title}</b>.\n\n👤 Будь ласка, введіть ваше **Справжнє Ім'я та Прізвище**:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }            // 5. Обробка натискання кнопки "Повернути книгу"
            else if (callbackQuery.Data.StartsWith("act_r_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));
                string tgName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username))
                    tgName += $" (@{callbackQuery.Message.Chat.Username})";

                var userBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(tgName);
                if (!userBooks.Any(b => b.CatalogRowIndex == rowIndex))
                {
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Ви не можете повернути цю книгу, бо вона записана не на вас.", showAlert: true, cancellationToken: cancellationToken);
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
                    SessionManager.PendingRequests[request.RequestId] = request;

                    var adminKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Прийняти", $"req_apr_{request.RequestId}"),
                        InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                    });

                    string adminMsg = $"📥 **ЗАПИТ НА ПОВЕРНЕННЯ**\n\n👤 Читач: {tgName}\n📖 Книга: **{title}**";
                    foreach (var adminId in SessionManager.AdminIds)
                    {
                        try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Markdown, replyMarkup: adminKeyboard); } catch { }
                    }

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"⏳ Запит на повернення книги '<b>{title}</b>' відправлено. Дочекайтеся підтвердження.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // 6. Обробка відповідей адміністратора на запити (Схвалення / Відхилення)
            else if (callbackQuery.Data.StartsWith("req_apr_") || callbackQuery.Data.StartsWith("req_rej_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                bool isApprove = callbackQuery.Data.StartsWith("req_apr_");
                string reqId = callbackQuery.Data.Substring(8);

                if (!SessionManager.PendingRequests.TryRemove(reqId, out var request))
                {
                    try { await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Цей запит вже був оброблений іншим адміном або скасований.", showAlert: true, cancellationToken: cancellationToken); } catch { }
                    try { await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken); } catch { }
                    return;
                }

                string originalText = callbackQuery.Message!.Text ?? "Запит";

                if (isApprove)
                {
                    if (request.Type == RequestType.Borrow)
                    {
                        DateTime dueDate = DateTime.Now.AddDays(request.BorrowDays);
                        await GoogleSheetsService.AddBorrowingAsync(request.BookTitle, request.RealName, request.UserName, request.Contact ?? "", request.UserId, dueDate);

                        // ЗМЕНШУЄМО ДОСТУПНУ КІЛЬКІСТЬ НА 1
                        await GoogleSheetsService.ChangeAvailableCountAsync(request.CatalogRowIndex, -1);

                        await botClient.SendMessage(request.UserId, $"🎉 **Ваш запит схвалено!**\nКнигу **{request.BookTitle}** закріплено за вами.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
                    else // Повернення
                    {
                        // ЗБІЛЬШУЄМО ДОСТУПНУ КІЛЬКІСТЬ НА 1
                        await GoogleSheetsService.ChangeAvailableCountAsync(request.CatalogRowIndex, 1);
                        await GoogleSheetsService.LogReturnDateAsync(request.BookTitle, request.UserId);

                        await botClient.SendMessage(request.UserId, $"✅ **Повернення підтверджено!**\nДякуємо, що повернули книгу **{request.BookTitle}**.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }

                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n✅ **СХВАЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
                else
                {
                    string rejectMsg = request.Type == RequestType.Borrow
                        ? $"❌ Ваш запит на книгу\n{request.BookTitle}\nбуло відхилено адміністратором."
                        : $"❌ Ваш запит на повернення книги\n{request.BookTitle}\nвідхилено (можливо, ви не здали книгу фізично).";

                    await botClient.SendMessage(request.UserId, rejectMsg, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n❌ **ВІДХИЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            // 14. Обробка кнопки подовження терміну
            else if (callbackQuery.Data.StartsWith("act_ext_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(8));

                bool success = await GoogleSheetsService.ExtendBorrowingAsync(rowIndex);

                if (success)
                {
                    // Оновлюємо повідомлення: прибираємо кнопку і пишемо текст підтвердження
                    await botClient.EditMessageText(
                        chatId,
                        callbackQuery.Message.MessageId,
                        "✅ **Термін здачі успішно подовжено на 1 місяць (30 днів)!**\nНаступного разу ви не зможете скористатися цією функцією. Приємного читання!",
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Помилка подовження. Зверніться до адміністратора.", showAlert: true, cancellationToken: cancellationToken);
                }
            }
            // 7. Натиснуто кнопку "Видалити" під книгою
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
                        new InlineKeyboardButton[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Так, видалити", $"conf_del_{rowIndex}"),
                            InlineKeyboardButton.WithCallbackData("❌ Скасувати", $"canc_del_{rowIndex}")
                        }
                    });
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⚠️ Ви дійсно хочете безповоротно видалити книгу **{title}** з каталогу?", parseMode: ParseMode.Markdown, replyMarkup: confirmKeyboard, cancellationToken: cancellationToken);
                }
            }
            // 8. Підтверджено видалення
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
            // 9. Скасовано видалення
            else if (callbackQuery.Data.StartsWith("canc_del_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, "❌ Видалення скасовано.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }
            // 10. Натиснуто кнопку "Редагувати"
            // 10. Натиснуто кнопку "Редагувати"
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

                    // Зчитуємо поточні кількості з таблиці
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
                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"✏️ Редагуємо книгу: **{oldTitle}**\n\nВведіть НОВУ НАЗВУ (або відправте `-` щоб залишити без змін):", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }            // 11. Обмін книги
            else if (callbackQuery.Data.StartsWith("act_exch_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(9));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    SessionManager.AdminExchangeSessions[chatId] = new AdminExchangeSession
                    {
                        OldBookRowIndex = rowIndex,
                        OldBookTitle = title
                    };
                    SessionManager.UserStates[chatId] = UserState.WaitingForExchangeReaderName;

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"Selected: **{title}**.\n👤 Введіть Ім'я в Telegram (або контакт) читача, який робить обмін:", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            // 12. Видати вручну
            // 12. Видати вручну
            else if (callbackQuery.Data.StartsWith("act_man_b_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    // Зчитуємо кількість з 5-ї колонки (індекс 4)
                    int disponible = books[rowIndex - 2].Count > 4 ? int.TryParse(books[rowIndex - 2][4]?.ToString(), out int d) ? d : 0 : 0;

                    if (disponible <= 0)
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Всі примірники цієї книги вже взяті.", showAlert: true, cancellationToken: cancellationToken);
                        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                        return;
                    }

                    SessionManager.AdminSessions[chatId] = new ManualBorrowingSession { BookId = title };
                    SessionManager.UserStates[chatId] = UserState.WaitingForManualReaderName;

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"📖 Обрано: **{title}**.\n👤 Введіть ПІБ читача (офлайн користувача), якому видається книга:", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }            // 13. Повернути вручну
            else if (callbackQuery.Data.StartsWith("act_man_r_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    await GoogleSheetsService.UpdateBookStatusAsync(title, "Доступна");
                    await GoogleSheetsService.LogReturnDateAsync(title);

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"✅ Книгу '<b>{title}</b>' успішно повернуто вручну від офлайн-користувача! Статус оновлено на 'Доступна'.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // 4.5. Вибір періоду та відправка запиту адміну
            // 4.5. Вибір періоду та відправка запиту адміну
            else if (callbackQuery.Data.StartsWith("period_"))
            {
                int days = int.Parse(callbackQuery.Data.Split('_')[1]);
                var session = SessionManager.BorrowSessions[chatId];

                string bookTitle = session.BookTitle ?? "Невідома книга";
                string contact = session.Contact ?? "Не вказано";
                string realName = session.RealName ?? "Без імені"; // Беремо введене реальне ім'я

                // Залишаємо телеграм-нікнейм для підстраховки адміну
                // ... вирахування днів та отримання сесії залишається без змін ...
                string telegramName = callbackQuery.Message!.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(callbackQuery.Message.Chat.Username)) telegramName += $" (@{callbackQuery.Message.Chat.Username})";

                var request = new PendingRequest
                {
                    Type = RequestType.Borrow,
                    UserId = chatId,
                    UserName = telegramName, // Чистий Telegram нікнейм
                    RealName = realName,     // Справжнє ім'я окремо
                    Contact = contact,
                    BookTitle = bookTitle,
                    BorrowDays = days
                };
                SessionManager.PendingRequests[request.RequestId] = request;

                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                });

                // Красиве повідомлення для адміна
                string adminMsg = $"📩 **НОВИЙ ЗАПИТ НА ВИДАЧУ**\n\n👤 Читач: **{realName}**\n🔗 Профіль: {telegramName}\n📞 Контакт: {contact}\n📖 Книга: **{bookTitle}**\n⏳ Термін: {days} днів";
                foreach (var adminId in SessionManager.AdminIds)
                {
                    try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Markdown, replyMarkup: adminKeyboard); } catch { }
                }

                SessionManager.ClearSession(chatId);
                await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"⏳ Запит на книгу **{bookTitle}** (на {days} днів) відправлено. Очікуйте!", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
            }            
            
            
            // Обов'язкова відповідь серверу Telegram
            try { await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken); } catch { }
        }
    }
}