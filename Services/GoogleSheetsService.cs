using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LibraryBot.Data;
using LibraryBot.Models;

namespace LibraryBot.Services
{
    public class GoogleSheetsService
    {
        public const int COL_CATALOG_TITLE = 0;
        public const int COL_CATALOG_AUTHOR = 1;
        public const int COL_CATALOG_GENRE = 2;
        public const int COL_CATALOG_EXCHANGE = 3;
        public const int COL_CATALOG_AVAILABLE = 4;
        public const int COL_CATALOG_TOTAL = 5;
        public const int COL_CATALOG_ID = 6;

        public static void Initialize()
        {
            using var db = new AppDbContext();
            try
            {
                db.Database.CanConnect();
                Console.WriteLine("✅ Database connected successfully! (PostgreSQL Railway)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ DB Error: {ex.Message}");
            }
        }

        public static async Task<IList<IList<object>>?> GetBooksAsync()
        {
            using var db = new AppDbContext();
            var books = await db.Books.AsNoTracking().OrderBy(b => b.Id).ToListAsync();
            var result = new List<IList<object>>();
            foreach (var b in books)
            {
                result.Add(new List<object> {
                    b.Title, b.Author, b.Genre, b.ExchangeStatus, b.AvailableCount, b.TotalCount, b.Id
                });
            }
            return result;
        }

        public static async Task AddBorrowingAsync(string bookTitle, string realName, string telegramName, string contact, long chatId, DateTime dueDate)
        {
            using var db = new AppDbContext();
            // Шукаємо ID книги за її назвою (або можна було б передавати одразу ID)
            var book = await db.Books.FirstOrDefaultAsync(b => b.Title.ToLower() == bookTitle.ToLower());
            if (book == null) return;

            var borrow = new DbBorrowing
            {
                BookId = book.Id, // Використовуємо зовнішній ключ!
                RealName = realName,
                TelegramName = telegramName,
                Contact = contact,
                ChatId = chatId,
                DueDate = dueDate,
                IssueDate = DateTime.Now,
                IsExtended = false
            };
            db.Borrowings.Add(borrow);
            await db.SaveChangesAsync();
        }

        public static async Task<bool> TryDecrementAvailableAsync(int bookId)
        {
            using var db = new AppDbContext();
            var book = await db.Books.FindAsync(bookId);
            if (book == null || book.AvailableCount <= 0) return false;
            book.AvailableCount -= 1;
            await db.SaveChangesAsync();
            return true;
        }

        public static async Task<bool> ChangeAvailableCountAsync(int bookId, int delta)
        {
            using var db = new AppDbContext();
            var book = await db.Books.FindAsync(bookId);
            if (book != null)
            {
                book.AvailableCount = Math.Max(0, book.AvailableCount + delta);
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task LogReturnDateAsync(string bookTitle, long? chatId = null)
        {
            using var db = new AppDbContext();
            // Додаємо .Include(b => b.Book) щоб дістати Title через зв'язок
            var query = db.Borrowings.Include(b => b.Book).Where(b => b.Book.Title.ToLower() == bookTitle.ToLower() && b.ReturnDate == null);
            if (chatId.HasValue)
            {
                query = query.Where(b => b.ChatId == chatId.Value);
            }
            var borrow = await query.FirstOrDefaultAsync();
            if (borrow != null)
            {
                borrow.ReturnDate = DateTime.Now;
                await db.SaveChangesAsync();
            }
        }

        public static async Task<int> CountActiveBorrowingsAsync(string bookTitle)
        {
            using var db = new AppDbContext();
            return await db.Borrowings.Include(b => b.Book)
                .CountAsync(b => b.Book.Title.ToLower() == bookTitle.ToLower() && b.ReturnDate == null);
        }

        public static async Task<List<(string Title, int BookId)>> GetUserBorrowedBooksAsync(long chatId)
        {
            using var db = new AppDbContext();
            var borrows = await db.Borrowings.Include(b => b.Book).AsNoTracking()
                .Where(b => b.ChatId == chatId && b.ReturnDate == null)
                .ToListAsync();

            var result = new List<(string Title, int BookId)>();
            foreach (var b in borrows)
            {
                if (b.Book != null)
                {
                    result.Add((b.Book.Title, b.BookId));
                }
            }
            return result;
        }

        public static async Task<bool> AddBookToCatalogAsync(string title, string author, string genre, string exchangeStatus, int quantity = 1)
        {
            using var db = new AppDbContext();
            db.Books.Add(new DbBook
            {
                Title = title,
                Author = author,
                Genre = genre,
                ExchangeStatus = exchangeStatus,
                AvailableCount = quantity,
                TotalCount = quantity
            });
            await db.SaveChangesAsync();
            return true;
        }

        public static async Task<bool> UpdateBookInCatalogAsync(int bookId, string title, string author, string genre, string exchangeStatus, int available, int total)
        {
            using var db = new AppDbContext();
            var book = await db.Books.FindAsync(bookId);
            if (book != null)
            {
                book.Title = title;
                book.Author = author;
                book.Genre = genre;
                book.ExchangeStatus = exchangeStatus;
                book.AvailableCount = available;
                book.TotalCount = total;
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task<bool> DeleteBookByIdAsync(int bookId)
        {
            using var db = new AppDbContext();
            var book = await db.Books.FindAsync(bookId);
            if (book != null)
            {
                book.TotalCount = 0;
                book.AvailableCount = 0;
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task<bool> AddExchangeLogAsync(string oldTitle, string newTitle, string telegramName)
        {
            using var db = new AppDbContext();
            db.ExchangeLogs.Add(new DbExchangeLog
            {
                OldBookTitle = oldTitle,
                NewBookTitle = newTitle,
                TelegramName = telegramName,
                ExchangeDate = DateTime.Now
            });
            await db.SaveChangesAsync();
            return true;
        }

        public static async Task<IList<IList<object>>?> GetAllBorrowingsAsync()
        {
            using var db = new AppDbContext();
            var borrows = await db.Borrowings.Include(b => b.Book).AsNoTracking().OrderBy(b => b.Id).ToListAsync();
            var result = new List<IList<object>>();
            foreach (var b in borrows)
            {
                result.Add(new List<object> {
                    b.Book?.Title ?? "Невідома книга",
                    b.RealName,
                    b.TelegramName,
                    b.Contact,
                    b.IssueDate.ToString("dd.MM.yyyy HH:mm"),
                    b.ReturnDate.HasValue ? b.ReturnDate.Value.ToString("dd.MM.yyyy HH:mm") : "",
                    b.ChatId.ToString(),
                    b.DueDate.ToString("dd.MM.yyyy"),
                    b.IsExtended ? "Так" : "Ні"
                });
            }
            return result;
        }

        public static async Task<bool> ExtendBorrowingAsync(int rowIndex)
        {
            using var db = new AppDbContext();
            int index = rowIndex - 2;
            if (index < 0) return false;
            var borrow = await db.Borrowings.OrderBy(b => b.Id).Skip(index).Take(1).FirstOrDefaultAsync();
            if (borrow != null)
            {
                borrow.DueDate = borrow.DueDate.AddDays(30);
                borrow.IsExtended = true;
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task<bool> ProcessExchangeOutgoingBookAsync(int bookId, string title)
        {
            using var db = new AppDbContext();
            var book = await db.Books.FindAsync(bookId);
            if (book != null)
            {
                book.AvailableCount = Math.Max(0, book.AvailableCount - 1);
                book.TotalCount = Math.Max(0, book.TotalCount - 1);
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task AddPendingRequestAsync(Models.PendingRequest req)
        {
            using var db = new AppDbContext();
            db.PendingRequests.Add(new DbPendingRequest
            {
                RequestId = req.RequestId,
                Type = req.Type.ToString(),
                UserId = req.UserId,
                UserName = req.UserName,
                RealName = req.RealName,
                Contact = req.Contact ?? "-",
                BookId = req.CatalogRowIndex > 0 ? req.CatalogRowIndex : null, // Використовуємо індекс як ID книги
                CatalogRowIndex = req.CatalogRowIndex,
                BorrowDays = req.BorrowDays,
                NewBookTitle = req.NewBookTitle,
                NewBookAuthor = req.NewBookAuthor,
                NewBookGenre = req.NewBookGenre
            });
            await db.SaveChangesAsync();
        }

        public static async Task<Models.PendingRequest?> GetPendingRequestAsync(string requestId)
        {
            using var db = new AppDbContext();
            var req = await db.PendingRequests.Include(r => r.Book).AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId);
            if (req != null)
            {
                Enum.TryParse(req.Type, out Models.RequestType type);
                return new Models.PendingRequest
                {
                    RequestId = req.RequestId,
                    Type = type,
                    UserId = req.UserId,
                    UserName = req.UserName,
                    RealName = req.RealName,
                    Contact = req.Contact,
                    BookTitle = req.Book?.Title ?? "Невідома книга", // Дістаємо актуальну назву через зв'язок
                    CatalogRowIndex = req.CatalogRowIndex,
                    BorrowDays = req.BorrowDays,
                    NewBookTitle = req.NewBookTitle,
                    NewBookAuthor = req.NewBookAuthor,
                    NewBookGenre = req.NewBookGenre
                };
            }
            return null;
        }

        public static async Task<bool> DeletePendingRequestAsync(string requestId)
        {
            using var db = new AppDbContext();
            var req = await db.PendingRequests.FirstOrDefaultAsync(r => r.RequestId == requestId);
            if (req != null)
            {
                db.PendingRequests.Remove(req);
                await db.SaveChangesAsync();
                return true;
            }
            return false;
        }

        public static async Task<(int CurrentlyReadingCount, int ReadCount, List<string> ReadBooks, List<string> CurrentBooks)> GetUserProfileAsync(long chatId)
        {
            using var db = new AppDbContext();
            var userBorrows = await db.Borrowings.Include(b => b.Book).AsNoTracking().Where(b => b.ChatId == chatId).ToListAsync();

            int currentCount = 0;
            int readCount = 0;
            var currentBooks = new List<string>();
            var readBooks = new List<string>();

            foreach (var b in userBorrows)
            {
                string title = b.Book?.Title ?? "Невідома книга";
                if (b.ReturnDate == null)
                {
                    currentCount++;
                    currentBooks.Add(title);
                }
                else
                {
                    readCount++;
                    readBooks.Add(title);
                }
            }

            return (currentCount, readCount, readBooks, currentBooks);
        }

        public static async Task<(int TotalBooks, int AvailableBooks, int BorrowedBooks, int OverdueBooks, int PendingRequests)> GetLibraryStatisticsAsync()
        {
            using var db = new AppDbContext();
            int total = await db.Books.SumAsync(b => b.TotalCount);
            int available = await db.Books.SumAsync(b => b.AvailableCount);

            var activeBorrows = await db.Borrowings.AsNoTracking().Where(b => b.ReturnDate == null).ToListAsync();
            int borrowed = activeBorrows.Count;
            int overdue = activeBorrows.Count(b => b.DueDate.Date < DateTime.Now.Date);

            int pending = await db.PendingRequests.CountAsync();

            return (total, available, borrowed, overdue, pending);
        }

        public static async Task<List<(string Title, string Name, string Contact, string DueDate)>> GetOverdueBorrowingsAsync()
        {
            using var db = new AppDbContext();
            var overdues = await db.Borrowings.Include(b => b.Book).AsNoTracking()
                .Where(b => b.ReturnDate == null && b.DueDate.Date < DateTime.Now.Date)
                .ToListAsync();

            var result = new List<(string Title, string Name, string Contact, string DueDate)>();
            foreach (var b in overdues)
            {
                string fullName = string.IsNullOrWhiteSpace(b.RealName) || b.RealName == "Без імені" ? b.TelegramName : $"{b.RealName} ({b.TelegramName})";
                result.Add((b.Book?.Title ?? "Невідома книга", fullName, b.Contact, b.DueDate.ToString("dd.MM.yyyy")));
            }
            return result;
        }

        public static async Task<List<(string Title, string Name, string Contact, string DueDate)>> GetActiveBorrowingsAsync()
        {
            using var db = new AppDbContext();
            var active = await db.Borrowings.Include(b => b.Book).AsNoTracking()
                .Where(b => b.ReturnDate == null)
                .ToListAsync();

            var result = new List<(string Title, string Name, string Contact, string DueDate)>();
            foreach (var b in active)
            {
                string fullName = string.IsNullOrWhiteSpace(b.RealName) || b.RealName == "Без імені" ? b.TelegramName : $"{b.RealName} ({b.TelegramName})";
                result.Add((b.Book?.Title ?? "Невідома книга", fullName, b.Contact, b.DueDate.ToString("dd.MM.yyyy")));
            }
            return result;
        }
    }
}