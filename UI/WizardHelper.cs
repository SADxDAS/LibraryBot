using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;
using LibraryBot.Models;
using LibraryBot.Services;
using System.Linq;

namespace LibraryBot.UI
{
    public static class WizardHelper
    {
        public static async Task SendOrUpdateWizardAsync(ITelegramBotClient botClient, long chatId, AdminBookSession session, int? existingMsgId = null, CancellationToken ct = default)
        {
            string text = $"🛠 <b>Майстер додавання книги (Крок {session.CurrentStep} з 5)</b>\n\n";
            text += $"📖 Назва: {(string.IsNullOrEmpty(session.Title) ? "<i>не вказано</i>" : $"<b>{TextUtils.EscapeHtml(session.Title)}</b>")}\n";
            text += $"👤 Автор: {(string.IsNullOrEmpty(session.Author) ? "<i>не вказано</i>" : $"<b>{TextUtils.EscapeHtml(session.Author)}</b>")}\n";
            text += $"🎭 Жанр: {(string.IsNullOrEmpty(session.Genre) ? "<i>не вказано</i>" : $"<b>{TextUtils.EscapeHtml(session.Genre)}</b>")}\n";
            text += $"📦 Кількість: <b>{session.CurrentAvailable}</b>\n";
            text += $"🔄 Обмін: <b>{session.ExchangeStatus}</b>\n\n";

            var buttons = new List<InlineKeyboardButton[]>();

            if (session.CurrentStep == 1)
            {
                text += "👉 <b>Введіть НАЗВУ книги текстовим повідомленням у чат:</b>";

                // ДОДАНО: Копіювання по кліку
                if (!string.IsNullOrEmpty(session.Title) && session.Title != "-")
                    text += $"\n💡 <i>Поточна:</i> <code>{TextUtils.EscapeHtml(session.Title)}</code> <i>(натисніть, щоб скопіювати)</i>";

                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookTitle;

                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад (Скасувати)", "wizard_cancel"),
                    InlineKeyboardButton.WithCallbackData("Вперед ➡️", "wizard_next")
                });
            }
            else if (session.CurrentStep == 2)
            {
                text += "👉 <b>Введіть АВТОРА книги текстовим повідомленням:</b>";

                // ДОДАНО: Копіювання по кліку
                if (!string.IsNullOrEmpty(session.Author) && session.Author != "-")
                    text += $"\n💡 <i>Поточний:</i> <code>{TextUtils.EscapeHtml(session.Author)}</code> <i>(натисніть, щоб скопіювати)</i>";

                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookAuthor;

                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⏭ Пропустити етап", "wizard_skip") });
                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "wizard_prev"),
                    InlineKeyboardButton.WithCallbackData("Вперед ➡️", "wizard_next")
                });
            }
            else if (session.CurrentStep == 3)
            {
                text += "👉 <b>Введіть ЖАНР книги текстовим повідомленням:</b>";

                // ДОДАНО: Копіювання по кліку
                if (!string.IsNullOrEmpty(session.Genre) && session.Genre != "-")
                    text += $"\n💡 <i>Поточний:</i> <code>{TextUtils.EscapeHtml(session.Genre)}</code> <i>(натисніть, щоб скопіювати)</i>";

                SessionManager.UserStates[chatId] = UserState.WaitingForAddBookGenre;

                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⏭ Пропустити етап", "wizard_skip") });
                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "wizard_prev"),
                    InlineKeyboardButton.WithCallbackData("Вперед ➡️", "wizard_next")
                });
            }
            else if (session.CurrentStep == 4)
            {
                text += "👉 <b>Встановіть КІЛЬКІСТЬ примірників:</b>\n<i>(Використовуйте кнопки або натисніть 'Ввести' щоб написати вручну)</i>";

                if (SessionManager.UserStates[chatId] == UserState.WaitingForAddBookQuantity)
                    text += "\n\n⌨️ <i>Чекаю на введення числа в чат...</i>";
                else
                    SessionManager.UserStates[chatId] = UserState.None;

                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("➖", "wizard_qty_minus"),
                    InlineKeyboardButton.WithCallbackData($"✍️ Ввести: {session.CurrentAvailable}", "wizard_qty_manual"),
                    InlineKeyboardButton.WithCallbackData("➕", "wizard_qty_plus")
                });
                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "wizard_prev"),
                    InlineKeyboardButton.WithCallbackData("Вперед ➡️", "wizard_next")
                });
            }
            else if (session.CurrentStep == 5)
            {
                text += "👉 <b>Чи доступна книга для ОБМІНУ (Буккросингу)?</b>";
                SessionManager.UserStates[chatId] = UserState.None;

                string toggleText = session.ExchangeStatus == "Так" ? "✅ Так (Змінити на Ні)" : "❌ Ні (Змінити на Так)";
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(toggleText, "wizard_exch_toggle") });
                buttons.Add(new[] {
                    InlineKeyboardButton.WithCallbackData("⬅️ Назад", "wizard_prev"),
                    InlineKeyboardButton.WithCallbackData("💾 ЗБЕРЕГТИ", "wizard_save")
                });
            }

            var keyboard = new InlineKeyboardMarkup(buttons);

            if (existingMsgId.HasValue && existingMsgId.Value > 0)
            {
                try { await botClient.EditMessageText(chatId, existingMsgId.Value, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct); }
                catch { }
            }
            else
            {
                var msg = await botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
                session.WizardMessageId = msg.MessageId;
            }
        }
    }
}