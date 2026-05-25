using LibraryBot.Commands; // Підключаємо папку з нашими командами
using LibraryBot.Models;
using LibraryBot.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace LibraryBot.Handlers
{
    public static class UpdateHandler
    {
        // РЕЄСТР КОМАНД: бот зберігає всі можливі дії в цьому масиві
        private static readonly List<ICommand> _commands = new List<ICommand>
        {
            new StartCommand(),
            new HelpCommand(),
            new CancelCommand(),
            new MyIdCommand(),
            new TestRemindersCommand(),
            new CatalogCommand(),
            new BorrowCommand(),
            new ReturnCommand(),
            new SearchCommand(),
            new UserExchangeCommand(),
            new AddBookCommand(),
            new DeleteBookCommand(),
            new EditBookCommand(),
            new AdminExchangeCommand(),
            new ManualBorrowCommand(),
            new ManualReturnCommand()
        };

        public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            // Обробка інлайн-кнопок
            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await CallbackHandler.HandleAsync(botClient, update.CallbackQuery, cancellationToken);
                return;
            }

            if (update.Message is not { } message) return;
            await HandleMessageAsync(botClient, message, cancellationToken);
        }

        private static async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
        {
            long chatId = message.Chat.Id;
            string messageText = message.Text?.Trim() ?? "";

            // 1. ПЕРЕВІРЯЄМО, ЧИ ЦЕ ТЕКСТ КОМАНДИ МЕНЮ
            // Шукаємо першу команду, в масиві `Triggers` якої є текст, що надіслав користувач
            var command = _commands.FirstOrDefault(c => c.Triggers.Contains(messageText));

            if (command != null)
            {
                // ЯКЩО ЗНАЙШЛИ: Команда сама очистить сесію та виконає свої дії.
                // Сюди код зайде і залізобетонно перерве будь-яку стару дію.
                await command.ExecuteAsync(botClient, message, cancellationToken);
                return;
            }

            // 2. ЯКЩО ЦЕ НЕ КНОПКА МЕНЮ — НАПРАВЛЯЄМО ТЕКСТ У СТЕЙТ-МАШИНУ
            var currentState = SessionManager.UserStates.GetValueOrDefault(chatId, UserState.None);

            if (currentState != UserState.None && SessionManager.AdminIds.Contains(chatId))
            {
                if (await AdminStateHandler.HandleAsync(botClient, chatId, messageText, currentState, cancellationToken)) return;
            }

            if (currentState != UserState.None)
            {
                if (await UserStateHandler.HandleAsync(botClient, message, currentState, cancellationToken)) return;
            }
        }

        public static Task HandleErrorAsync(ITelegramBotClient botClient, System.Exception exception, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"Помилка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}