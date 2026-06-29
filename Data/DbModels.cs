using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

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

        // Навігаційні властивості для зв'язків (зворотний бік зв'язку)
        public virtual ICollection<DbBorrowing> Borrowings { get; set; } = new List<DbBorrowing>();
        public virtual ICollection<DbPendingRequest> PendingRequests { get; set; } = new List<DbPendingRequest>();
    }

    // Таблиця "Видачі"
    public class DbBorrowing
    {
        [Key]
        public int Id { get; set; }

        // Внешний ключ вместо строки BookTitle
        public int BookId { get; set; }

        [ForeignKey(nameof(BookId))]
        public virtual DbBook Book { get; set; } = null!;

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

        // Внешний ключ может быть Nullable, так как тип запроса "UserExchange" 
        // может предлагать абсолютно новую книгу, которой еще нет в каталоге
        public int? BookId { get; set; }

        [ForeignKey(nameof(BookId))]
        public virtual DbBook? Book { get; set; }

        public int CatalogRowIndex { get; set; }
        public int BorrowDays { get; set; }

        // Поля для книг, предлагаемых пользователями на обмен (остаются строками)
        public string NewBookTitle { get; set; } = "-";
        public string NewBookAuthor { get; set; } = "-";
        public string NewBookGenre { get; set; } = "-";
    }

    // Таблиця "Обмін" (Логи)
    public class DbExchangeLog
    {
        [Key]
        public int Id { get; set; }

        // В логах обмена текстовые названия лучше оставить как исторический снимок (Snapshot).
        // Если через год книга будет полностью удалена из каталога, запись в логах 
        // всё равно сохранит информацию о том, какое именно название фигурировало в обмене.
        public string OldBookTitle { get; set; } = string.Empty;
        public string NewBookTitle { get; set; } = string.Empty;
        public string TelegramName { get; set; } = string.Empty;
        public DateTime ExchangeDate { get; set; }
    }
    // Таблиця "Користувачі" (Профіль)
    public class DbUser
    {
        [Key]
        public long ChatId { get; set; } // Використовуємо Telegram ID як первинний ключ

        public string RealName { get; set; } = string.Empty;
        public string Contact { get; set; } = string.Empty;

        public DateTime RegistrationDate { get; set; } = DateTime.UtcNow;
    }
}