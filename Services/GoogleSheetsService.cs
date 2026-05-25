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
        static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
        static readonly string ApplicationName = "LibraryBot";
        public static string SpreadsheetId { get; private set; } = "";
        static SheetsService? _service;

        public static void Initialize()
        {
            SpreadsheetId = Environment.GetEnvironmentVariable("SPREADSHEET_ID")
                ?? throw new Exception("Помилка: SPREADSHEET_ID не знайдено в .env файлі!");

            // Вимикаємо попередження CS0618 тільки для наступного рядка
#pragma warning disable CS0618
            GoogleCredential credential = GoogleCredential.FromFile("credentials.json").CreateScoped(Scopes);
#pragma warning restore CS0618 // Вмикаємо попередження назад

            _service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            Console.WriteLine("Google Sheets connected!");
        }        // Беремо 6 колонок (до F)
        public static async Task<IList<IList<object>>?> GetBooksAsync()
        {
            string range = "Каталог!A2:F";
            SpreadsheetsResource.ValuesResource.GetRequest request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);

            try
            {
                ValueRange response = await request.ExecuteAsync();
                return response.Values;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка читання таблиці: {ex.Message}");
                return null;
            }
        }

        public static async Task AddBorrowingAsync(string bookTitle, string realName, string telegramName, string contact, long chatId, DateTime dueDate)
        {
            string range = "Видачі!A:I";
            var valueRange = new ValueRange();

            var rowList = new List<object>
            {
                bookTitle, realName, telegramName, contact,
                DateTime.Now.ToString("dd.MM.yyyy HH:mm"), "",
                chatId.ToString(), dueDate.ToString("dd.MM.yyyy"), ""
            };
            valueRange.Values = new List<IList<object>> { rowList };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            try { await appendRequest.ExecuteAsync(); } catch (Exception ex) { Console.WriteLine($"Помилка: {ex.Message}"); }
        }

        // Адаптер для ручної видачі (щоб не ламати старий код)
        public static async Task UpdateBookStatusAsync(string bookTitle, string newStatus)
        {
            int delta = newStatus.Equals("Доступна", StringComparison.OrdinalIgnoreCase) ? 1 : -1;
            var books = await GetBooksAsync();
            if (books == null) return;

            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].Count > 0 && books[i][0]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                {
                    await ChangeAvailableCountAsync(i + 2, delta);
                    break;
                }
            }
        }

        // Математична зміна кількості (Головний рушій)
        public static async Task<bool> ChangeAvailableCountAsync(int rowIndex, int delta)
        {
            string range = $"Каталог!E{rowIndex}";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try
            {
                var response = await request.ExecuteAsync();
                int currentAvailable = 0;
                if (response.Values != null && response.Values.Count > 0)
                {
                    int.TryParse(response.Values[0][0]?.ToString(), out currentAvailable);
                }

                int newAvailable = currentAvailable + delta;
                if (newAvailable < 0) newAvailable = 0;

                var valueRange = new ValueRange();
                valueRange.Values = new List<IList<object>> { new List<object> { newAvailable } };

                var updateRequest = _service.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"Помилка зміни кількості: {ex.Message}"); return false; }
        }

        public static async Task<(bool exists, bool isAvailable)> CheckBookAvailabilityAsync(string bookTitle)
        {
            var books = await GetBooksAsync();
            if (books == null) return (false, false);

            foreach (var row in books)
            {
                if (row.Count > 0 && row[0]?.ToString()?.Equals(bookTitle.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                {
                    int available = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int d) ? d : 0 : 0;
                    return (true, available > 0);
                }
            }
            return (false, false);
        }

        // ВИПРАВЛЕНО: Додано параметр chatId!
        public static async Task LogReturnDateAsync(string bookTitle, long? chatId = null)
        {
            string range = "Видачі!A2:G";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);

            try
            {
                var response = await request.ExecuteAsync();
                var rows = response.Values;
                if (rows == null) return;

                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i].Count > 0 && rows[i][0]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        bool matchChatId = true;
                        if (chatId.HasValue)
                        {
                            string rowChatId = rows[i].Count > 6 ? rows[i][6]?.ToString() ?? "" : "";
                            if (rowChatId != chatId.Value.ToString()) matchChatId = false;
                        }

                        if (matchChatId && (rows[i].Count <= 5 || string.IsNullOrWhiteSpace(rows[i][5]?.ToString())))
                        {
                            int rowIndex = i + 2;
                            string updateRange = $"Видачі!F{rowIndex}";

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
            catch (Exception ex) { Console.WriteLine($"Помилка повернення: {ex.Message}"); }
        }

        public static async Task<List<(string Title, int CatalogRowIndex)>> GetUserBorrowedBooksAsync(string telegramName)
        {
            var result = new List<(string Title, int CatalogRowIndex)>();
            string rangeBorrow = "Видачі!A2:F";
            var requestBorrow = _service!.Spreadsheets.Values.Get(SpreadsheetId, rangeBorrow);
            ValueRange responseBorrow;

            try { responseBorrow = await requestBorrow.ExecuteAsync(); }
            catch { return result; }

            var borrowRows = responseBorrow.Values;
            if (borrowRows == null) return result;

            var catalogBooks = await GetBooksAsync();
            if (catalogBooks == null) return result;

            foreach (var row in borrowRows)
            {
                if (row.Count > 2)
                {
                    string rowTitle = row[0]?.ToString() ?? "";
                    string rowUser = row[2]?.ToString() ?? "";
                    string returnDate = row.Count > 5 ? row[5]?.ToString() ?? "" : "";

                    if (rowUser.Contains(telegramName, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(returnDate))
                    {
                        int catalogRowIndex = -1;
                        for (int i = 0; i < catalogBooks.Count; i++)
                        {
                            if (catalogBooks[i].Count > 0 && catalogBooks[i][0]?.ToString()?.Equals(rowTitle, StringComparison.OrdinalIgnoreCase) == true)
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

        // АДАПТЕР: Ігноруємо старий статус, пишемо 1 Доступно і 1 Всього
        // АДАПТЕР: Додано параметр quantity (за замовчуванням 1)
        public static async Task AddBookToCatalogAsync(string title, string author, string genre, string status, string exchangeStatus, int quantity = 1)
        {
            string range = "Каталог!A:F";
            var valueRange = new ValueRange();

            // Записуємо кількість у колонки E (Доступно) та F (Всього)
            var rowList = new List<object> { title, author, genre, exchangeStatus, quantity, quantity };
            valueRange.Values = new List<IList<object>> { rowList };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
            try { await appendRequest.ExecuteAsync(); } catch { }
        }
        // АДАПТЕР: Оновлюємо тільки перші 4 колонки (щоб не стерти цифри)
        // Оновлений метод редагування книги (записує всі 6 колонок)
        public static async Task<bool> UpdateBookInCatalogAsync(int rowIndex, string title, string author, string genre, string exchangeStatus, int available, int total)
        {
            string range = $"Каталог!A{rowIndex}:F{rowIndex}";

            var valueRange = new ValueRange();
            // Передаємо оновлені текстові поля та прораховані цифри кількості
            valueRange.Values = new List<IList<object>> { new List<object> { title, author, genre, exchangeStatus, available, total } };

            var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            try { await updateRequest.ExecuteAsync(); return true; }
            catch { return false; }
        }
        // Фізичне видалення рядка з таблиці (зі зсувом догори)
        public static async Task<bool> DeleteBookFromCatalogAsync(string title)
        {
            var books = await GetBooksAsync();
            if (books == null) return false;

            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].Count > 0 && books[i][0]?.ToString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true)
                {
                    int rowIndex = i + 2; // +2, бо індекс i починається з 0, а перший рядок — це заголовки

                    try
                    {
                        // 1. Отримуємо внутрішній ID аркуша "Каталог" (SheetId), оскільки API видалення працює саме за ним
                        var spreadsheet = await _service!.Spreadsheets.Get(SpreadsheetId).ExecuteAsync();
                        var sheet = spreadsheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Каталог");
                        if (sheet == null) return false;

                        int sheetId = sheet.Properties.SheetId ?? 0;

                        // 2. Формуємо запит на повне видалення рядка
                        var deleteRequest = new Request
                        {
                            DeleteDimension = new DeleteDimensionRequest
                            {
                                Range = new DimensionRange
                                {
                                    SheetId = sheetId,
                                    Dimension = "ROWS",
                                    StartIndex = rowIndex - 1, // API використовує індексацію з 0 (тому віднімаємо 1)
                                    EndIndex = rowIndex        // EndIndex не включається, тому видалиться рівно 1 рядок
                                }
                            }
                        };

                        var batchUpdateRequest = new BatchUpdateSpreadsheetRequest
                        {
                            Requests = new List<Request> { deleteRequest }
                        };

                        // 3. Виконуємо запит
                        await _service.Spreadsheets.BatchUpdate(batchUpdateRequest, SpreadsheetId).ExecuteAsync();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Помилка повного видалення рядка: {ex.Message}");
                        return false;
                    }
                }
            }
            return false;
        }
        public static async Task AddExchangeLogAsync(string oldTitle, string newTitle, string telegramName)
        {
            string range = "Обмін!A:D";
            var valueRange = new ValueRange();
            var rowList = new List<object> { oldTitle, newTitle, telegramName, DateTime.Now.ToString("dd.MM.yyyy") };
            valueRange.Values = new List<IList<object>> { rowList };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;
            try { await appendRequest.ExecuteAsync(); } catch { }
        }

        public static async Task<IList<IList<object>>?> GetAllBorrowingsAsync()
        {
            string range = "Видачі!A2:I";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);
            try { var response = await request.ExecuteAsync(); return response.Values; }
            catch (Exception ex) { Console.WriteLine($"Помилка читання: {ex.Message}"); return null; }
        }

        public static async Task<bool> ExtendBorrowingAsync(int rowIndex)
        {
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, $"Видачі!H{rowIndex}");
            try
            {
                var response = await request.ExecuteAsync();
                if (response.Values == null || response.Values.Count == 0) return false;

                string currentDueStr = response.Values[0][0]?.ToString() ?? "";
                if (DateTime.TryParseExact(currentDueStr, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dueDate))
                {
                    DateTime newDueDate = dueDate.AddDays(30);
                    string updateRange = $"Видачі!H{rowIndex}:I{rowIndex}";
                    var valueRange = new ValueRange();
                    valueRange.Values = new List<IList<object>> { new List<object> { newDueDate.ToString("dd.MM.yyyy"), "Так" } };

                    var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, updateRange);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                    await updateRequest.ExecuteAsync();
                    return true;
                }
                return false;
            }
            catch (Exception ex) { Console.WriteLine($"Помилка подовження: {ex.Message}"); return false; }
        }
        // Обробка старої книги при обміні (віднімаємо кількість або видаляємо рядок)
        // Обробка старої книги при обміні (приймає індекс рядка)
        public static async Task<bool> ProcessExchangeOutgoingBookAsync(int rowIndex, string title)
        {
            var books = await GetBooksAsync();
            if (books == null || books.Count < rowIndex - 1) return false;

            var row = books[rowIndex - 2]; // Знаходимо конкретний рядок

            // Перевіряємо, чи збігається назва (для безпеки)
            if (row.Count > 0 && row[0]?.ToString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true)
            {
                int disponible = row.Count > 4 ? int.TryParse(row[4]?.ToString(), out int d) ? d : 0 : 0;
                int total = row.Count > 5 ? int.TryParse(row[5]?.ToString(), out int t) ? t : 1 : 1;

                if (total > 1)
                {
                    // Робимо -1 від обох колонок
                    int newDisponible = Math.Max(0, disponible - 1);
                    int newTotal = total - 1;

                    string range = $"Каталог!E{rowIndex}:F{rowIndex}";
                    var valueRange = new ValueRange();
                    valueRange.Values = new List<IList<object>> { new List<object> { newDisponible, newTotal } };

                    var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                    try { await updateRequest.ExecuteAsync(); return true; }
                    catch { return false; }
                }
                else
                {
                    // Якщо копія була єдина — повністю видаляємо рядок
                    return await DeleteBookFromCatalogAsync(title);
                }
            }
            return false;
        }


    }
}