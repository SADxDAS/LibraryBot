using System.Collections.Concurrent;
using LibraryBot.Models;
using System.Collections.Generic;
namespace LibraryBot.Services
{
    public static class SessionManager
    {
        public static readonly HashSet<long> AdminIds = new()
        {
            973920888,
            6009402319
        };

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
        }
    }
}