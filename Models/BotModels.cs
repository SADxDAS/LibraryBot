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
        WaitingForEditBookExchangeStatus // Новий
    }

    public class ManualBorrowingSession
    {
        public string? BookId { get; set; }
        public string? ReaderName { get; set; }
        public string? ReaderContact { get; set; }
    }

    public class UserBorrowingSession
    {
        public string? BookTitle { get; set; }
    }

    public enum RequestType
    {
        Borrow,
        Return
    }

    public class PendingRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8); // Коротка унікальна ID
        public RequestType Type { get; set; }
        public long UserId { get; set; }
        public string UserName { get; set; } = "";
        public string? Contact { get; set; }
        public string BookTitle { get; set; } = "";
        public int CatalogRowIndex { get; set; }
    }
    public class AdminBookSession
    {
        public int EditRowIndex { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Genre { get; set; }
        public string? Status { get; set; } // "Доступна" або "Читають"
        public string? ExchangeStatus { get; set; } // Нове поле: "Так" або "Ні"
    }
    public class AdminExchangeSession
    {
        public int OldBookRowIndex { get; set; }
        public string OldBookTitle { get; set; } = "";
        public string ReaderName { get; set; } = "";
        public string NewBookTitle { get; set; } = "";
        public string NewBookAuthor { get; set; } = "";
        public string NewBookGenre { get; set; } = "";
    }

}