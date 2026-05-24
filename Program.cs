using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using DotNetEnv;
using LibraryBot.Handlers;
using LibraryBot.Services;

namespace LibraryBot
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Env.Load();
            SessionManager.LoadAdminsFromEnv();
            string botToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? "";

            GoogleSheetsService.Initialize();

            var botClient = new TelegramBotClient(botToken);
            using var cts = new CancellationTokenSource();

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            botClient.StartReceiving(
                UpdateHandler.HandleUpdateAsync,
                UpdateHandler.HandleErrorAsync,
                receiverOptions,
                cancellationToken: cts.Token
            );

            DailyReminderService.Start(botClient, cts.Token);
            var me = await botClient.GetMe();
            Console.WriteLine($"BOT @{me.Username} SUCCESFULY LAUNCH!");
            Console.WriteLine("ENTER TO STOP...");
            Console.ReadLine();

            cts.Cancel();
        }
    }
}