using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Linq;

namespace LibraryBot.Services
{
    public class GoogleSheetsService
    {
        // ==========================================
        // КЕШУВАННЯ (Прискорення роботи бота)
        // ==========================================
        private static IList<IList<object>>? _booksCache = null;
        private static DateTime _cacheExpiration = DateTime.MinValue;

        // Метод для примусового очищення кешу (викликаємо, коли щось змінили в каталозі)
        public static void ClearCache()
        {
            _booksCache = null;
            Console.WriteLine("[Кеш] Кеш каталогу очищено.");
        }

        // ==========================================
        // КОНСТАНТИ КОЛОНОК (Позбавлення від "магічних чисел")
        // ==========================================

        // Аркуш "Каталог"
        public const int COL_CATALOG_TITLE = 0;
        public const int COL_CATALOG_AUTHOR = 1;
        public const int COL_CATALOG_GENRE = 2;
        public const int COL_CATALOG_EXCHANGE = 3;
        public const int COL_CATALOG_AVAILABLE = 4;
        public const int COL_CATALOG_TOTAL = 5;

        // Аркуш "Видачі"
        public const int COL_BORROW_TITLE = 0;
        public const int COL_BORROW_REALNAME = 1;
        public const int COL_BORROW_TGNAME = 2;
        public const int COL_BORROW_CONTACT = 3;
        public const int COL_BORROW_ISSUEDATE = 4;
        public const int COL_BORROW_RETURNDATE = 5;
        public const int COL_BORROW_CHATID = 6;
        public const int COL_BORROW_DUEDATE = 7;
        public const int COL_BORROW_EXTENDED = 8;

        // Аркуш "Запити"
        public const int COL_REQ_ID = 0;
        public const int COL_REQ_TYPE = 1;
        public const int COL_REQ_USERID = 2;
        public const int COL_REQ_USERNAME = 3;
        public const int COL_REQ_REALNAME = 4;
        public const int COL_REQ_CONTACT = 5;
        public const int COL_REQ_BOOKTITLE = 6;
        public const int COL_REQ_CATINDEX = 7;
        public const int COL_REQ_DAYS = 8;
        public const int COL_REQ_NEWTITLE = 9;
        public const int COL_REQ_NEWAUTHOR = 10;
        public const int COL_REQ_NEWGENRE = 11;

        // Хелпер: перетворює індекс колонки у літеру ('A', 'B' тощо)
        private static string GetCol(int colIndex) => ((char)('A' + colIndex)).ToString();

        // ==========================================

        static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
        static readonly string ApplicationName = "LibraryBot";
        public static string SpreadsheetId { get; private set; } = "";
        static SheetsService? _service;

        public static void Initialize()
        {
            SpreadsheetId = Environment.GetEnvironmentVariable("SPREADSHEET_ID")
                ?? throw new Exception("Помилка: SPREADSHEET_ID не знайдено в .env файлі!");

#pragma warning disable CS0618
            GoogleCredential credential = GoogleCredential.FromFile("credentials.json").CreateScoped(Scopes);
#pragma warning restore CS0618 

            _service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            Console.WriteLine("Google Sheets connected!");
        }

        public static async Task<IList<IList<object>>?> GetBooksAsync()
        {
            // 1. ПЕРЕВІРКА КЕШУ: Якщо дані є і кеш ще живий (менше 5 хв) - віддаємо миттєво!
            if (_booksCache != null && DateTime.Now < _cacheExpiration)
            {
                return _booksCache;
            }

            // 2. ЯКЩО КЕШУ НЕМАЄ: Йдемо в Google
            string range = $"Каталог!A2:{GetCol(COL_CATALOG_TOTAL)}";
            SpreadsheetsResource.ValuesResource.GetRequest request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);

            try
            {
                ValueRange response = await request.ExecuteAsync();

                // 3. ЗБЕРІГАЄМО В КЕШ на 5 хвилин
                _booksCache = response.Values;
                _cacheExpiration = DateTime.Now.AddMinutes(5);
                Console.WriteLine("[Кеш] Каталог завантажено з Google Sheets та закешовано.");

                return _booksCache;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sheets API] Помилка читання таблиці Каталогу: {ex.Message}");
                return null;
            }
        }

        public static async Task AddBorrowingAsync(string bookTitle, string realName, string telegramName, string contact, long chatId, DateTime dueDate)
        {
            string range = $"Видачі!A:{GetCol(COL_BORROW_EXTENDED)}";
            var valueRange = new ValueRange();

            var rowList = new object[COL_BORROW_EXTENDED + 1];
            rowList[COL_BORROW_TITLE] = bookTitle;
            rowList[COL_BORROW_REALNAME] = realName;
            rowList[COL_BORROW_TGNAME] = telegramName;
            rowList[COL_BORROW_CONTACT] = contact;
            rowList[COL_BORROW_ISSUEDATE] = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            rowList[COL_BORROW_RETURNDATE] = "";
            rowList[COL_BORROW_CHATID] = chatId.ToString();
            rowList[COL_BORROW_DUEDATE] = dueDate.ToString("dd.MM.yyyy");
            rowList[COL_BORROW_EXTENDED] = "";

            valueRange.Values = new List<IList<object>> { new List<object>(rowList) };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

            try { await appendRequest.ExecuteAsync(); }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка запису видачі: {ex.Message}"); }
        }

        public static async Task UpdateBookStatusAsync(string bookTitle, string newStatus)
        {
            int delta = newStatus.Equals("Доступна", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            var books = await GetBooksAsync();
            if (books == null) return;

            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].Count > COL_CATALOG_TITLE && books[i][COL_CATALOG_TITLE]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                {
                    await ChangeAvailableCountAsync(i + 2, delta);
                    break;
                }
            }
        }

        public static async Task<bool> ChangeAvailableCountAsync(int rowIndex, int delta)
        {
            string range = $"Каталог!{GetCol(COL_CATALOG_AVAILABLE)}{rowIndex}";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try
            {
                var response = await request.ExecuteAsync();
                int currentAvailable = 0;
                if (response.Values != null && response.Values.Count > 0)
                {
                    int.TryParse(response.Values[0][0]?.ToString(), out currentAvailable);
                }

                int newAvailable = Math.Max(0, currentAvailable + delta);

                var valueRange = new ValueRange();
                valueRange.Values = new List<IList<object>> { new List<object> { newAvailable } };

                var updateRequest = _service.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                ClearCache(); // ОЧИЩАЄМО КЕШ, бо кількість змінилась
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка зміни кількості доступних примірників: {ex.Message}"); return false; }
        }

        public static async Task<(bool exists, bool isAvailable)> CheckBookAvailabilityAsync(string bookTitle)
        {
            var books = await GetBooksAsync();
            if (books == null) return (false, false);

            foreach (var row in books)
            {
                if (row.Count > COL_CATALOG_TITLE && row[COL_CATALOG_TITLE]?.ToString()?.Equals(bookTitle.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                {
                    int available = row.Count > COL_CATALOG_AVAILABLE ? int.TryParse(row[COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0 : 0;
                    return (true, available > 0);
                }
            }
            return (false, false);
        }

        public static async Task LogReturnDateAsync(string bookTitle, long? chatId = null)
        {
            string range = $"Видачі!A2:{GetCol(COL_BORROW_CHATID)}";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);

            try
            {
                var response = await request.ExecuteAsync();
                var rows = response.Values;
                if (rows == null) return;

                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Count > COL_BORROW_TITLE && rows[i][COL_BORROW_TITLE]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        bool matchChatId = true;
                        if (chatId.HasValue)
                        {
                            string rowChatId = rows[i].Count > COL_BORROW_CHATID ? rows[i][COL_BORROW_CHATID]?.ToString() ?? "" : "";
                            if (rowChatId != chatId.Value.ToString()) matchChatId = false;
                        }

                        if (matchChatId && (rows[i].Count <= COL_BORROW_RETURNDATE || string.IsNullOrWhiteSpace(rows[i][COL_BORROW_RETURNDATE]?.ToString())))
                        {
                            int rowIndex = i + 2;
                            string updateRange = $"Видачі!{GetCol(COL_BORROW_RETURNDATE)}{rowIndex}";

                            var valueRange = new ValueRange();
                            valueRange.Values = new List<IList<object>> { new List<object> { DateTime.Now.ToString("dd.MM.yyyy HH:mm") } };

                            var updateRequest = _service.Spreadsheets.Values.Update(valueRange, SpreadsheetId, updateRange);
                            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                            await updateRequest.ExecuteAsync();
                            break;
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка логування дати повернення: {ex.Message}"); }
        }

        public static async Task<List<(string Title, int CatalogRowIndex)>> GetUserBorrowedBooksAsync(string telegramName)
        {
            var result = new List<(string Title, int CatalogRowIndex)>();
            string rangeBorrow = $"Видачі!A2:{GetCol(COL_BORROW_RETURNDATE)}";
            var requestBorrow = _service!.Spreadsheets.Values.Get(SpreadsheetId, rangeBorrow);
            ValueRange responseBorrow;

            try { responseBorrow = await requestBorrow.ExecuteAsync(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[Sheets API] Помилка GetUserBorrowedBooksAsync: {ex.Message}");
                return result;
            }

            var borrowRows = responseBorrow.Values;
            if (borrowRows == null) return result;

            var catalogBooks = await GetBooksAsync();
            if (catalogBooks == null) return result;

            foreach (var row in borrowRows)
            {
                if (row.Count > COL_BORROW_TGNAME)
                {
                    string rowTitle = row[COL_BORROW_TITLE]?.ToString() ?? "";
                    string rowUser = row[COL_BORROW_TGNAME]?.ToString() ?? "";
                    string returnDate = row.Count > COL_BORROW_RETURNDATE ? row[COL_BORROW_RETURNDATE]?.ToString() ?? "" : "";

                    if (rowUser.Contains(telegramName, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(returnDate))
                    {
                        int catalogRowIndex = -1;
                        for (int i = 0; i < catalogBooks.Count; i++)
                        {
                            if (catalogBooks[i].Count > COL_CATALOG_TITLE && catalogBooks[i][COL_CATALOG_TITLE]?.ToString()?.Equals(rowTitle, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                catalogRowIndex = i + 2;
                                break;
                            }
                        }
                        if (catalogRowIndex != -1) result.Add((rowTitle, catalogRowIndex));
                    }
                }
            }
            return result;
        }

        public static async Task<bool> AddBookToCatalogAsync(string title, string author, string genre, string status, string exchangeStatus, int quantity = 1)
        {
            string range = $"Каталог!A:{GetCol(COL_CATALOG_TOTAL)}";
            var valueRange = new ValueRange();
            var rowList = new object[COL_CATALOG_TOTAL + 1];
            rowList[COL_CATALOG_TITLE] = title;
            rowList[COL_CATALOG_AUTHOR] = author;
            rowList[COL_CATALOG_GENRE] = genre;
            rowList[COL_CATALOG_EXCHANGE] = exchangeStatus;
            rowList[COL_CATALOG_AVAILABLE] = quantity;
            rowList[COL_CATALOG_TOTAL] = quantity;

            valueRange.Values = new List<IList<object>> { new List<object>(rowList) };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

            try
            {
                await appendRequest.ExecuteAsync();
                ClearCache(); // ОЧИЩАЄМО КЕШ
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка додавання книги до каталогу: {ex.Message}"); return false; }
        }

        public static async Task<bool> UpdateBookInCatalogAsync(int rowIndex, string title, string author, string genre, string exchangeStatus, int available, int total)
        {
            string range = $"Каталог!A{rowIndex}:{GetCol(COL_CATALOG_TOTAL)}{rowIndex}";
            var valueRange = new ValueRange();
            var rowList = new object[COL_CATALOG_TOTAL + 1];
            rowList[COL_CATALOG_TITLE] = title;
            rowList[COL_CATALOG_AUTHOR] = author;
            rowList[COL_CATALOG_GENRE] = genre;
            rowList[COL_CATALOG_EXCHANGE] = exchangeStatus;
            rowList[COL_CATALOG_AVAILABLE] = available;
            rowList[COL_CATALOG_TOTAL] = total;

            valueRange.Values = new List<IList<object>> { new List<object>(rowList) };

            var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            try
            {
                await updateRequest.ExecuteAsync();
                ClearCache(); // ОЧИЩАЄМО КЕШ
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка оновлення книги (рядок {rowIndex}): {ex.Message}"); return false; }
        }

        public static async Task<bool> DeleteBookFromCatalogAsync(string title)
        {
            var books = await GetBooksAsync();
            if (books == null) return false;

            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].Count > COL_CATALOG_TITLE && books[i][COL_CATALOG_TITLE]?.ToString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true)
                {
                    int rowIndex = i + 2;
                    try
                    {
                        var spreadsheet = await _service!.Spreadsheets.Get(SpreadsheetId).ExecuteAsync();
                        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Каталог");
                        if (sheet == null) return false;

                        int sheetId = sheet.Properties.SheetId ?? 0;

                        var deleteRequest = new Request
                        {
                            DeleteDimension = new DeleteDimensionRequest
                            {
                                Range = new DimensionRange
                                {
                                    SheetId = sheetId,
                                    Dimension = "ROWS",
                                    StartIndex = rowIndex - 1,
                                    EndIndex = rowIndex
                                }
                            }
                        };

                        var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
                        {
                            Requests = new List<Request> { deleteRequest }
                        };

                        await _service.Spreadsheets.BatchUpdate(batchUpdateRequest, SpreadsheetId).ExecuteAsync();
                        ClearCache(); // ОЧИЩАЄМО КЕШ
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Sheets API] Помилка повного видалення рядка: {ex.Message}");
                        return false;
                    }
                }
            }
            return false;
        }

        public static async Task<bool> AddExchangeLogAsync(string oldTitle, string newTitle, string telegramName)
        {
            string range = "Обмін!A:D";
            var valueRange = new ValueRange();
            var rowList = new List<object> { oldTitle, newTitle, telegramName, DateTime.Now.ToString("dd.MM.yyyy") };
            valueRange.Values = new List<IList<object>> { rowList };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

            try { await appendRequest.ExecuteAsync(); return true; }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка запису в лог обміну: {ex.Message}"); return false; }
        }

        public static async Task<IList<IList<object>>?> GetAllBorrowingsAsync()
        {
            string range = $"Видачі!A2:{GetCol(COL_BORROW_EXTENDED)}";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try { var response = await request.ExecuteAsync(); return response.Values; }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка читання видач: {ex.Message}"); return null; }
        }

        public static async Task<bool> ExtendBorrowingAsync(int rowIndex)
        {
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, $"Видачі!{GetCol(COL_BORROW_DUEDATE)}{rowIndex}");
            try
            {
                var response = await request.ExecuteAsync();
                if (response.Values == null || response.Values.Count == 0) return false;

                string currentDueStr = response.Values[0][0]?.ToString() ?? "";
                if (DateTime.TryParseExact(currentDueStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                {
                    DateTime newDueDate = dueDate.AddDays(30);
                    string updateRange = $"Видачі!{GetCol(COL_BORROW_DUEDATE)}{rowIndex}:{GetCol(COL_BORROW_EXTENDED)}{rowIndex}";
                    var valueRange = new ValueRange();
                    valueRange.Values = new List<IList<object>> { new List<object> { newDueDate.ToString("dd.MM.yyyy"), "Так" } };

                    var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, updateRange);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                    await updateRequest.ExecuteAsync();
                    return true;
                }
                return false;
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка подовження терміну: {ex.Message}"); return false; }
        }

        public static async Task<bool> ProcessExchangeOutgoingBookAsync(int rowIndex, string title)
        {
            var books = await GetBooksAsync();
            if (books == null || books.Count < rowIndex - 1) return false;

            var row = books[rowIndex - 2];
            if (row.Count > COL_CATALOG_TITLE && row[COL_CATALOG_TITLE]?.ToString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true)
            {
                int disponible = row.Count > COL_CATALOG_AVAILABLE ? int.TryParse(row[COL_CATALOG_AVAILABLE]?.ToString(), out int d) ? d : 0 : 0;
                int total = row.Count > COL_CATALOG_TOTAL ? int.TryParse(row[COL_CATALOG_TOTAL]?.ToString(), out int t) ? t : 1 : 1;

                if (total > 1)
                {
                    int newDisponible = Math.Max(0, disponible - 1);
                    int newTotal = total - 1;

                    string range = $"Каталог!{GetCol(COL_CATALOG_AVAILABLE)}{rowIndex}:{GetCol(COL_CATALOG_TOTAL)}{rowIndex}";
                    var valueRange = new ValueRange();
                    valueRange.Values = new List<IList<object>> { new List<object> { newDisponible, newTotal } };

                    var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                    try
                    {
                        await updateRequest.ExecuteAsync();
                        ClearCache(); // ОЧИЩАЄМО КЕШ
                        return true;
                    }
                    catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка списання примірника: {ex.Message}"); return false; }
                }
                else
                {
                    return await DeleteBookFromCatalogAsync(title); // Цей метод вже має ClearCache() всередині
                }
            }
            return false;
        }

        // ==========================================
        // БЛОК РОБОТИ ІЗ ЗАПИТАМИ КОРИСТУВАЧІВ
        // ==========================================

        public static async Task AddPendingRequestAsync(Models.PendingRequest req)
        {
            string range = $"Запити!A:{GetCol(COL_REQ_NEWGENRE)}";
            var valueRange = new ValueRange();
            var rowList = new object[COL_REQ_NEWGENRE + 1];
            rowList[COL_REQ_ID] = req.RequestId;
            rowList[COL_REQ_TYPE] = req.Type.ToString();
            rowList[COL_REQ_USERID] = req.UserId;
            rowList[COL_REQ_USERNAME] = req.UserName;
            rowList[COL_REQ_REALNAME] = req.RealName;
            rowList[COL_REQ_CONTACT] = req.Contact ?? "-";
            rowList[COL_REQ_BOOKTITLE] = req.BookTitle;
            rowList[COL_REQ_CATINDEX] = req.CatalogRowIndex;
            rowList[COL_REQ_DAYS] = req.BorrowDays;
            rowList[COL_REQ_NEWTITLE] = req.NewBookTitle;
            rowList[COL_REQ_NEWAUTHOR] = req.NewBookAuthor;
            rowList[COL_REQ_NEWGENRE] = req.NewBookGenre;

            valueRange.Values = new List<IList<object>> { new List<object>(rowList) };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
            try { await appendRequest.ExecuteAsync(); } catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка додавання запиту: {ex.Message}"); }
        }

        public static async Task<Models.PendingRequest?> GetPendingRequestAsync(string requestId)
        {
            string range = $"Запити!A2:{GetCol(COL_REQ_NEWGENRE)}";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try
            {
                var response = await request.ExecuteAsync();
                var rows = response.Values;
                if (rows == null) return null;

                foreach (var row in rows)
                {
                    if (row.Count > COL_REQ_ID && row[COL_REQ_ID]?.ToString() == requestId)
                    {
                        Enum.TryParse(row.Count > COL_REQ_TYPE ? row[COL_REQ_TYPE]?.ToString() : "Borrow", out Models.RequestType type);
                        long.TryParse(row.Count > COL_REQ_USERID ? row[COL_REQ_USERID]?.ToString() : "0", out long userId);
                        int.TryParse(row.Count > COL_REQ_CATINDEX ? row[COL_REQ_CATINDEX]?.ToString() : "0", out int catalogIndex);
                        int.TryParse(row.Count > COL_REQ_DAYS ? row[COL_REQ_DAYS]?.ToString() : "0", out int borrowDays);

                        return new Models.PendingRequest
                        {
                            RequestId = requestId,
                            Type = type,
                            UserId = userId,
                            UserName = row.Count > COL_REQ_USERNAME ? row[COL_REQ_USERNAME]?.ToString() ?? "" : "",
                            RealName = row.Count > COL_REQ_REALNAME ? row[COL_REQ_REALNAME]?.ToString() ?? "" : "",
                            Contact = row.Count > COL_REQ_CONTACT ? row[COL_REQ_CONTACT]?.ToString() : "-",
                            BookTitle = row.Count > COL_REQ_BOOKTITLE ? row[COL_REQ_BOOKTITLE]?.ToString() ?? "" : "",
                            CatalogRowIndex = catalogIndex,
                            BorrowDays = borrowDays,
                            NewBookTitle = row.Count > COL_REQ_NEWTITLE ? row[COL_REQ_NEWTITLE]?.ToString() ?? "-" : "-",
                            NewBookAuthor = row.Count > COL_REQ_NEWAUTHOR ? row[COL_REQ_NEWAUTHOR]?.ToString() ?? "-" : "-",
                            NewBookGenre = row.Count > COL_REQ_NEWGENRE ? row[COL_REQ_NEWGENRE]?.ToString() ?? "-" : "-"
                        };
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка читання запиту: {ex.Message}"); }
            return null;
        }

        public static async Task<bool> DeletePendingRequestAsync(string requestId)
        {
            string range = "Запити!A2:A";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try
            {
                var response = await request.ExecuteAsync();
                var rows = response.Values;
                if (rows == null) return false;

                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Count > COL_REQ_ID && rows[i][COL_REQ_ID]?.ToString() == requestId)
                    {
                        int rowIndex = i + 2;
                        var spreadsheet = await _service!.Spreadsheets.Get(SpreadsheetId).ExecuteAsync();
                        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Запити");
                        if (sheet == null) return false;

                        int sheetId = sheet.Properties.SheetId ?? 0;

                        var deleteRequest = new Request
                        {
                            DeleteDimension = new DeleteDimensionRequest
                            {
                                Range = new DimensionRange
                                {
                                    SheetId = sheetId,
                                    Dimension = "ROWS",
                                    StartIndex = rowIndex - 1,
                                    EndIndex = rowIndex
                                }
                            }
                        };

                        var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
                        {
                            Requests = new List<Request> { deleteRequest }
                        };

                        await _service.Spreadsheets.BatchUpdate(batchUpdateRequest, SpreadsheetId).ExecuteAsync();
                        return true;
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[Sheets API] Помилка видалення запиту: {ex.Message}"); }
            return false;
        }
        // Публічний метод для обчислення відстані Левенштейна
        public static int ComputeLevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return string.IsNullOrEmpty(t) ? 0 : t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;
            int[] v0 = new int[t.Length + 1];
            int[] v1 = new int[t.Length + 1];
            for (int i = 0; i < v0.Length; i++) v0[i] = i;
            for (int i = 0; i < s.Length; i++)
            {
                v1[0] = i + 1;
                for (int j = 0; j < t.Length; j++)
                {
                    int cost = (s[i] == t[j]) ? 0 : 1;
                    v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
                }
                for (int j = 0; j < v0.Length; j++) v0[j] = v1[j];
            }
            return v1[t.Length];
        }

        // НОВИЙ МЕТОД ДЛЯ ПРОФІЛЮ КОРИСТУВАЧА
        public static async Task<(int CurrentlyReadingCount, int ReadCount, List<string> ReadBooks, List<string> CurrentBooks)> GetUserProfileAsync(long chatId)
        {
            var borrowings = await GetAllBorrowingsAsync();
            int readCount = 0;
            int currentCount = 0;
            var readBooks = new List<string>();
            var currentBooks = new List<string>();

            if (borrowings == null) return (0, 0, readBooks, currentBooks);

            foreach (var row in borrowings)
            {
                if (row.Count > COL_BORROW_CHATID)
                {
                    string rowChatId = row[COL_BORROW_CHATID]?.ToString() ?? "";
                    if (rowChatId == chatId.ToString()) // Шукаємо всі записи цього користувача
                    {
                        string title = row[COL_BORROW_TITLE]?.ToString() ?? "Невідома книга";
                        string returnDate = row.Count > COL_BORROW_RETURNDATE ? row[COL_BORROW_RETURNDATE]?.ToString() ?? "" : "";

                        if (string.IsNullOrWhiteSpace(returnDate))
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
                }
            }
            return (currentCount, readCount, readBooks, currentBooks);
        }
        // НОВИЙ МЕТОД ДЛЯ СТАТИСТИКИ АДМІНА
        public static async Task<(int TotalBooks, int AvailableBooks, int BorrowedBooks, int OverdueBooks, int PendingRequests)> GetLibraryStatisticsAsync()
        {
            // 1. Рахуємо фонд та доступні книги (Каталог)
            var books = await GetBooksAsync();
            int total = 0, available = 0;
            if (books != null)
            {
                foreach (var row in books)
                {
                    if (row.Count > COL_CATALOG_TOTAL && int.TryParse(row[COL_CATALOG_TOTAL]?.ToString(), out int t)) total += t;
                    if (row.Count > COL_CATALOG_AVAILABLE && int.TryParse(row[COL_CATALOG_AVAILABLE]?.ToString(), out int a)) available += a;
                }
            }

            // 2. Рахуємо книги на руках та боржників (Видачі)
            var borrowings = await GetAllBorrowingsAsync();
            int overdue = 0, borrowed = 0;
            if (borrowings != null)
            {
                foreach (var row in borrowings)
                {
                    string returnDate = row.Count > COL_BORROW_RETURNDATE ? row[COL_BORROW_RETURNDATE]?.ToString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(returnDate)) // Якщо немає дати повернення - книга на руках
                    {
                        borrowed++;
                        string dueDateStr = row.Count > COL_BORROW_DUEDATE ? row[COL_BORROW_DUEDATE]?.ToString() ?? "" : "";

                        // Перевіряємо дедлайн (чи протерміновано)
                        if (DateTime.TryParseExact(dueDateStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                        {
                            if (dueDate.Date < DateTime.Now.Date)
                            {
                                overdue++;
                            }
                        }
                    }
                }
            }

            // 3. Рахуємо чергу запитів
            int pending = 0;
            try
            {
                var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, "Запити!A2:A");
                var response = await request.ExecuteAsync();
                if (response.Values != null) pending = response.Values.Count;
            }
            catch { }

            return (total, available, borrowed, overdue, pending);
        }
        // НОВИЙ МЕТОД: Отримання списку боржників
        public static async Task<List<(string Title, string Name, string Contact, string DueDate)>> GetOverdueBorrowingsAsync()
        {
            var result = new List<(string Title, string Name, string Contact, string DueDate)>();
            var borrowings = await GetAllBorrowingsAsync();
            if (borrowings == null || borrowings.Count <= 1) return result;

            foreach (var row in borrowings.Skip(1)) // Пропускаємо заголовок (1-й рядок)
            {
                string returnDate = row.Count > COL_BORROW_RETURNDATE ? row[COL_BORROW_RETURNDATE]?.ToString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(returnDate)) // Якщо книгу ще не повернули
                {
                    string dueDateStr = row.Count > COL_BORROW_DUEDATE ? row[COL_BORROW_DUEDATE]?.ToString() ?? "" : "";

                    if (DateTime.TryParseExact(dueDateStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                    {
                        if (dueDate.Date < DateTime.Now.Date) // Якщо дедлайн вже минув
                        {
                            string title = row.Count > COL_BORROW_TITLE ? row[COL_BORROW_TITLE]?.ToString() ?? "Невідомо" : "Невідомо";
                            string realName = row.Count > COL_BORROW_REALNAME ? row[COL_BORROW_REALNAME]?.ToString() ?? "" : "";
                            string tgName = row.Count > COL_BORROW_TGNAME ? row[COL_BORROW_TGNAME]?.ToString() ?? "" : "";
                            string contact = row.Count > COL_BORROW_CONTACT ? row[COL_BORROW_CONTACT]?.ToString() ?? "Не вказано" : "Не вказано";

                            // Формуємо красиве ім'я (Справжнє ім'я + Нікнейм)
                            string fullName = string.IsNullOrWhiteSpace(realName) || realName == "Без імені" ? tgName : $"{realName} ({tgName})";
                            if (string.IsNullOrWhiteSpace(fullName)) fullName = "Невідомий читач";

                            result.Add((title, fullName, contact, dueDateStr));
                        }
                    }
                }
            }
            return result;
        }
    }
}