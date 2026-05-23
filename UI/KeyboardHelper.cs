using Telegram.Bot.Types.ReplyMarkups;
using LibraryBot.Services; // Щоб отримати доступ до SessionManager.AdminId

namespace LibraryBot.UI
{
    public static class KeyboardHelper
    {
        // Універсальний метод, який сам підбирає меню
        public static ReplyKeyboardMarkup GetMenu(long chatId)
        {
            if (SessionManager.AdminIds.Contains(chatId))
                return GetAdminMenu();

            return GetUserMenu();
        }

        private static ReplyKeyboardMarkup GetUserMenu()
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "📚 Каталог", "🔍 Пошук" },
                new KeyboardButton[] { "📥 Взяти книгу", "📤 Повернути книгу" },
                new KeyboardButton[] { "ℹ️ Допомога", "❌ Скасувати дію" }
            })
            {
                ResizeKeyboard = true
            };
        }

        private static ReplyKeyboardMarkup GetAdminMenu()
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "📚 Каталог", "🔍 Пошук" },
                new KeyboardButton[] { "➕ Додати", "✏️ Редагувати", "🗑 Видалити" },
                new KeyboardButton[] { "📥 Видати вручну", "📤 Повернути вручну" },
                new KeyboardButton[] { "🤝 Обмін", "❌ Скасувати дію" }
            })
            {
                ResizeKeyboard = true
            };
        }
    }
}