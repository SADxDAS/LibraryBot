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
using static LibraryBot.Commands.ManualReturnCommand;

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
            new ManualReturnCommand(),
            new MyProfileCommand(),
            new AdminStatsCommand(),
            new AdminBorrowingsListCommand()
        };

        public static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            long? userId = update.CallbackQuery?.Message?.Chat.Id ?? update.Message?.Chat.Id;

            // АНТИ-ФЛУД: відсікаємо спам ДО будь-якої роботи з БД чи блокувань (найдешевший шлях).
            if (userId is long id)
            {
                var decision = RateLimiter.Check(id, SessionManager.AdminIds.Contains(id));
                switch (decision)
                {
                    case RateDecision.FirstThrottle:
                        // Попереджаємо ОДИН раз, далі — тиша.
                        try { await botClient.SendMessage(id, "⏳ Занадто багато запитів. Будь ласка, зачекайте кілька секунд.", cancellationToken: cancellationToken); }
                        catch { /* користувач міг заблокувати бота — ігноруємо */ }
                        return;
                    case RateDecision.Throttled:
                    case RateDecision.GlobalOverload:
                        return; // тихо відкидаємо, щоб не підсилювати атаку
                }
            }

            // КОАЛЕСИНГ (single-flight): якщо такий самий запит від цього користувача вже
            // обробляється (або щойно оброблений), цей — дублікат, відкидаємо. Багато
            // однакових запитів за секунду → лише ОДНА відповідь.
            string? dedupKey = BuildDedupKey(update, userId);
            if (dedupKey != null && !RequestCoalescer.TryEnter(dedupKey))
                return;

            try
            {
                // Серіалізуємо обробку в межах ОДНОГО користувача: його повідомлення/натискання
                // оброблюються по черзі, тож швидкі подвійні натискання не псують його стан сесії.
                // РІЗНІ користувачі мають різні ключі → працюють паралельно, у різних потоках.
                if (userId is long uid)
                {
                    using (await AsyncKeyedLock.LockAsync($"user:{uid}", cancellationToken))
                    {
                        await RouteUpdateAsync(botClient, update, cancellationToken);
                    }
                }
                else
                {
                    await RouteUpdateAsync(botClient, update, cancellationToken);
                }
            }
            finally
            {
                if (dedupKey != null) RequestCoalescer.Exit(dedupKey);
            }
        }

        /// <summary>
        /// Ключ для коалесингу = користувач + зміст запиту. Однакові натискання/повідомлення
        /// дають однаковий ключ і колапсують в одну обробку. null — коалесинг не застосовуємо.
        /// </summary>
        private static string? BuildDedupKey(Update update, long? userId)
        {
            if (userId is not long uid) return null;

            if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data is { } data)
                return $"cb:{uid}:{data}";

            if (update.Message?.Text is { } txt && !string.IsNullOrWhiteSpace(txt))
                return $"msg:{uid}:{txt.Trim()}";

            return null;
        }

        private static async Task RouteUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
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

            // 1. ПЕРЕВІРКА: Чи активний зараз "Майстер" (Wizard)?
            bool isWizardActive = SessionManager.AdminBookSessions.ContainsKey(chatId)
                                  && SessionManager.AdminBookSessions[chatId].CurrentStep > 0;

            if (isWizardActive)
            {
                // Дозволяємо скасування
                if (messageText == "/cancel" || messageText == "❌ Скасувати дію")
                {
                    await AdminStateHandler.HandleAsync(botClient, chatId, messageText, UserState.None, cancellationToken);
                    return;
                }

                var isCommand = _commands.Any(c => c.Triggers.Contains(messageText));

                if (!isCommand)
                {
                    // Намагаємося згодувати текст Майстру (наприклад, введення назви чи автора)
                    bool handled = await AdminStateHandler.HandleAsync(botClient, chatId, messageText, SessionManager.UserStates.GetValueOrDefault(chatId, UserState.None), cancellationToken);

                    if (!handled)
                    {
                        // Якщо Майстру зараз текст НЕ потрібен (наприклад, він чекає натискання інлайн-кнопки кількості)
                        // ми просто мовчки видаляємо випадкове повідомлення адміна, щоб воно не заважало.
                        try { await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }
                    }

                    // 🛑 ГОЛОВНЕ: БЛОКУЄМО ВИКОНАННЯ. Текст ніколи не дійде до автопошуку.
                    return;
                }
                else
                {
                    // Якщо адмін ввів іншу команду (наприклад, /start) під час роботи майстра - блокуємо
                    var warningMsg = await botClient.SendMessage(chatId, "⚠️ <b>Ви зараз у режимі додавання/редагування книги.</b>\nЗавершіть процес кнопками або натисніть «❌ Скасувати дію».",
                        parseMode: ParseMode.Html, cancellationToken: cancellationToken);

                    try { await botClient.DeleteMessage(chatId, message.MessageId, cancellationToken); } catch { }

                    // Видаляємо попередження через 4 секунди, щоб не засмічувати чат
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(4000);
                        try { await botClient.DeleteMessage(chatId, warningMsg.MessageId, CancellationToken.None); } catch { }
                    });

                    return; // 🛑 БЛОКУЄМО ВИКОНАННЯ
                }
            }

            // ====================================================================
            // СТАНДАРТНА ЛОГІКА БОТА (Виконується ТІЛЬКИ якщо Майстер НЕ активний)
            // ====================================================================

            // 2. ПЕРЕВІРЯЄМО, ЧИ ЦЕ ТЕКСТ КОМАНДИ МЕНЮ
            var command = _commands.FirstOrDefault(c => c.Triggers.Contains(messageText));

            if (command != null)
            {
                await command.ExecuteAsync(botClient, message, cancellationToken);
                return;
            }

            // 3. НАПРАВЛЯЄМО ТЕКСТ У СТЕЙТ-МАШИНУ (якщо є активний стан)
            var currentState = SessionManager.UserStates.GetValueOrDefault(chatId, UserState.None);

            if (currentState != UserState.None && SessionManager.AdminIds.Contains(chatId))
            {
                if (await AdminStateHandler.HandleAsync(botClient, chatId, messageText, currentState, cancellationToken)) return;
            }

            if (currentState != UserState.None)
            {
                if (await UserStateHandler.HandleAsync(botClient, message, currentState, cancellationToken)) return;
            }

            // 4. ДІЯ ЗА ЗАМОВЧУВАННЯМ: АВТОМАТИЧНИЙ ПОШУК
            if (!string.IsNullOrEmpty(messageText))
            {
                await LibraryDisplayService.SearchBooksAsync(botClient, chatId, messageText, cancellationToken);
            }
        }
        public static Task HandleErrorAsync(ITelegramBotClient botClient, System.Exception exception, CancellationToken cancellationToken)
        {
            System.Console.WriteLine($"Помилка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }
    }
}