namespace LibraryBot.Services
{
    /// <summary>
    /// Допоміжні функції для безпечного формування повідомлень Telegram.
    /// </summary>
    public static class TextUtils
    {
        /// <summary>
        /// Екранує текст для ParseMode.Html. Дані від користувача (назви книг, імена,
        /// контакти) можуть містити &amp; &lt; &gt; — без екранування Telegram відхиляє
        /// все повідомлення ("can't parse entities"). Завжди проганяйте динамічний
        /// вміст через цей метод перед вставкою в HTML-повідомлення.
        /// </summary>
        public static string EscapeHtml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
