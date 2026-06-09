using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using LibraryBot.UI;

namespace LibraryBot.Services
{
    public static class LibraryDisplayService
    {
        public static async Task SendOrEditBooksPageAsync(ITelegramBotClient botClient, long chatId, int pageIndex, int? messageIdToEdit, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();

            if (books == null || books.Count == 0)
            {
                if (messageIdToEdit.HasValue)
                    await botClient.EditMessageText(chatId, messageIdToEdit.Value, "📭 Каталог наразі порожній.", cancellationToken: cancellationToken);
                else
                    await botClient.SendMessage(chatId, "📭 Каталог наразі порожній.", cancellationToken: cancellationToken);
                return;
            }

            int pageSize = 10;
            var validBooks = books.Select((b, i) => new { RowData = b, ActualIndex = i + 2 })
                                  .Where(b => b.RowData.Count > 0
                                              && !string.IsNullOrWhiteSpace(b.RowData[0]?.ToString())
                                              && CardTotal(b.RowData) > 0) // приховуємо списані книги (Total=0)
                                  .ToList();

            int totalPages = (int)Math.Ceiling((double)validBooks.Count / pageSize);
            if (totalPages == 0) totalPages = 1;
            if (pageIndex < 0) pageIndex = 0;
            if (pageIndex >= totalPages) pageIndex = totalPages - 1;

            var pageBooks = validBooks.Skip(pageIndex * pageSize).Take(pageSize).ToList();

            string text = $"📚 <b>КАТАЛОГ КНИГ</b> (Сторінка {pageIndex + 1} з {totalPages})\n";
            text += "➖➖➖➖➖➖➖➖➖➖\n\n";

            var buttons = new List<InlineKeyboardButton[]>();

            foreach (var item in pageBooks)
            {
                var row = item.RowData;

                string title = TextUtils.EscapeHtml(row.Count > 0 ? row[0]?.ToString() ?? "Без назви" : "Без назви");
                string author = TextUtils.EscapeHtml(row.Count > 1 ? row[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор");
                string genre = TextUtils.EscapeHtml(row.Count > 2 ? row[2]?.ToString() ?? "Не вказано" : "Не вказано");
                string exchange = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "Так" : "Так";

                int disponible = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int d) ? d : 0 : 0;
                int total = row.Count > 5 ? int.TryParse(row[5]?.ToString(), out int t) ? t : 1 : 1;
                int reading = total - disponible;

                bool isAvailable = disponible > 0;
                string statusIcon = isAvailable ? "🟢" : "🔴";
                string exchangeTag = exchange.Equals("Ні", StringComparison.OrdinalIgnoreCase) ? " <i>(Не обмінюється)</i>" : "";

                string statusText = total > 1
                    ? $"Доступно {disponible} | Читають {reading}"
                    : (disponible > 0 ? "Доступна" : "Читають");

                text += $"📖 <b>{title}</b>\n";
                text += $"👤 <i>Автор: {author}</i>\n";
                text += $"🎭 Жанр: {genre}\n";
                text += $"📊 Статус: {statusIcon} <b>{statusText}</b>{exchangeTag}\n";
                text += "〰️〰️〰️〰️〰️〰️〰️〰️〰️〰️\n";
            }

            var navButtons = new List<InlineKeyboardButton>();
            if (pageIndex > 0) navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"page_{pageIndex - 1}"));
            navButtons.Add(InlineKeyboardButton.WithCallbackData("🔢 Сторінка", "jump_page"));
            if (pageIndex < totalPages - 1) navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ➡️", $"page_{pageIndex + 1}"));

            buttons.Add(navButtons.ToArray());
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Закрити", "close_catalog") });

            var keyboard = new InlineKeyboardMarkup(buttons);

            if (messageIdToEdit.HasValue)
                await botClient.EditMessageText(chatId, messageIdToEdit.Value, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            else
                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Спільний універсальний нечіткий пошук по каталогу.
        /// Повертає книги, відсортовані за релевантністю (найточніші — першими).
        /// BookId — стабільний первинний ключ DbBook.Id (для адресації в колбеках).
        /// </summary>
        private static List<(int BookId, IList<object> Data)> FuzzyFind(IList<IList<object>> books, string query)
        {
            // Компілюємо (нормалізуємо + токенізуємо) запит ОДИН раз, а не для кожної книги.
            var compiledQuery = BookSearch.Compile(query);

            var scored = new List<(int BookId, IList<object> Data, double Score)>();
            foreach (var row in books)
            {
                if (row.Count == 0) continue;
                if (CardTotal(row) <= 0) continue; // приховані (списані) книги — Total=0 — не показуємо в пошуку

                string title = row[GoogleSheetsService.COL_CATALOG_TITLE]?.ToString() ?? "";
                string author = row.Count > 1 ? row[GoogleSheetsService.COL_CATALOG_AUTHOR]?.ToString() ?? "" : "";

                double score = compiledQuery.Score(title, author);
                if (score > 0)
                {
                    int bookId = row.Count > GoogleSheetsService.COL_CATALOG_ID
                        ? Convert.ToInt32(row[GoogleSheetsService.COL_CATALOG_ID])
                        : 0;
                    scored.Add((bookId, row, score));
                }
            }

            return scored
                .OrderByDescending(x => x.Score)
                .Select(x => (x.BookId, x.Data))
                .ToList();
        }

        public static async Task SearchBooksAsync(ITelegramBotClient botClient, long chatId, string query, string telegramName, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            // Універсальний нечіткий пошук по словах, відсортований за релевантністю.
            var foundBooks = FuzzyFind(books, query);

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🔍 Знайдено книг: {foundBooks.Count}. Ось результати:", cancellationToken: cancellationToken);
            var userBorrowedBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(telegramName);
            var userBorrowedRowIndexes = userBorrowedBooks.Select(b => b.BookId).ToHashSet();

            foreach (var item in foundBooks.Take(5))
            {
                string title = TextUtils.EscapeHtml(item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви");
                string author = TextUtils.EscapeHtml(item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор");
                string genre = TextUtils.EscapeHtml(item.Data.Count > 2 ? item.Data[2]?.ToString() ?? "Не вказано" : "Не вказано");

                int disponible = item.Data.Count > 4 ? int.TryParse(item.Data[4]?.ToString(), out int d) ? d : 0 : 0;
                int total = item.Data.Count > 5 ? int.TryParse(item.Data[5]?.ToString(), out int t) ? t : 1 : 1;
                int reading = total - disponible;

                bool isAvailable = disponible > 0;
                string statusIcon = isAvailable ? "🟢" : "🔴";

                string statusText = total > 1
                    ? $"Доступно {disponible} | Читають {reading}"
                    : (disponible > 0 ? "Доступна" : "Читають");

                // Чи доступна книга для обміну (буккросингу)
                string exchange = item.Data.Count > GoogleSheetsService.COL_CATALOG_EXCHANGE
                    ? item.Data[GoogleSheetsService.COL_CATALOG_EXCHANGE]?.ToString()?.Trim() ?? "Так"
                    : "Так";
                bool isExchangeable = !exchange.Equals("Ні", StringComparison.OrdinalIgnoreCase);
                string exchangeLine = !isExchangeable
                    ? "🔄 Обмін: 🚫 Не обмінюється"
                    : (disponible > 0
                        ? "🔄 Обмін: ✅ Доступна для обміну"
                        : "🔄 Обмін: 🟡 Можливий, але всі примірники на руках");

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n🎭 Жанр: {genre}\n📊 Статус: {statusIcon} {statusText}\n{exchangeLine}";

                var keyboardButtons = new List<InlineKeyboardButton[]>();

                if (isAvailable)
                {
                    keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData($"📥 Взяти книгу (Доступно: {disponible})", $"act_b_{item.BookId}") });
                }

                if (userBorrowedRowIndexes.Contains(item.BookId))
                {
                    keyboardButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("📤 Повернути мій примірник", $"act_r_{item.BookId}") });
                }

                InlineKeyboardMarkup? keyboard = keyboardButtons.Count > 0 ? new InlineKeyboardMarkup(keyboardButtons) : null;

                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }

            if (foundBooks.Count > 5)
            {
                await botClient.SendMessage(chatId, "<i>Показано перші 5 результатів. Спробуйте ввести більш точну назву.</i>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
        }

        public static async Task ShowUserBorrowedBooksAsync(ITelegramBotClient botClient, long chatId, string telegramName, CancellationToken cancellationToken)
        {
            var borrowedBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(telegramName);

            if (borrowedBooks == null || borrowedBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, "🤷‍♂️ У вас немає книг на руках, які можна повернути.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"📚 Книги, які зараз у вас на руках (всього: {borrowedBooks.Count}):", cancellationToken: cancellationToken);

            foreach (var book in borrowedBooks)
            {
                string text = $"📖 <b>{TextUtils.EscapeHtml(book.Title)}</b>\n📊 Статус: 🔴 Знаходиться у вас";
                var button = InlineKeyboardButton.WithCallbackData("📤 Повернути цю книгу", $"act_r_{book.BookId}");
                var keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        // ── Спільний рушій для адмінських/обмінних пошуків ────────────────
        // Уся однакова обвʼязка (отримання каталогу, скидання сесії, перевірка
        // порожнечі, нечіткий пошук, "нічого не знайдено", заголовок, цикл по 5)
        // зібрана тут. Кожен конкретний пошук передає лише заголовок, текст
        // "не знайдено", необовʼязковий фільтр і функцію побудови картки.
        private static async Task RenderSearchAsync(
            ITelegramBotClient botClient, long chatId, string query,
            Func<int, string> headerText,
            string notFoundText,
            Func<(int BookId, IList<object> Data), (string Text, InlineKeyboardMarkup? Keyboard)> buildCard,
            CancellationToken cancellationToken,
            Func<(int BookId, IList<object> Data), bool>? filter = null)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var found = FuzzyFind(books, query);
            if (filter != null) found = found.Where(filter).ToList();

            if (found.Count == 0)
            {
                await botClient.SendMessage(chatId, notFoundText, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, headerText(found.Count), cancellationToken: cancellationToken);

            foreach (var item in found.Take(5))
            {
                var (text, keyboard) = buildCard(item);
                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        // Дрібні екстрактори полів книги з legacy-рядка (з екрануванням для HTML).
        private static string CardTitle(IList<object> d) => TextUtils.EscapeHtml(d.Count > 0 ? d[0]?.ToString() ?? "Без назви" : "Без назви");
        private static string CardAuthor(IList<object> d) => TextUtils.EscapeHtml(d.Count > 1 ? d[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор");
        private static string CardGenre(IList<object> d) => TextUtils.EscapeHtml(d.Count > 2 ? d[2]?.ToString() ?? "Не вказано" : "Не вказано");
        private static int CardAvailable(IList<object> d) => d.Count > 4 && int.TryParse(d[4]?.ToString(), out int v) ? v : 0;
        private static int CardTotal(IList<object> d) => d.Count > 5 && int.TryParse(d[5]?.ToString(), out int v) ? v : 1;
        private static InlineKeyboardMarkup OneButton(string label, string data)
            => new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData(label, data) } });

        public static Task SearchBooksForDeletionAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
            => RenderSearchAsync(botClient, chatId, query,
                count => $"🗑 Знайдено книг для видалення: {count}:",
                $"🔍 За запитом \"{query}\" нічого не знайдено.",
                item => (
                    $"📖 <b>{CardTitle(item.Data)}</b>\n👤 Автор: {CardAuthor(item.Data)}",
                    OneButton("🗑 Видалити", $"act_del_{item.BookId}")),
                cancellationToken);

        public static Task SearchBooksForEditingAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
            => RenderSearchAsync(botClient, chatId, query,
                count => $"✏️ Знайдено книг для редагування: {count}:",
                $"🔍 За запитом \"{query}\" нічого не знайдено.",
                item => (
                    $"📖 <b>{CardTitle(item.Data)}</b>\n👤 Автор: {CardAuthor(item.Data)}\n🎭 Жанр: {CardGenre(item.Data)}",
                    OneButton("✏️ Редагувати", $"act_edit_{item.BookId}")),
                cancellationToken);

        public static Task SearchBooksForExchangeAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
            => RenderSearchAsync(botClient, chatId, query,
                count => $"🤝 Знайдено книг для обміну: {count}:",
                $"🔍 За запитом \"{query}\" нічого не знайдено.",
                item =>
                {
                    string exchangeStatus = item.Data.Count > 3 ? item.Data[3]?.ToString()?.Trim() ?? "Так" : "Так";
                    int disponible = CardAvailable(item.Data);
                    bool isExchangeable = !exchangeStatus.Equals("Ні", StringComparison.OrdinalIgnoreCase);
                    bool canExchangeNow = isExchangeable && disponible > 0;

                    string statusText = !isExchangeable
                        ? "🚫 Не обмінюється"
                        : (disponible <= 0 ? "🔴 Немає в наявності (всі на руках)" : $"🟢 Можна обміняти (Доступно: {disponible})");

                    string text = $"📖 <b>{CardTitle(item.Data)}</b>\n👤 Автор: {CardAuthor(item.Data)}\n📊 Статус: {statusText}";
                    InlineKeyboardMarkup? kb = canExchangeNow ? OneButton("🤝 Обміняти цю", $"act_exch_{item.BookId}") : null;
                    return (text, kb);
                },
                cancellationToken);

        public static Task SearchBooksForManualBorrowAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
            => RenderSearchAsync(botClient, chatId, query,
                count => $"🤝 Знайдено книг для ручної видачі: {count}:",
                $"🔍 За запитом \"{query}\" нічого не знайдено.",
                item =>
                {
                    bool isAvailable = CardAvailable(item.Data) > 0;
                    string text = $"📖 <b>{CardTitle(item.Data)}</b>\n👤 Автор: {CardAuthor(item.Data)}\n📊 Статус: {(isAvailable ? "🟢 Доступна" : "🔴 Недоступна")}";
                    InlineKeyboardMarkup? kb = isAvailable ? OneButton("🤝 Видати вручну", $"act_man_b_{item.BookId}") : null;
                    return (text, kb);
                },
                cancellationToken);

        public static Task SearchBooksForManualReturnAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
            => RenderSearchAsync(botClient, chatId, query,
                count => $"📥 Знайдено виданих книг: {count}. Оберіть ту, яку принесли:",
                $"🔍 Серед виданих книг за запитом \"{query}\" нічого не знайдено.",
                item => (
                    $"📖 <b>{CardTitle(item.Data)}</b>\n👤 Автор: {CardAuthor(item.Data)}\n📊 Статус: 🔴 Читають",
                    OneButton("📤 Повернути в бібліотеку", $"act_man_r_{item.BookId}")),
                cancellationToken,
                filter: item => CardTotal(item.Data) - CardAvailable(item.Data) > 0); // лише ті, що на руках

        // ПОШУК ДЛЯ КОРИСТУВАЦЬКОГО ОБМІНУ
        public static async Task SearchBooksForUserExchangeAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var foundBooks = FuzzyFind(books, query);

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено. Спробуйте ввести іншу назву:", cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🔍 Знайдено книг: {foundBooks.Count}. Оберіть ту, яку хочете отримати натомість:", cancellationToken: cancellationToken);

            foreach (var item in foundBooks.Take(5))
            {
                string title = TextUtils.EscapeHtml(item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви");
                string author = TextUtils.EscapeHtml(item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор");

                int disponible = item.Data.Count > 4 ? int.TryParse(item.Data[4]?.ToString(), out int d) ? d : 0 : 0;
                bool isAvailable = disponible > 0;

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n📊 Статус: {(isAvailable ? $"🟢 Доступно ({disponible} шт.)" : "🔴 Немає в наявності (всі на руках)")}";

                InlineKeyboardMarkup? keyboard = null;
                if (isAvailable)
                {
                    var button = InlineKeyboardButton.WithCallbackData("🔄 Обміняти на цю книгу", $"userex_sel_{item.BookId}");
                    keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                }

                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }
    }
}