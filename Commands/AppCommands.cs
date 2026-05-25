using LibraryBot.Models;
using LibraryBot.Services;
using LibraryBot.UI;
using System.Threading;
using System.Threading.Tasks;
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
            string tgName = message.Chat.FirstName ?? "Без імені";
            if (!string.IsNullOrEmpty(message.Chat.Username)) tgName += $" (@{message.Chat.Username})";
            await LibraryDisplayService.ShowUserBorrowedBooksAsync(botClient, chatId, tgName, cancellationToken);
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
        public string[] Triggers => new[] { "🤝 Обміняти книгу" };
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
            SessionManager.UserStates[chatId] = UserState.WaitingForAddBookTitle;
            SessionManager.AdminBookSessions[chatId] = new AdminBookSession();
            await botClient.SendMessage(chatId, "📝 Введіть НАЗВУ нової книги:", cancellationToken: cancellationToken);
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
        public string[] Triggers => new[] { "/exchange", "🤝 Обмін" };
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
}