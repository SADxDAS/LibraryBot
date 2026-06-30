using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace LibraryBot.Commands
{
    // ==========================================
    // СИСТЕМНІ ТА ЗАГАЛЬНІ КОМАНДИ
    // ==========================================

    public class StartCommand : ICommand
    {
        public string[] Triggers => new[] { "/start" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            string startText = "👋 <b>Привіт! Це бібліотека «Паперового Життя»</b> 📚\n\n" +
                               "Тут ви можете:\n" +
                               "📥 <b>Взяти книгу</b> на прочитання\n" +
                               "📤 <b>Повернути ту</b>, що вже прочитали\n" +
                               "🤝 <b>Обміняти буккросингом</b> свої книги з нашими 💚\n\n" +
                               "<i>Оберіть потрібну дію в меню нижче 👇</i>";
            await botClient.SendMessage(chatId, startText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
        }
    }

    public class HelpCommand : ICommand
    {
        public string[] Triggers => new[] { "/help", "ℹ️ Допомога" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
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
            await botClient.SendMessage(chatId, helpText, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
        }
    }

    public class CancelCommand : ICommand
    {
        public string[] Triggers => new[] { "/cancel", "❌ Скасувати дію" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            await botClient.SendMessage(chatId, "Дію скасовано. Повернення до головного меню.", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
        }
    }

    public class MyIdCommand : ICommand
    {
        public string[] Triggers => new[] { "/myid" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            await botClient.SendMessage(message.Chat.Id, $"Ваш Telegram ID: {message.Chat.Id}", cancellationToken: cancellationToken);
        }
    }

    public class TestRemindersCommand : ICommand
    {
        public string[] Triggers => new[] { "/test_reminders" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;
            await botClient.SendMessage(chatId, "🔄 Запускаю перевірку бази та розсилку дедлайнів...", cancellationToken: cancellationToken);
            await NotificationService.CheckDeadlinesAsync(botClient, cancellationToken);
            await botClient.SendMessage(chatId, "✅ Тестова перевірка завершена!", cancellationToken: cancellationToken);
        }
    }

    // ==========================================
    // КОМАНДИ КОРИСТУВАЧА
    // ==========================================

    public class CatalogCommand : ICommand
    {
        public string[] Triggers => new[] { "/books", "📚 Каталог" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            SessionManager.ClearSession(message.Chat.Id);
            await LibraryDisplayService.SendOrEditBooksPageAsync(botClient, message.Chat.Id, 0, null, cancellationToken);
        }
    }

    public class BorrowCommand : ICommand
    {
        public string[] Triggers => new[] { "/borrow", "📥 Взяти книгу" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора, щоб знайти книгу:", cancellationToken: cancellationToken);
        }
    }

    public class ReturnCommand : ICommand
    {
        public string[] Triggers => new[] { "/return", "📤 Повернути книгу" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            await LibraryDisplayService.ShowUserBorrowedBooksAsync(botClient, chatId, cancellationToken);
        }
    }

    public class SearchCommand : ICommand
    {
        public string[] Triggers => new[] { "/search", "🔍 Пошук" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора (можна частково) для пошуку:", cancellationToken: cancellationToken);
        }
    }

    public class UserExchangeCommand : ICommand
    {
        public string[] Triggers => new[] { "/exchange", "🤝 Обміняти" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForUserExchangeTitle;
            SessionManager.UserExchangeSessions[chatId] = new UserExchangeSession();
            await botClient.SendMessage(chatId, "🤝 <b>Оформлення обміну книги</b>\n\nБудь ласка, введіть <b>НАЗВУ</b> книги, яку ви хочете принести до бібліотеки:", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
        }
    }

    // ==========================================
    // КОМАНДИ АДМІНІСТРАТОРА
    // ==========================================

    public class AddBookCommand : ICommand
    {
        public string[] Triggers => new[] { "/add", "➕ Додати" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);

            // ВИПРАВЛЕНО: Явно вказуємо EditRowIndex = -1
            var session = new AdminBookSession { CurrentStep = 1, EditRowIndex = -1 };

            SessionManager.AdminBookSessions[chatId] = session;

            await WizardHelper.SendOrUpdateWizardAsync(botClient, chatId, session, null, cancellationToken);
        }
    }
    public class DeleteBookCommand : ICommand
    {
        public string[] Triggers => new[] { "/delete", "🗑 Видалити" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForDeleteSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАЛИТИ:", cancellationToken: cancellationToken);
        }
    }

    public class EditBookCommand : ICommand
    {
        public string[] Triggers => new[] { "/edit", "✏️ Редагувати" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForEditSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВІДРЕДАГУВАТИ:", cancellationToken: cancellationToken);
        }
    }

    public class AdminExchangeCommand : ICommand
    {
        public string[] Triggers => new[] { "/handexchange", "🤝 Обмін" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForExchangeSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги з бібліотеки, яку хочете ОБМІНЯТИ:", cancellationToken: cancellationToken);
        }
    }

    public class ManualBorrowCommand : ICommand
    {
        public string[] Triggers => new[] { "/handgive", "📥 Видати вручну" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForManualSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку книги, яку хочете ВИДАТИ ВРУЧНУ:", cancellationToken: cancellationToken);
        }
    }

    public class ManualReturnCommand : ICommand
    {
        public string[] Triggers => new[] { "/handreturn", "📤 Повернути вручну" };
        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            SessionManager.UserStates[chatId] = UserState.WaitingForManualReturnSearchQuery;
            await botClient.SendMessage(chatId, "🔍 Введіть назву книги або автора для пошуку серед книг, які зараз знаходяться НА РУКАХ:", cancellationToken: cancellationToken);
        }

    }

    // --- ОНОВЛЕНА КОМАНДА ПРОФІЛЮ ---
    public class MyProfileCommand : ICommand
    {
        public string[] Triggers => new[] { "/profile", "👤 Мій профіль" };

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            SessionManager.ClearSession(chatId);

            var loadingMsg = await botClient.SendMessage(chatId, "⏳ Формую вашу читацьку картку...", cancellationToken: cancellationToken);

            var profileData = await LibraryDbService.GetUserProfileAsync(chatId);

            // НОВЕ: Завантажуємо дані профілю з бази даних (ім'я та контакт)
            var dbUser = await LibraryDbService.GetUserAsync(chatId);
            string realName = dbUser != null && !string.IsNullOrEmpty(dbUser.RealName) ? dbUser.RealName : "Не вказано";
            string contactInfo = dbUser != null && !string.IsNullOrEmpty(dbUser.Contact) ? dbUser.Contact : "Не вказано";

            // ГЕЙМІФІКАЦІЯ: Визначаємо ранг користувача
            string rank = "Новачок 🌱";
            if (profileData.ReadCount >= 20) rank = "Бібліотечний Магістр 🧙‍♂️";
            else if (profileData.ReadCount >= 10) rank = "Експерт 🦉";
            else if (profileData.ReadCount >= 5) rank = "Книголюб 📚";
            else if (profileData.ReadCount >= 1) rank = "Читач 📖";

            string tgName = message.Chat.FirstName ?? "Читач";

            string text = $"👤 <b>Профіль: {TextUtils.EscapeHtml(tgName)}</b>\n";
            text += $"🏆 Звання: <b>{rank}</b>\n";
            text += $"📖 Прочитано книг: <b>{profileData.ReadCount}</b>\n";
            text += $"⏳ Зараз на руках: <b>{profileData.CurrentlyReadingCount}</b>\n\n";

            // НОВЕ: Додаємо інформацію про ім'я та контакт у повідомлення
            text += $"📋 <b>Ваші дані для бібліотеки:</b>\n";
            text += $"Ім'я: <b>{TextUtils.EscapeHtml(realName)}</b>\n";
            text += $"Контакт: <code>{TextUtils.EscapeHtml(contactInfo)}</code>\n\n";

            if (profileData.CurrentlyReadingCount > 0)
            {
                text += "<b>Зараз ви читаєте:</b>\n";
                foreach (var b in profileData.CurrentBooks) text += $"🔸 <i>{TextUtils.EscapeHtml(b)}</i>\n";
                text += "\n";
            }

            if (profileData.ReadCount > 0)
            {
                text += "<b>Ваша історія прочитаного:</b>\n";
                var recentRead = System.Linq.Enumerable.Reverse(profileData.ReadBooks).Take(10);
                foreach (var b in recentRead) text += $"✅ <i>{TextUtils.EscapeHtml(b)}</i>\n";

                if (profileData.ReadCount > 10)
                    text += $"<i>...та ще {profileData.ReadCount - 10} книг(и)</i>\n";
            }
            else
            {
                text += "<i>Ви ще не прочитали жодної книги з нашої бібліотеки. Час це виправити! 😉</i>";
            }

            await botClient.DeleteMessage(chatId, loadingMsg.MessageId, cancellationToken);

            // НОВЕ: Створюємо інлайн-кнопку для редагування
            var inlineKeyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(new[]
            {
                new[] { Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("✏️ Редагувати дані", "edit_profile") }
            });

            // Відправляємо профіль з інлайн-кнопкою
            await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);

            // Відправляємо окреме повідомлення, щоб зберегти звичайну клавіатуру меню знизу
            await botClient.SendMessage(chatId, "<i>Оберіть дію в меню 👇</i>", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
        }
    }

    public class AdminStatsCommand : ICommand
    {
        public string[] Triggers => new[] { "/stats", "📊 Статистика" };

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);

            var loadingMsg = await botClient.SendMessage(chatId, "⏳ Збираю статистику...", cancellationToken: cancellationToken);

            var stats = await LibraryDbService.GetLibraryStatisticsAsync();

            string text = "📊 <b>Статистика Бібліотеки</b>\n\n";
            text += $"📚 Загальний книжковий фонд: <b>{stats.TotalBooks}</b> прим.\n";
            text += $"🟢 Доступно на полицях: <b>{stats.AvailableBooks}</b> прим.\n";
            text += $"🤝 На руках у читачів: <b>{stats.BorrowedBooks}</b> прим.\n\n";

            if (stats.OverdueBooks > 0)
                text += $"⚠️ Боржники (Протерміновано): <b>{stats.OverdueBooks}</b> ❗️\n";
            else
                text += $"✅ Боржники (Протерміновано): <b>0</b>\n";

            if (stats.PendingRequests > 0)
                text += $"📩 Необроблені запити в черзі: <b>{stats.PendingRequests}</b> 🔔\n";
            else
                text += $"📩 Необроблені запити: <b>0</b>\n";

            await botClient.DeleteMessage(chatId, loadingMsg.MessageId, cancellationToken);

            if (stats.OverdueBooks > 0)
            {
                var inlineKeyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(new[]
                {
                    Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("🚨 Показати боржників", "view_overdue")
                });
                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
            }
        }
    }
    public class AuditCommand : ICommand
    {
        public string[] Triggers => new[] { "/audit", "🔎 Аудит" };

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);
            var loadingMsg = await botClient.SendMessage(chatId, "⏳ Запускаю перевірку бази даних...", cancellationToken: cancellationToken);

            var lostBooks = await LibraryDbService.GetLostBooksAsync();

            await botClient.DeleteMessage(chatId, loadingMsg.MessageId, cancellationToken);

            if (lostBooks.Count == 0)
            {
                await botClient.SendMessage(chatId, "✅ <b>Усе ідеально сходиться!</b>\nЖодного загубленого примірника не знайдено.", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, cancellationToken: cancellationToken);
                return;
            }

            string text = "⚠️ <b>Знайдено розбіжності у фонді!</b>\nЦі книги недоступні для видачі, але їх немає на руках у читачів:\n\n";
            var buttons = new List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton[]>();

            foreach (var b in lostBooks)
            {
                text += $"📖 <b>{TextUtils.EscapeHtml(b.Title)}</b>\n";
                text += $"❓ Загублено примірників: <b>{b.LostCount}</b>\n➖➖➖➖➖➖\n";

                // Обрізаємо назву для кнопки, якщо вона задовга
                string shortTitle = b.Title.Length > 20 ? b.Title.Substring(0, 17) + "..." : b.Title;

                // Створюємо інлайн кнопку для кожної проблемної книги
                buttons.Add(new[] { Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData($"➕ Повернути +1 ({shortTitle})", $"fix_lost_{b.BookId}") });
            }

            var inlineKeyboard = new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(buttons);
            await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);
        }
    }
    public class AdminBorrowingsListCommand : ICommand
    {
        public string[] Triggers => new[] { "/borrowings", "📋 Список видач" };

        public async Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            if (!SessionManager.AdminIds.Contains(chatId)) return;

            SessionManager.ClearSession(chatId);

            var loadingMsg = await botClient.SendMessage(chatId, "⏳ Завантажую список активних видач...", cancellationToken: cancellationToken);

            var activeList = await LibraryDbService.GetActiveBorrowingsAsync();

            await botClient.DeleteMessage(chatId, loadingMsg.MessageId, cancellationToken);

            if (activeList.Count == 0)
            {
                await botClient.SendMessage(chatId, "✅ Наразі немає виданих книг (всі книги в бібліотеці).", replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
                return;
            }

            int page = 1;
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

            var inlineKeyboard = GetPaginationKeyboard(page, totalPages);

            await botClient.SendMessage(chatId, text, parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: inlineKeyboard, cancellationToken: cancellationToken);
            await botClient.SendMessage(chatId, "<i>Оберіть дію в меню 👇</i>", parseMode: Telegram.Bot.Types.Enums.ParseMode.Html, replyMarkup: KeyboardHelper.GetMenu(chatId), cancellationToken: cancellationToken);
        }

        public static Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup GetPaginationKeyboard(int currentPage, int totalPages)
        {
            if (totalPages <= 1) return null;

            var buttons = new List<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>();

            if (currentPage > 1)
                buttons.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"borrow_page_{currentPage - 1}"));

            if (currentPage < totalPages)
                buttons.Add(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton.WithCallbackData("Вперед ➡️", $"borrow_page_{currentPage + 1}"));

            return new Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup(buttons);
        }
    }
}