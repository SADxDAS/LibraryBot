using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace LibraryBot.Commands
{
    public interface ICommand
    {
        // Масив слів або команд, на які реагує цей клас (наприклад, "/start" або "📚 Каталог")
        string[] Triggers { get; }

        // Метод, який виконує саму логіку команди
        Task ExecuteAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken);
    }
}