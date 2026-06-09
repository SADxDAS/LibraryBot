using System;
using System.ComponentModel.DataAnnotations;

namespace LibraryBot.Data
{
    // Таблиця "Каталог"
    public class DbBook
    {
        [Key]
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string ExchangeStatus { get; set; } = "Так";
        public int AvailableCount { get; set; }
        public int TotalCount { get; set; }
    }

    // Таблиця "Видачі"
    public class DbBorrowing
    {
        [Key]
        public int Id { get; set; }
        public string BookTitle { get; set; } = string.Empty;
        public string RealName { get; set; } = string.Empty;
        public string TelegramName { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;
        public DateTime IssueDate { get; set; }
        public DateTime? ReturnDate { get; set; }
        public long ChatId { get; set; }
        public DateTime DueDate { get; set; }
        public bool IsExtended { get; set; }
    }

    // Таблиця "Запити"
    public class DbPendingRequest
    {
        [Key]
        public string RequestId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "Borrow", "Return" або "UserExchange"
        public long UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string RealName { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;
        public string BookTitle { get; set; } = string.Empty;
        public int CatalogRowIndex { get; set; }
        public int BorrowDays { get; set; }
        public string NewBookTitle { get; set; } = "-";
        public string NewBookAuthor { get; set; } = "-";
        public string NewBookGenre { get; set; } = "-";
    }

    // Таблиця "Обмін" (Логи)
    public class DbExchangeLog
    {
        [Key]
        public int Id { get; set; }
        public string OldBookTitle { get; set; } = string.Empty;
        public string NewBookTitle { get; set; } = string.Empty;
        public string TelegramName { get; set; } = string.Empty;
        public DateTime ExchangeDate { get; set; }
    }
}