using System.Collections.Concurrent;
using LibraryBot.Models;
using System.Collections.Generic;
namespace LibraryBot.Services
{
    public static class SessionManager
    {
        public static readonly HashSet<long> AdminIds = new();
        // Додаємо метод для завантаження адмінів
        public static void LoadAdminsFromEnv()
        {
            string? adminIdsStr = Environment.GetEnvironmentVariable("ADMIN_IDS");
            if (!string.IsNullOrWhiteSpace(adminIdsStr))
            {
                var ids = adminIdsStr.Split(',');
                foreach (var idStr in ids)
                {
                    if (long.TryParse(idStr.Trim(), out long id))
                    {
                        AdminIds.Add(id);
                    }
                }
            }
        }
        public static readonly ConcurrentDictionary<long, UserExchangeSession> UserExchangeSessions = new();

        public static readonly ConcurrentDictionary<long, AdminBookSession> AdminBookSessions = new();
        public static readonly ConcurrentDictionary<string, PendingRequest> PendingRequests = new();
        public static readonly ConcurrentDictionary<long, UserState> UserStates = new();
        public static readonly ConcurrentDictionary<long, ManualBorrowingSession> AdminSessions = new();
        public static readonly ConcurrentDictionary<long, UserBorrowingSession> BorrowSessions = new();
        public static readonly ConcurrentDictionary<long, AdminExchangeSession> AdminExchangeSessions = new();
        // Зручний метод для повного очищення стану користувача
        public static void ClearSession(long chatId)
        {
            UserStates[chatId] = UserState.None;
            BorrowSessions.TryRemove(chatId, out _);
            AdminSessions.TryRemove(chatId, out _);
            AdminBookSessions.TryRemove(chatId, out _);
            AdminExchangeSessions.TryRemove(chatId, out _);
            UserExchangeSessions.TryRemove(chatId, out _); // <--- ДОДАНО
        }
    }
}