using System;
using System.Globalization;
using System.Text;

namespace LibraryBot.Services
{
    /// <summary>
    /// Універсальний нечіткий (fuzzy) пошук книг.
    ///
    /// Алгоритм:
    ///   1. Нормалізує текст (регістр, апострофи, пунктуація, ё→е).
    ///   2. Розбиває запит і поле на окремі слова (токени).
    ///   3. Для кожного слова запиту шукає найкраще співпадіння серед слів поля
    ///      (точне / префікс / підрядок / обмежена відстань Левенштейна).
    ///   4. Повертає оцінку релевантності (0 = не знайдено), за якою сортуємо результати.
    ///
    /// Продуктивність:
    ///   • Запит компілюється ОДИН раз (<see cref="Compile"/>) і переюзується для всіх книг,
    ///     а не нормалізується наново для кожної з ~200+ книг.
    ///   • Відстань Левенштейна рахується ОБМЕЖЕНО (<see cref="BoundedLevenshtein"/>) з двома
    ///     математично коректними відсіканнями: нижня межа за різницею довжин (dist ≥ ||a|-|b||)
    ///     та рання зупинка за Уккконеном (якщо мінімум рядка DP вже > порога — підсумок теж > порога).
    ///   • Буфери DP — на стеку (stackalloc), без алокацій у купі на кожне порівняння.
    ///
    /// Потокобезпека: клас не має змінюваного стану; усі методи працюють лише з локальними
    /// даними/аргументами, тож його можна безпечно викликати з кількох потоків одночасно.
    /// <see cref="CompiledQuery"/> незмінний після створення.
    /// </summary>
    public static class BookSearch
    {
        /// <summary>Компілює запит один раз для багаторазового скорингу книг.</summary>
        public static CompiledQuery Compile(string query) => new CompiledQuery(query);

        /// <summary>Чи підходить книга під запит (будь-яка релевантність > 0).</summary>
        public static bool IsMatch(string query, string? title, string? author)
            => Compile(query).Score(title, author) > 0;

        /// <summary>Разова оцінка релевантності (компілює запит на льоту).</summary>
        public static double Score(string query, string? title, string? author)
            => Compile(query).Score(title, author);

        /// <summary>Заздалегідь нормалізований і розбитий на токени запит (незмінний).</summary>
        public sealed class CompiledQuery
        {
            private readonly string _normalized;
            private readonly string[] _tokens;

            internal CompiledQuery(string query)
            {
                _normalized = Normalize(query);
                _tokens = _normalized.Length == 0
                    ? Array.Empty<string>()
                    : _normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }

            /// <summary>
            /// Оцінка релевантності книги. 0 — не знайдено; що більше — то точніше (макс. ~1.0).
            /// </summary>
            public double Score(string? title, string? author)
            {
                if (_tokens.Length == 0) return 0;

                string nTitle = Normalize(title);
                string nAuthor = Normalize(author);
                if (nTitle.Length == 0 && nAuthor.Length == 0) return 0;

                // Запасний варіант: весь запит — підрядок поля ("гар" → "гаррі").
                // Точне співпадіння в назві цінуємо трохи вище, ніж в авторі.
                double substringScore = 0;
                if (nTitle.Contains(_normalized)) substringScore = 0.97;
                else if (nAuthor.Contains(_normalized)) substringScore = 0.93;

                string field = (nTitle + " " + nAuthor).Trim();
                var fieldTokens = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fieldTokens.Length == 0) return substringScore;

                double total = 0;
                foreach (var qt in _tokens)
                {
                    int allowed = AllowedDistance(qt.Length);
                    double best = 0;
                    foreach (var ft in fieldTokens)
                    {
                        double s = TokenSimilarity(qt, ft, allowed);
                        if (s > best) best = s;
                        if (best >= 1.0) break;
                    }

                    // Хоча б одне слово запиту нічого не знайшло —
                    // покладаємось лише на підрядковий запасний варіант.
                    if (best <= 0) return substringScore;
                    total += best;
                }

                double tokenScore = total / _tokens.Length;
                return Math.Max(tokenScore, substringScore);
            }
        }

        /// <summary>Схожість двох окремих слів (0..1). allowed — дозволена к-сть одруківок.</summary>
        private static double TokenSimilarity(string q, string f, int allowed)
        {
            if (q == f) return 1.0;
            // Користувач набрав початок слова ("гарр" → "гаррі").
            if (f.StartsWith(q, StringComparison.Ordinal)) return 0.92;
            if (q.StartsWith(f, StringComparison.Ordinal)) return 0.88;
            // Одне слово міститься в іншому.
            if (f.Contains(q) || q.Contains(f)) return 0.82;

            if (allowed <= 0) return 0; // дуже короткі слова — лише точний/префіксний збіг вище

            int dist = BoundedLevenshtein(q, f, allowed);
            if (dist <= allowed)
                return 1.0 - (double)dist / Math.Max(q.Length, f.Length);
            return 0;
        }

        /// <summary>Скільки одруківок допускаємо в слові залежно від його довжини.</summary>
        private static int AllowedDistance(int len)
        {
            if (len <= 2) return 0;
            if (len <= 4) return 1;
            if (len <= 7) return 2;
            if (len <= 11) return 3;
            return 4;
        }

        /// <summary>
        /// Відстань редагування (Левенштейна), обмежена порогом <paramref name="max"/>.
        /// Повертає точну відстань, якщо вона ≤ max, інакше будь-яке число > max.
        ///
        /// Коректні математичні відсікання:
        ///   • Якщо різниця довжин > max — відстань теж > max (нижня межа). Виходимо одразу.
        ///   • Рання зупинка (Укконен): будь-який шлях вирівнювання проходить через кожен рядок
        ///     DP-матриці, а значення вздовж оптимального шляху не спадають. Тож якщо мінімум
        ///     цілого рядка вже > max, то й підсумкова відстань > max.
        /// Буфери — на стеку (без алокацій у купі для коротких слів).
        /// </summary>
        private static int BoundedLevenshtein(string s, string t, int max)
        {
            int n = s.Length, m = t.Length;
            if (Math.Abs(n - m) > max) return max + 1;
            if (n == 0) return m;
            if (m == 0) return n;

            const int StackCap = 64;
            Span<int> prev = (m + 1) <= StackCap ? stackalloc int[StackCap] : new int[m + 1];
            Span<int> curr = (m + 1) <= StackCap ? stackalloc int[StackCap] : new int[m + 1];

            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                int rowMin = i;
                char sc = s[i - 1];

                for (int j = 1; j <= m; j++)
                {
                    int cost = sc == t[j - 1] ? 0 : 1;
                    int del = prev[j] + 1;
                    int ins = curr[j - 1] + 1;
                    int sub = prev[j - 1] + cost;

                    int v = del < ins ? del : ins;
                    if (sub < v) v = sub;
                    curr[j] = v;
                    if (v < rowMin) rowMin = v;
                }

                if (rowMin > max) return max + 1; // рання зупинка — далі тільки гірше

                // міняємо рядки місцями (без копіювання)
                Span<int> tmp = prev;
                prev = curr;
                curr = tmp;
            }

            int result = prev[m];
            return result <= max ? result : max + 1;
        }

        /// <summary>
        /// Нормалізація: нижній регістр, ё→е, апострофи та пунктуація → пробіл,
        /// схлопування пробілів. Плутанину літер (и/і, е/є тощо) свідомо не чіпаємо —
        /// її покриває толерантність Левенштейна (різниця в один символ).
        /// </summary>
        private static string Normalize(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";

            string lower = s.ToLower(CultureInfo.InvariantCulture);
            var sb = new StringBuilder(lower.Length);
            foreach (char c in lower)
            {
                char ch = c == 'ё' ? 'е' : c;
                sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            }

            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
