using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using static System.Net.Mime.MediaTypeNames;

namespace LibraryBot.Handlers
{
    public static class UpdateHandler
    {
        public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await HandleCallbackQueryAsync(botClient, update.CallbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { } message || message.Text is not { } messageText)
                return;

            await HandleMessageAsync(botClient, message, cancellationToken);
        }

        private static async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            string messageText = message.Text!;
            // --- БРОНЕБІЙНІ КОМАНДИ (працюють завжди, навіть якщо бот чогось чекає) ---
            if (messageText == "/myid")
            {
                await botClient.SendMessage(chatId, $"Ваш Telegram ID: {chatId}", cancellationToken: cancellationToken);
                return; // Зупиняємо подальшу обробку
            }

            if (messageText == "/start")
            {
                SessionManager.ClearSession(chatId); // Примусово скидаємо всі завислі стани

                string startText = "👋 <b>Привіт! Це бібліотека «Паперового Життя»</b> 📚\n\n" +
                                   "Тут ви можете:\n" +
                                   "📥 <b>Взяти книгу</b> на прочитання\n" +
                                   "📤 <b>Повернути ту</b>, що вже прочитали\n" +
                                   "🤝 <b>Обміняти буккросингом</b> свої книги з нашими 💚\n\n" +
                                   "<i>Оберіть потрібну дію в меню нижче 👇</i>";

                await botClient.SendMessage(
                    chatId: chatId,
                    text: startText,
                    parseMode: ParseMode.Html, // Додали підтримку HTML-тегів
                    replyMarkup: KeyboardHelper.GetMenu(chatId),
                    cancellationToken: cancellationToken
                );
                return;
            }
            // --------------------------------------------------------------------------
            var currentState = SessionManager.UserStates.GetValueOrDefault(chatId, UserState.None);

            // 1. Обробка станів адміністратора
            if (currentState != UserState.None && SessionManager.AdminIds.Contains(chatId))
            {
                if (await ProcessAdminStateAsync(botClient, chatId, messageText, currentState, cancellationToken))
                    return;
            }

            // 2. Обробка станів користувача
            if (currentState != UserState.None)
            {
                if (await ProcessUserStateAsync(botClient, message, currentState, cancellationToken))
                    return;
            }

            // 3. Обробка звичайних команд та кнопок меню
            switch (messageText)
            {

                case "/books":
                case "📚 Каталог":
                    await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, chatId, 0, null, cancellationToken);
                    break;

                case "/borrow":
                case "📥 Взяти книгу":
                    SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
                    await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора, щоб знайти книгу:", cancellationToken: cancellationToken);
                    break;

                case "/return":
                case "📤 Повернути книгу":
                    string tgName = message.Chat.FirstName ?? "Без імені";
                    if (!string.IsNullOrEmpty(message.Chat.Username))
                        tgName += $" (@{message.Chat.Username})";
                    await LibraryDisplayService.ShowUserBorrowedBooksAsync(botClient, chatId, tgName, cancellationToken);
                    break;

                case "🔍 Пошук":
                    SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
                    await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора (можна частково) для пошуку:", cancellationToken: cancellationToken);
                    break;

                case "/cancel":
                case "❌ Скасувати дію":
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "Дію скасовано. Повернення до головного меню.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    break;

                case "ℹ️ Допомога":
                    string helpText = "ℹ️ <b>Довідка: як користуватися ботом</b>\n\n" +
                                      "Тут ви можете легко знаходити потрібну літературу.\n\n" +
                                      "📚 <b>Каталог</b> — перегляд повного списку книг бібліотеки.\n" +
                                      "🔍 <b>Пошук</b> — швидкий пошук за назвою книги або автором.\n\n" +
                                      "ℹ️ <i>Щоб взяти або повернути книгу потрібно підтвердження адміністратора.</i>\n" +
                                      "📥 <b>Взяти книгу</b> — оформлення запиту на видачу.\n" +
                                      "📤 <b>Повернути книгу</b> — перегляд ваших книг та надсилання запиту на повернення .\n\n" +
                                      "❌ <b>Скасувати дію</b> — перериває будь-який поточний крок (наприклад, якщо помилилися при введенні).\n\n" +
                                      "💡 <i>Щодо буккросингу: якщо ви принесли свою книгу для обміну, зверніться до баристи для його оформлення!</i>";

                    await botClient.SendMessage(
                                            chatId: chatId,
                                            text: helpText,
                                            parseMode: ParseMode.Html,
                                            cancellationToken: cancellationToken
                                        );
                    break;
                case "➕ Додати":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForAddBookTitle;
                        SessionManager.AdminBookSessions[chatId] = new AdminBookSession();
                        await botClient.SendMessage(chatId, "📝 Введіть НАЗВУ нової книги:", cancellationToken: cancellationToken);
                    }
                    break;

                case "🗑 Видалити":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForDeleteSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАЛИТИ:", cancellationToken: cancellationToken);
                    }
                    break;
                case "✏️ Редагувати":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForEditSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВІДРЕДАГУВАТИ:", cancellationToken: cancellationToken);
                    }
                    break;
                case "🤝 Обмін":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForExchangeSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги з бібліотеки, яку хочете ОБМІНЯТИ:", cancellationToken: cancellationToken);
                    }
                    break;
                case "🤝 Видати вручну":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForManualSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАТИ ВРУЧНУ:", cancellationToken: cancellationToken);
                    }
                    break;
                case "📤 Повернути вручну":
                    if (SessionManager.AdminIds.Contains(chatId))
                    {
                        SessionManager.UserStates[chatId] = UserState.WaitingForManualReturnSearchQuery;
                        await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку серед книг, які зараз знаходяться НА РУКАХ:", cancellationToken: cancellationToken);
                    }
                    break;
            }
        }

        private static async Task<bool> ProcessUserStateAsync(ITelegramBotClient botClient, Message message, UserState currentState, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            string messageText = message.Text!.Trim();

            if (currentState == UserState.WaitingForSearchQuery)
            {
                if (messageText == "❌ Скасувати дію" || messageText.ToLower() == "/cancel")
                {
                    SessionManager.ClearSession(chatId);
                    await botClient.SendMessage(chatId, "❌ Пошук скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                    return true;
                }

                // --- ПОЧАТОК НОВИХ ЗМІН ---
                // Спочатку формуємо ім'я користувача, щоб передати його в пошук
                string telegramName = message.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(message.Chat.Username))
                {
                    telegramName += $" (@{message.Chat.Username})";
                }

                // Тепер викликаємо пошук, передаючи туди telegramName (четвертий параметр)
                await LibraryDisplayService.SearchBooksAsync(botClient, chatId, messageText, telegramName, cancellationToken);
                // --- КІНЕЦЬ НОВИХ ЗМІН ---

                return true;
            }

            if (currentState == UserState.WaitingForBorrowContact)
            {
                string bookTitle = SessionManager.BorrowSessions[chatId].BookTitle ?? "Невідома книга";
                string telegramName = message.Chat.FirstName ?? "Без імені";
                if (!string.IsNullOrEmpty(message.Chat.Username)) telegramName += $" (@{message.Chat.Username})";

                // 1. Створюємо запит
                var request = new PendingRequest
                {
                    Type = RequestType.Borrow,
                    UserId = chatId,
                    UserName = telegramName,
                    Contact = messageText,
                    BookTitle = bookTitle
                };
                SessionManager.PendingRequests[request.RequestId] = request;

                // 2. Надсилаємо запит усім адмінам
                var adminKeyboard = new InlineKeyboardMarkup(new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Схвалити", $"req_apr_{request.RequestId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Відхилити", $"req_rej_{request.RequestId}")
                });

                string adminMsg = $"📩 **НОВИЙ ЗАПИТ НА ВИДАЧУ**\n\n👤 Читач: {telegramName}\n📞 Контакт: {messageText}\n📖 Книга: **{bookTitle}**";

                foreach (var adminId in SessionManager.AdminIds)
                {
                    try { await botClient.SendMessage(adminId, adminMsg, parseMode: ParseMode.Markdown, replyMarkup: adminKeyboard); } catch { }
                }

                SessionManager.ClearSession(chatId);
                await botClient.SendMessage(chatId, $"⏳ Запит на книгу\n{bookTitle}\nвідправлено. Очікуйте на підтвердження!", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                return true;
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

        private static async Task<bool> ProcessAdminStateAsync(ITelegramBotClient botClient, long chatId, string text, UserState state, CancellationToken cancellationToken)
        {
            // Скасування будь-якої дії
            if (text.ToLower() == "/cancel" || text == "❌ Скасувати дію")
            {
                SessionManager.ClearSession(chatId);
                await botClient.SendMessage(chatId, "Дію скасовано.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // ==========================================
            // 1. ДОДАВАННЯ КНИГИ
            // ==========================================
            if (state == UserState.WaitingForAddBookTitle)
            {
                SessionManager.AdminBookSessions[chatId].Title = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;
                await botClient.SendMessage(chatId, "👤 Введіть АВТОРА книги:", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookAuthor)
            {
                SessionManager.AdminBookSessions[chatId].Author = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookGenre;
                await botClient.SendMessage(chatId, "🎭 Введіть ЖАНР книги:", cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookGenre)
            {
                SessionManager.AdminBookSessions[chatId].Genre = text;
                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookExchangeStatus;

                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, "🔄 Чи доступна ця книга для обміну?", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForAddBookExchangeStatus)
            {
                string exchangeStatus = (text.ToLower() == "ні") ? "Ні" : "Так";
                var session = SessionManager.AdminBookSessions[chatId];

                // Статус наявності завжди "Доступна" при створенні
                await GoogleSheetsService.AddBookToCatalogAsync(session.Title!, session.Author!, session.Genre!, "Доступна", exchangeStatus);
                SessionManager.ClearSession(chatId);

                string exchText = exchangeStatus == "Ні" ? "Не обмінюється" : "Можна обмінювати";
                await botClient.SendMessage(chatId, $"✅ Книгу **{session.Title}** успішно додано!\nСтатус: Доступна ({exchText})", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }
            // ==========================================
            // 2. ВИДАЛЕННЯ КНИГИ
            // ==========================================
            if (state == UserState.WaitingForDeleteSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForDeletionAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            // ==========================================
            // 3. РЕДАГУВАННЯ КНИГИ
            // ==========================================
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
                SessionManager.UserStates[chatId] = UserState.WaitingForEditBookExchangeStatus;

                string oldExch = SessionManager.AdminBookSessions[chatId].ExchangeStatus ?? "Так";
                string currentChoice = oldExch == "Ні" ? "Ні" : "Так";

                var kb = new ReplyKeyboardMarkup(new[] { new KeyboardButton[] { "Так", "Ні", "-" }, new KeyboardButton[] { "❌ Скасувати дію" } }) { ResizeKeyboard = true };
                await botClient.SendMessage(chatId, $"🔄 Чи доступна ця книга для обміну? (Зараз: {currentChoice})\nОберіть `Так`, `Ні` або `-` щоб залишити без змін:", replyMarkup: kb, cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForEditBookExchangeStatus)
            {
                string finalExch = SessionManager.AdminBookSessions[chatId].ExchangeStatus ?? "Так";
                if (text.ToLower() == "так") finalExch = "Так";
                else if (text.ToLower() == "ні") finalExch = "Ні";

                var session = SessionManager.AdminBookSessions[chatId];
                // Передаємо збережений статус доступності та новий статус обміну
                bool updated = await GoogleSheetsService.UpdateBookInCatalogAsync(session.EditRowIndex, session.Title!, session.Author!, session.Genre!, session.Status!, finalExch);
                SessionManager.ClearSession(chatId);

                if (updated)
                    await botClient.SendMessage(chatId, $"✅ Книгу **{session.Title}** успішно оновлено!", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                else
                    await botClient.SendMessage(chatId, $"❌ Помилка оновлення.", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // ==========================================
            // 4. ОБМІН КНИГИ
            // ==========================================
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
                await GoogleSheetsService.AddExchangeLogAsync(session.OldBookTitle, session.NewBookTitle, session.ReaderName);
                await GoogleSheetsService.DeleteBookFromCatalogAsync(session.OldBookTitle);
                // Додали "Так" в кінці
                await GoogleSheetsService.AddBookToCatalogAsync(session.NewBookTitle, session.NewBookAuthor, session.NewBookGenre, "Доступна", "Так");
                SessionManager.ClearSession(chatId);
                string successText = $"✅ **Обмін успішно завершено!**\n\n📖 Стара книга '**{session.OldBookTitle}**' зникла з каталогу.\n✨ Нова книга '**{session.NewBookTitle}**' додана та доступна для читачів.\n🗂 Запис внесено в аркуш 'Обмін'.";
                await botClient.SendMessage(chatId, successText, parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }

            // ==========================================
            // 5. РУЧНА ВИДАЧА ТА ПОВЕРНЕННЯ
            // ==========================================
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

                await GoogleSheetsService.AddBorrowingAsync(session.BookId!, session.ReaderName!, session.ReaderContact!);
                await GoogleSheetsService.UpdateBookStatusAsync(session.BookId!, "Читають");

                SessionManager.ClearSession(chatId);
                await botClient.SendMessage(chatId, $"✅ Готово! Книга '**{session.BookId}**' видана читачу **{session.ReaderName}** ({session.ReaderContact}).", parseMode: ParseMode.Markdown, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return true;
            }
            if (state == UserState.WaitingForManualReturnSearchQuery)
            {
                await LibraryDisplayService.SearchBooksForManualReturnAsync(botClient, chatId, text, cancellationToken);
                return true;
            }

            return false;
        }
        private static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
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
            else if (callbackQuery.Data.StartsWith("act_b_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    // ЖОРСТКА ПЕРЕВІРКА: чи дійсно книга досі доступна?
                    string status = books[rowIndex - 2].Count > 3 ? books[rowIndex - 2][3]?.ToString()?.Trim() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(status) && !status.Equals("Доступна", StringComparison.OrdinalIgnoreCase))
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Ця книга вже кимось взята.", showAlert: true, cancellationToken: cancellationToken);
                        // Прибираємо неактуальну кнопку
                        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                        return; // Важливо! Зупиняємо виконання, щоб людина не перейшла до введення контакту
                    }

                    string title = books[rowIndex - 2][0]?.ToString() ?? "";
                    SessionManager.BorrowSessions[chatId] = new UserBorrowingSession { BookTitle = title };
                    SessionManager.UserStates[chatId] = UserState.WaitingForBorrowContact;

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"📖 Ви обрали книгу:\n<b>{title}</b>.\n📞 Введіть контаки для зв'язку:", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // 5. Обробка натискання кнопки "Повернути книгу"
            else if (callbackQuery.Data.StartsWith("act_r_"))
            {
                int rowIndex = int.Parse(callbackQuery.Data.Substring(6));

                // ЖОРСТКА ПЕРЕВІРКА: чи дійсно цей користувач брав цю книгу?
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

                    // 1. Створюємо запит на повернення
                    var request = new PendingRequest
                    {
                        Type = RequestType.Return,
                        UserId = chatId,
                        UserName = tgName,
                        BookTitle = title,
                        CatalogRowIndex = rowIndex
                    };
                    SessionManager.PendingRequests[request.RequestId] = request;

                    // 2. Сповіщаємо адмінів
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

                    // 3. Змінюємо кнопку користувача, щоб він не міг натиснути двічі
                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"⏳ Запит на повернення книги '<b>{title}</b>' відправлено. Дочекайтеся підтвердження.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // 6. Обробка відповідей адміністратора на запити (Схвалення / Відхилення)
            else if (callbackQuery.Data.StartsWith("req_apr_") || callbackQuery.Data.StartsWith("req_rej_"))
            {
                // Перевіряємо, чи це дійсно адмін натиснув кнопку
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                bool isApprove = callbackQuery.Data.StartsWith("req_apr_");
                string reqId = callbackQuery.Data.Substring(8); // Дістаємо ID запиту

                // Шукаємо і видаляємо запит із черги
                // Шукаємо і видаляємо запит із черги
                if (!SessionManager.PendingRequests.TryRemove(reqId, out var request))
                {
                    try
                    {
                        // Якщо запит старий, Telegram кине помилку, ми її просто проігноруємо
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "⚠️ Цей запит вже був оброблений іншим адміном або скасований.", showAlert: true, cancellationToken: cancellationToken);
                    }
                    catch { }

                    try
                    {
                        // Також безпечно прибираємо клавіатуру
                        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    }
                    catch { }

                    return;
                }

                string originalText = callbackQuery.Message!.Text ?? "Запит";

                if (isApprove)
                {
                    if (request.Type == RequestType.Borrow)
                    {
                        // Записуємо в таблицю тільки зараз!
                        await GoogleSheetsService.AddBorrowingAsync(request.BookTitle, request.UserName, request.Contact ?? "");
                        await GoogleSheetsService.UpdateBookStatusAsync(request.BookTitle, "Читають");

                        // Пишемо читачу
                        await botClient.SendMessage(request.UserId, $"🎉 **Ваш запит схвалено!**\nКнигу **{request.BookTitle}** закріплено за вами.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
                    else // Повернення
                    {
                        await GoogleSheetsService.UpdateBookStatusAsync(request.BookTitle, "Доступна");
                        await GoogleSheetsService.LogReturnDateAsync(request.BookTitle);

                        await botClient.SendMessage(request.UserId, $"✅ **Повернення підтверджено!**\nДякуємо, що повернули книгу **{request.BookTitle}**.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
                    // Оновлюємо адмінське повідомлення (прибираємо кнопки і пишемо статус)
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n✅ **СХВАЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
                else // Відхилено
                {
                    string rejectMsg = request.Type == RequestType.Borrow
                        ? $"❌ Ваш запит на книгу\n{request.BookTitle}\nбуло відхилено адміністратором."
                        : $"❌ Ваш запит на повернення книги\n{request.BookTitle}\nвідхилено (можливо, ви не здали книгу фізично).";

                    await botClient.SendMessage(request.UserId, rejectMsg, parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, originalText + "\n\n❌ **ВІДХИЛЕНО**", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            // 7. Натиснуто кнопку "Видалити" під книгою (Викликаємо підтвердження)
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

                    // Змінюємо повідомлення на запит підтвердження
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
                    {
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"✅ Книгу **{title}** успішно видалено з каталогу.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await botClient.EditMessageText(chatId, callbackQuery.Message.MessageId, $"❌ Сталася помилка при видаленні книги **{title}**.", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                    }
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
                    string oldStatus = books[rowIndex - 2].Count > 3 ? books[rowIndex - 2][3]?.ToString()?.Trim() ?? "Доступна" : "Доступна";
                    string oldExchange = books[rowIndex - 2].Count > 4 ? books[rowIndex - 2][4]?.ToString()?.Trim() ?? "Так" : "Так"; // Читаємо колонку E

                    SessionManager.AdminBookSessions[chatId] = new AdminBookSession
                    {
                        EditRowIndex = rowIndex,
                        Title = oldTitle,
                        Author = oldAuthor,
                        Genre = oldGenre,
                        Status = oldStatus,
                        ExchangeStatus = oldExchange
                    };

                    SessionManager.UserStates[chatId] = UserState.WaitingForEditBookTitle;
                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"✏️ Редагуємо книгу: **{oldTitle}**\n\nВведіть НОВУ НАЗВУ (або відправте `-` щоб залишити без змін):", parseMode: ParseMode.Markdown, cancellationToken: cancellationToken);
                }
            }
            // 11. Обмін книги
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

            else if (callbackQuery.Data.StartsWith("act_man_b_"))
            {
                if (!SessionManager.AdminIds.Contains(chatId)) return;

                int rowIndex = int.Parse(callbackQuery.Data.Substring(10));
                var books = await GoogleSheetsService.GetBooksAsync();

                if (books != null && books.Count >= rowIndex - 1)
                {
                    string title = books[rowIndex - 2][0]?.ToString() ?? "";

                    // Перевірка чи книгу не забрали, поки ми думали
                    string status = books[rowIndex - 2].Count > 3 ? books[rowIndex - 2][3]?.ToString()?.Trim() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(status) && !status.Equals("Доступна", StringComparison.OrdinalIgnoreCase))
                    {
                        await botClient.AnswerCallbackQuery(callbackQuery.Id, "❌ Ця книга вже кимось взята.", showAlert: true, cancellationToken: cancellationToken);
                        await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                        return;
                    }

                    SessionManager.AdminSessions[chatId] = new ManualBorrowingSession { BookId = title };
                    SessionManager.UserStates[chatId] = UserState.WaitingForManualReaderName;

                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
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

                    // Оновлюємо таблицю каталогу та лог видач через готові сервіси
                    await GoogleSheetsService.UpdateBookStatusAsync(title, "Доступна");
                    await GoogleSheetsService.LogReturnDateAsync(title);

                    // Прибираємо кнопку, щоб уникнути повторного кліку
                    await botClient.EditMessageReplyMarkup(chatId, callbackQuery.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
                    await botClient.SendMessage(chatId, $"✅ Книгу '<b>{title}</b>' успішно повернуто вручну від офлайн-користувача! Статус оновлено на 'Доступна'.", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
                }
            }
            // Обов'язкова відповідь серверу Telegram, щоб "годинничок" на кнопці перестав крутитися
            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
        }
        public static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine($"Помилка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}