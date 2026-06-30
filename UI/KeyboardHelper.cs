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
        //"🤝 Обміняти",
        private static ReplyKeyboardMarkup GetUserMenu()
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "📚 Каталог", "🔍 Пошук" },
                new KeyboardButton[] { "📥 Взяти книгу", "📤 Повернути книгу" },
                new KeyboardButton[] { "ℹ️ Допомога", "👤 Мій профіль"  },
                new KeyboardButton[] {"❌ Скасувати дію"  }

            })
            {
                ResizeKeyboard = true
            };
        }
        private static ReplyKeyboardMarkup GetAdminMenu()
        {
            return new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "📚 Каталог", "📋 Список видач","🤝 Обмін" },
                new KeyboardButton[] { "➕ Додати", "✏️ Редагувати", "🗑 Видалити" },
                new KeyboardButton[] { "📥 Видати вручну", "📤 Повернути вручну" },
                new KeyboardButton[] { "📊 Статистика", "❌ Скасувати дію"}
            })
            {
                ResizeKeyboard = true
            };
        }
    }
}