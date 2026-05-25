namespace LibraryBot.Models
{
    public enum UserState
    {
        None,
        WaitingForManualBookId,
        WaitingForManualReaderName,
        WaitingForManualReaderContact,
        WaitingForPageNumber,
        WaitingForBorrowBookTitle,
        WaitingForBorrowContact,
        WaitingForReturnBookTitle,
        WaitingForSearchQuery,
        WaitingForAddBookTitle,
        WaitingForAddBookAuthor,
        WaitingForAddBookGenre,
        WaitingForDeleteSearchQuery,
        WaitingForEditSearchQuery,
        WaitingForEditBookTitle,
        WaitingForEditBookAuthor,
        WaitingForEditBookGenre,
        WaitingForExchangeSearchQuery,
        WaitingForExchangeReaderName,
        WaitingForExchangeNewTitle,
        WaitingForExchangeNewAuthor,
        WaitingForExchangeNewGenre,
        WaitingForManualSearchQuery,
        WaitingForManualReturnSearchQuery,
        WaitingForAddBookExchangeStatus, // Новий
        WaitingForEditBookExchangeStatus,
        WaitingForUserExchangeTitle,
        WaitingForUserExchangeAuthor,
        WaitingForUserExchangeGenre,
        WaitingForBorrowPeriod,
        WaitingForBorrowRealName,        // НОВЕ: Очікуємо справжнє ім'я
        WaitingForBorrowContactMethod,   // НОВЕ: Очікуємо натискання кнопки зв'язку
        WaitingForBorrowContactInput,
        WaitingForAddBookQuantity,
        WaitingForExchangeExchangeStatus,
        WaitingForEditBookQuantity,
        WaitingForUserExchangeSearchQuery,
    }

    public class ManualBorrowingSession
    {
        public string? BookId { get; set; }
        public string? ReaderName { get; set; }
        public string? ReaderContact { get; set; }
        public int CatalogRowIndex { get; set; } // <--- ДОДАЙ ЦЕ

    }

    public class UserBorrowingSession
    {
        public string? BookTitle { get; set; }
        public string? RealName { get; set; }        // НОВЕ
        public string? ContactMethod { get; set; }   // НОВЕ
        public string? Contact { get; set; }         // НОВЕ
        public int CatalogRowIndex { get; set; }
    }

    public enum RequestType
    {
        Borrow,
        Return,
        UserExchange
    }

    public class PendingRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
        public RequestType Type { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; } = "";
        public string RealName { get; set; } = "";
        public string? Contact { get; set; }

        // Поля для книги з бібліотеки, яку користувач ХОЧЕ ЗАБРАТИ:
        public string BookTitle { get; set; } = "";
        public int CatalogRowIndex { get; set; }
        public int BorrowDays { get; set; }

        // Поля для нової книги, яку користувач ПРИНІС:
        public string NewBookTitle { get; set; } = "-";
        public string NewBookAuthor { get; set; } = "-";
        public string NewBookGenre { get; set; } = "-";
    }
    public class AdminBookSession
    {
        public int EditRowIndex { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Genre { get; set; }
        public string? Status { get; set; } // "Доступна" або "Читають"
        public string? ExchangeStatus { get; set; } // Нове поле: "Так" або "Ні"
        public int Quantity { get; set; }
        public int CurrentAvailable { get; set; } // <--- ДОДАЙ ЦЕ
        public int CurrentTotal { get; set; }
    }
    public class AdminExchangeSession
    {
        public int OldBookRowIndex { get; set; }
        public string OldBookTitle { get; set; } = "";
        public string ReaderName { get; set; } = "";
        public string NewBookTitle { get; set; } = "";
        public string NewBookAuthor { get; set; } = "";
        public string NewBookGenre { get; set; } = "";
        public string NewBookExchangeStatus { get; set; } = "Так";
    }
    public class UserExchangeSession
    {
        // Книга, яку принесли:
        public string Title { get; set; } = "";
        public string Author { get; set; } = "-";
        public string Genre { get; set; } = "-";

        // Книга, яку забирають натомість:
        public string LibraryBookTitle { get; set; } = "";
        public int LibraryBookRowIndex { get; set; }
    }
}