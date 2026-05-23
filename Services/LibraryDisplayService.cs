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

            int pageSize = 10; // Кількість книг на одній сторінці

            // Фільтруємо порожні рядки, щоб каталог не "ламався"
            var validBooks = books.Select((b, i) => new { RowData = b, ActualIndex = i + 2 })
                                  .Where(b => b.RowData.Count > 0 && !string.IsNullOrWhiteSpace(b.RowData[0]?.ToString()))
                                  .ToList();

            int totalPages = (int)Math.Ceiling((double)validBooks.Count / pageSize);
            if (totalPages == 0) totalPages = 1;
            if (pageIndex < 0) pageIndex = 0;
            if (pageIndex >= totalPages) pageIndex = totalPages - 1;

            var pageBooks = validBooks.Skip(pageIndex * pageSize).Take(pageSize).ToList();

            // Формуємо красиву "шапку" каталогу
            string text = $"📚 <b>КАТАЛОГ КНИГ</b> (Сторінка {pageIndex + 1} з {totalPages})\n";
            text += "➖➖➖➖➖➖➖➖➖➖\n\n";

            var buttons = new List<InlineKeyboardButton[]>();

            // Додаємо кожну книгу з красивим форматуванням
            foreach (var item in pageBooks)
            {
                var row = item.RowData;

                string title = row.Count > 0 ? row[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = row.Count > 1 ? row[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";
                string genre = row.Count > 2 ? row[2]?.ToString() ?? "Не вказано" : "Не вказано";
                string status = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "Доступна" : "Доступна";
                string exchange = row.Count > 4 ? row[4]?.ToString()?.Trim() ?? "Так" : "Так"; // Колонка E

                bool isAvailable = status.Equals("Доступна", StringComparison.OrdinalIgnoreCase);
                string statusIcon = isAvailable ? "🟢" : "🔴";
                string exchangeTag = exchange.Equals("Ні", StringComparison.OrdinalIgnoreCase) ? " <i>(Не обмінюється)</i>" : "";

                text += $"📖 <b>{title}</b>\n";
                text += $"👤 <i>Автор: {author}</i>\n";
                text += $"🎭 Жанр: {genre}\n";
                text += $"📊 Статус: {statusIcon} <b>{status}</b>{exchangeTag}\n";
                text += "〰️〰️〰️〰️〰️〰️〰️〰️〰️〰️\n";
            }

            // Блок навігації (тільки перегортання сторінок та закриття)
            var navButtons = new List<InlineKeyboardButton>();
            if (pageIndex > 0) navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"page_{pageIndex - 1}"));
            navButtons.Add(InlineKeyboardButton.WithCallbackData("🔢 Сторінка", "jump_page"));
            if (pageIndex < totalPages - 1) navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ➡️", $"page_{pageIndex + 1}"));

            buttons.Add(navButtons.ToArray());
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Закрити", "close_catalog") });

            var keyboard = new InlineKeyboardMarkup(buttons);

            // Відправляємо або оновлюємо повідомлення
            if (messageIdToEdit.HasValue)
            {
                await botClient.EditMessageText(chatId, messageIdToEdit.Value, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
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

            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    foundBooks.Add((i + 2, row));
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🔍 Знайдено книг: {foundBooks.Count}. Ось результати:", cancellationToken: cancellationToken);
            // Отримуємо список книг, які зараз на руках у цього користувача
            var userBorrowedBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(telegramName);
            var userBorrowedRowIndexes = userBorrowedBooks.Select(b => b.CatalogRowIndex).ToHashSet();

            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";
                string genre = item.Data.Count > 2 ? item.Data[2]?.ToString() ?? "Не вказано" : "Не вказано";
                string status = item.Data.Count > 3 ? item.Data[3]?.ToString()?.Trim() ?? "" : "";

                bool isAvailable = string.IsNullOrWhiteSpace(status) || status.Equals("Доступна", StringComparison.OrdinalIgnoreCase);
                string statusIcon = isAvailable ? "🟢 Доступна" : "🔴 Читають"; // Було "На руках"

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n🎭 Жанр: {genre}\n📊 Статус: {statusIcon}";

                InlineKeyboardMarkup? keyboard = null;

                if (isAvailable)
                {
                    // Якщо доступна — кнопку бачать усі
                    var button = InlineKeyboardButton.WithCallbackData("📥 Взяти книгу", $"act_b_{item.RowIndex}");
                    keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                }
                else if (userBorrowedRowIndexes.Contains(item.RowIndex))
                {
                    // Якщо недоступна, АЛЕ її взяв саме цей користувач — показуємо йому кнопку повернення
                    var button = InlineKeyboardButton.WithCallbackData("📤 Повернути книгу", $"act_r_{item.RowIndex}");
                    keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                }
                // В іншому випадку (книга на руках у когось іншого) keyboard залишається null (кнопки не буде взагалі)

                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }

            if (foundBooks.Count > 5)
            {
                await botClient.SendMessage(chatId, "<i>Показано перші 5 результатів. Якщо ви не знайшли потрібну книгу, спробуйте ввести більш точну назву.</i>", parseMode: ParseMode.Html, cancellationToken: cancellationToken);
            }
        }

        public static async Task ShowUserBorrowedBooksAsync(ITelegramBotClient botClient, long chatId, string telegramName, CancellationToken cancellationToken)
        {
            // Отримуємо список книг користувача
            var borrowedBooks = await GoogleSheetsService.GetUserBorrowedBooksAsync(telegramName);

            if (borrowedBooks == null || borrowedBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, "🤷‍♂️ У вас немає книг на руках, які можна повернути.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"📚 Книги, які зараз у вас на руках (всього: {borrowedBooks.Count}):", cancellationToken: cancellationToken);

            // Виводимо кожну книгу окремим повідомленням з кнопкою
            foreach (var book in borrowedBooks)
            {
                string text = $"📖 <b>{book.Title}</b>\n📊 Статус: 🔴 Знаходиться у вас";

                // Перевикористовуємо вже готовий та налаштований ідентифікатор act_r_
                var button = InlineKeyboardButton.WithCallbackData("📤 Повернути цю книгу", $"act_r_{book.CatalogRowIndex}");
                var keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });

                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        public static async Task SearchBooksForDeletionAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    foundBooks.Add((i + 2, row));
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🗑 Знайдено книг для видалення: {foundBooks.Count}:", cancellationToken: cancellationToken);

            // Виводимо до 5 результатів, щоб не спамити
            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}";

                var button = InlineKeyboardButton.WithCallbackData("🗑 Видалити", $"act_del_{item.RowIndex}");
                var keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });

                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        public static async Task SearchBooksForEditingAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    foundBooks.Add((i + 2, row));
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"✏️ Знайдено книг для редагування: {foundBooks.Count}:", cancellationToken: cancellationToken);

            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";
                string genre = item.Data.Count > 2 ? item.Data[2]?.ToString() ?? "Не вказано" : "Не вказано";

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n🎭 Жанр: {genre}";

                var button = InlineKeyboardButton.WithCallbackData("✏️ Редагувати", $"act_edit_{item.RowIndex}");
                var keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });

                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }


        public static async Task SearchBooksForExchangeAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    foundBooks.Add((i + 2, row));
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🤝 Знайдено книг для обміну: {foundBooks.Count}:", cancellationToken: cancellationToken);

            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";

                // Читаємо статус обміну з нової колонки E (індекс 4)
                string exchangeStatus = item.Data.Count > 4 ? item.Data[4]?.ToString()?.Trim() ?? "Так" : "Так";

                // Книгу можна обміняти, якщо в колонці E не стоїть "Ні"
                bool canExchange = !exchangeStatus.Equals("Ні", StringComparison.OrdinalIgnoreCase);
                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n📊 Статус: {(canExchange ? "🟢 Можна обміняти" : "🚫 Не обмінюється")}";

                InlineKeyboardMarkup? keyboard = null;
                if (canExchange)
                {
                    var button = InlineKeyboardButton.WithCallbackData("🤝 Обміняти цю", $"act_exch_{item.RowIndex}");
                    keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                }

                await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        public static async Task SearchBooksForManualBorrowAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    foundBooks.Add((i + 2, row));
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 За запитом \"{query}\" нічого не знайдено.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"🤝 Знайдено книг для ручної видачі: {foundBooks.Count}:", cancellationToken: cancellationToken);

            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";
                string status = item.Data.Count > 3 ? item.Data[3]?.ToString()?.Trim() ?? "" : "";

                bool isAvailable = string.IsNullOrWhiteSpace(status) || status.Equals("Доступна", StringComparison.OrdinalIgnoreCase);

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n📊 Статус: {(isAvailable ? "🟢 Доступна" : "🔴 Недоступна")}";

                InlineKeyboardMarkup? keyboard = null;
                if (isAvailable)
                {
                    // Унікальний ідентифікатор act_man_b_ (Manual Borrow)
                    var button = InlineKeyboardButton.WithCallbackData("🤝 Видати вручну", $"act_man_b_{item.RowIndex}");
                    keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });
                }

                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }

        public static async Task SearchBooksForManualReturnAsync(ITelegramBotClient botClient, long chatId, string query, CancellationToken cancellationToken)
        {
            var books = await GoogleSheetsService.GetBooksAsync();
            SessionManager.ClearSession(chatId);

            if (books == null || books.Count == 0)
            {
                await botClient.SendMessage(chatId, "Каталог порожній.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            // Шукаємо збіги тільки серед виданих книг
            var foundBooks = new List<(int RowIndex, IList<object> Data)>();
            for (int i = 0; i < books.Count; i++)
            {
                var row = books[i];
                if (row.Count > 0 &&
                    ((row[0]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                     (row.Count > 1 && row[1]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    string status = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "" : "";
                    bool isAvailable = string.IsNullOrWhiteSpace(status) || status.Equals("Доступна", StringComparison.OrdinalIgnoreCase);

                    // Якщо книга НЕ доступна — значить її можна повернути
                    if (!isAvailable)
                    {
                        foundBooks.Add((i + 2, row));
                    }
                }
            }

            if (foundBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, $"🔍 Серед виданих книг за запитом \"{query}\" нічого не знайдено.", replyMarkup: UI.KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            await botClient.SendMessage(chatId, $"📥 Знайдено виданих книг: {foundBooks.Count}. Оберіть ту, яку принесли:", cancellationToken: cancellationToken);

            foreach (var item in foundBooks.Take(5))
            {
                string title = item.Data.Count > 0 ? item.Data[0]?.ToString() ?? "Без назви" : "Без назви";
                string author = item.Data.Count > 1 ? item.Data[1]?.ToString() ?? "Невідомий автор" : "Невідомий автор";

                // Замінюємо дефолтне значення на "Читають"
                string status = item.Data.Count > 3 ? item.Data[3]?.ToString()?.Trim() ?? "Читають" : "Читають";

                string text = $"📖 <b>{title}</b>\n👤 Автор: {author}\n📊 Статус: 🔴 {status}";

                // Створюємо унікальний CallbackData індивідуально для ручного повернення (act_man_r_)
                var button = InlineKeyboardButton.WithCallbackData("📤 Повернути в бібліотеку", $"act_man_r_{item.RowIndex}");
                var keyboard = new InlineKeyboardMarkup(new[] { new[] { button } });

                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: keyboard, cancellationToken: cancellationToken);
            }
        }


    }
}