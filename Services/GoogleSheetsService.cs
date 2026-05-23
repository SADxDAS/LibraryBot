using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace LibraryBot.Services
{
    public class GoogleSheetsService
    {
        static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
        static readonly string ApplicationName = "LibraryBot";
        // ID твоєї таблиці з посилання
        static readonly string SpreadsheetId = "114FuoP5i-8l2lLwekmqPf3hx1q0Rc0AxZvTek6ZWhmk";
        static SheetsService? _service;

        // Ініціалізація підключення
        public static void Initialize()
        {
            GoogleCredential credential;

            // Зчитуємо файл з ключами, який ти завантажив
            using (var stream = new FileStream("credentials.json", FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);
            }

            _service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });

            Console.WriteLine("Google Sheets connected!");
        }

        // Метод для отримання списку книг
        public static async Task<IList<IList<object>>> GetBooksAsync()
        {
            // УВАГА: Заміни "Каталог" на назву твого аркуша з книгами, якщо ти його перейменував (наприклад, "Каталог")
            // A2:E означає, що ми беремо дані з колонок від A до E, починаючи з 2-го рядка (без заголовків)
            string range = "Каталог!A2:E";

            SpreadsheetsResource.ValuesResource.GetRequest request = _service.Spreadsheets.Values.Get(SpreadsheetId, range);

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

        // Метод для запису нової видачі в таблицю
        public static async Task AddBorrowingAsync(string bookTitle, string telegramName, string contact)
        {
            // Вказуємо, що дописуємо дані на аркуш "Видачі"
            string range = "Видачі!A:D";

            // Формуємо рядок з даними
            var valueRange = new ValueRange();
            var rowList = new List<object> { bookTitle, telegramName, contact, DateTime.Now.ToString("dd.MM.yyyy HH:mm") };
            valueRange.Values = new List<IList<object>> { rowList };

            // Створюємо запит на додавання (Append)
            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;

            try
            {
                await appendRequest.ExecuteAsync();
                Console.WriteLine($"Записано в таблицю: {bookTitle} взяв {telegramName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка запису в таблицю: {ex.Message}");
            }
        }

        // Метод для оновлення статусу книги в каталозі
        public static async Task UpdateBookStatusAsync(string bookTitle, string newStatus)
        {
            // 1. Спочатку отримуємо весь список, щоб знайти на якому рядку ця книга
            var books = await GetBooksAsync();
            if (books == null) return;

            int rowIndex = -1;
            for (int i = 0; i < books.Count; i++)
            {
                // Перевіряємо першу колонку (Назва). Порівнюємо без урахування регістру
                if (books[i].Count > 0 &&
                    books[i][0]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                {
                    // +2 тому, що масив починається з 0, а дані в таблиці ми зчитуємо з 2-го рядка (бо 1-й це шапка)
                    rowIndex = i + 2;
                    break;
                }
            }

            if (rowIndex == -1)
            {
                Console.WriteLine($"Книгу '{bookTitle}' не знайдено в каталозі для оновлення статусу.");
                return;
            }

            // 2. Вказуємо конкретну клітинку для оновлення. 
            // УВАГА: Якщо Статус у тебе не в колонці D, зміни літеру тут (наприклад, "Каталог!E{rowIndex}")
            string range = $"Каталог!D{rowIndex}";

            var valueRange = new ValueRange();
            valueRange.Values = new List<IList<object>> { new List<object> { newStatus } };

            // Створюємо запит на оновлення (Update замість Append)
            var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            try
            {
                await updateRequest.ExecuteAsync();
                Console.WriteLine($"Статус книги '{bookTitle}' успішно змінено на '{newStatus}'.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка оновлення статусу в таблиці: {ex.Message}");
            }
        }

        // Метод для перевірки наявності та статусу книги
        public static async Task<(bool exists, bool isAvailable)> CheckBookAvailabilityAsync(string bookTitle)
        {
            var books = await GetBooksAsync();
            if (books == null) return (false, false);

            foreach (var row in books)
            {
                // Перевіряємо збіг назви (Колонка A, індекс 0)
                if (row.Count > 0 && row[0]?.ToString()?.Equals(bookTitle.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Беремо статус з Колонки D (індекс 3). Якщо колонки немає, вважаємо статус порожнім
                    string status = row.Count > 3 ? row[3]?.ToString()?.Trim() ?? "" : "";

                    // Книга доступна, якщо статус порожній або явно вказано "Доступна"
                    // Якщо там "На руках" або "Не обмінюється" - поверне false
                    bool isAvailable = string.IsNullOrWhiteSpace(status) || status.Equals("Доступна", StringComparison.OrdinalIgnoreCase);

                    return (true, isAvailable);
                }
            }

            // Якщо цикл закінчився і ми нічого не знайшли
            return (false, false);
        }

        // Метод для фіксації дати повернення книги в журналі видач
        public static async Task LogReturnDateAsync(string bookTitle)
        {
            // Зчитуємо дані з аркуша "Видачі" (колонки від A до E)
            string range = "Видачі!A2:E";
            var request = _service!.Spreadsheets.Values.Get(SpreadsheetId, range);

            try
            {
                var response = await request.ExecuteAsync();
                var rows = response.Values;
                if (rows == null) return;

                for (int i = 0; i < rows.Count; i++)
                {
                    // Шукаємо рядок, де збігається назва книги (індекс 0)
                    if (rows[i].Count > 0 && rows[i][0]?.ToString()?.Equals(bookTitle, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Перевіряємо, чи колонка E (індекс 4) порожня або взагалі відсутня в цьому рядку
                        if (rows[i].Count <= 4 || string.IsNullOrWhiteSpace(rows[i][4]?.ToString()))
                        {
                            int rowIndex = i + 2; // Переводимо індекс масиву в номер рядка Excel
                            string updateRange = $"Видачі!E{rowIndex}";

                            var valueRange = new ValueRange();
                            valueRange.Values = new List<IList<object>> { new List<object> { DateTime.Now.ToString("dd.MM.yyyy HH:mm") } };

                            var updateRequest = _service.Spreadsheets.Values.Update(valueRange, SpreadsheetId, updateRange);
                            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                            await updateRequest.ExecuteAsync();
                            Console.WriteLine($"Зафіксовано дату повернення для книги: {bookTitle}");
                            break; // Перериваємо цикл, бо закрили поточну видачу
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Помилка фіксації дати повернення: {ex.Message}");
            }
        }

        public static async Task<List<(string Title, int CatalogRowIndex)>> GetUserBorrowedBooksAsync(string telegramName)
        {
            var result = new List<(string Title, int CatalogRowIndex)>();

            // 1. Зчитуємо всі записи з аркуша "Видачі"
            string rangeBorrow = "Видачі!A2:E";
            var requestBorrow = _service!.Spreadsheets.Values.Get(SpreadsheetId, rangeBorrow);
            ValueRange responseBorrow;

            try { responseBorrow = await requestBorrow.ExecuteAsync(); }
            catch { return result; }

            var borrowRows = responseBorrow.Values;
            if (borrowRows == null) return result;

            // 2. Зчитуємо весь каталог книг для пошуку індексів
            var catalogBooks = await GetBooksAsync();
            if (catalogBooks == null) return result;

            // Шукаємо активні видачі для конкретного користувача
            foreach (var row in borrowRows)
            {
                if (row.Count > 1)
                {
                    string rowTitle = row[0]?.ToString() ?? "";
                    string rowUser = row[1]?.ToString() ?? "";
                    string returnDate = row.Count > 4 ? row[4]?.ToString() ?? "" : "";

                    // Якщо ім'я збігається і книга ще не повернута (пуста дата повернення)
                    if (rowUser.Equals(telegramName, StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(returnDate))
                    {
                        // Знаходимо рядок цієї книги в основному каталозі
                        int catalogRowIndex = -1;
                        for (int i = 0; i < catalogBooks.Count; i++)
                        {
                            if (catalogBooks[i].Count > 0 && catalogBooks[i][0]?.ToString()?.Equals(rowTitle, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                catalogRowIndex = i + 2;
                                break;
                            }
                        }

                        if (catalogRowIndex != -1)
                        {
                            result.Add((rowTitle, catalogRowIndex));
                        }
                    }
                }
            }
            return result;
        }

        // Додавання нової книги
        public static async Task AddBookToCatalogAsync(string title, string author, string genre, string status, string exchangeStatus)
        {
            string range = "Каталог!A:A";
            var valueRange = new ValueRange();

            var rowList = new List<object> { title, author, genre, status, exchangeStatus };
            valueRange.Values = new List<IList<object>> { rowList };

            var appendRequest = _service!.Spreadsheets.Values.Append(valueRange, SpreadsheetId, range);
            appendRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.AppendRequest.ValueInputOptionEnum.USERENTERED;
            appendRequest.InsertDataOption = SpreadsheetsResource.ValuesResource.AppendRequest.InsertDataOptionEnum.INSERTROWS;

            try { await appendRequest.ExecuteAsync(); } catch { }
        }        // Edit книги
                 // Edit книги (додано exchangeStatus та розширено діапазон до E)
        public static async Task<bool> UpdateBookInCatalogAsync(int rowIndex, string title, string author, string genre, string status, string exchangeStatus)
        {
            string range = $"Каталог!A{rowIndex}:E{rowIndex}";

            var valueRange = new ValueRange();
            valueRange.Values = new List<IList<object>> { new List<object> { title, author, genre, status, exchangeStatus } };

            var updateRequest = _service!.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
            updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

            try { await updateRequest.ExecuteAsync(); return true; }
            catch { return false; }
        }        // Видалення книги
        public static async Task<bool> DeleteBookFromCatalogAsync(string title)
        {
            var books = await GetBooksAsync();
            if (books == null) return false;

            for (int i = 0; i < books.Count; i++)
            {
                if (books[i].Count > 0 && books[i][0]?.ToString()?.Equals(title, StringComparison.OrdinalIgnoreCase) == true)
                {
                    int rowIndex = i + 2;
                    string range = $"Каталог!A{rowIndex}:E{rowIndex}";

                    var valueRange = new ValueRange();
                    // Записуємо порожні значення замість тексту
                    valueRange.Values = new List<IList<object>> { new List<object> { "", "", "", "", "" } };
                    var updateRequest = _service.Spreadsheets.Values.Update(valueRange, SpreadsheetId, range);
                    updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;

                    try
                    {
                        await updateRequest.ExecuteAsync();
                        return true;
                    }
                    catch { return false; }
                }
            }
            return false; // Якщо книгу не знайдено
        }

        // Фіксація обміну в журналі
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


    }
}