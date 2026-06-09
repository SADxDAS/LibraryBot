using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace LibraryBot.Services
{
    /// <summary>
    /// Коалесинг (single-flight) однакових запитів: якщо за короткий проміжок прилітає
    /// багато ІДЕНТИЧНИХ запитів від одного користувача (подвійні/потрійні натискання,
    /// дубльовані повідомлення), обробляється й відповідає лише ПЕРШИЙ — решта тихо
    /// відкидаються. «Один запит в обробці = одна відповідь.»
    ///
    /// Працює як проміс/«в польоті»: поки перший запит з певним ключем обробляється
    /// (InFlight), дублікати ігноруються; плюс невелике вікно після завершення, щоб
    /// зловити дублікати, що прийшли одразу після відповіді.
    ///
    /// Відрізняється від <see cref="RateLimiter"/>: лімітер ріже ОБСЯГ різних запитів,
    /// а коалесер прибирає ДУБЛІКАТИ одного й того ж запиту.
    ///
    /// Потокобезпека: стан кожного ключа змінюється під lock(entry); словник — Concurrent.
    /// </summary>
    public static class RequestCoalescer
    {
        // Вікно придушення дублікатів після завершення обробки.
        private static readonly double SuppressWindowSec = 1.0;

        private const int CleanupThreshold = 5000;
        private static readonly double IdleEvictSec = 60;

        private sealed class Entry
        {
            public bool InFlight;
            public long LastDoneTicks;
        }

        private static readonly ConcurrentDictionary<string, Entry> _map = new();
        private static long _lastCleanupTicks = Stopwatch.GetTimestamp();

        private static double SecondsSince(long ticks)
            => (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;

        /// <summary>
        /// Намагається «зайняти» ключ. true — викликач єдиний власник, хай обробляє;
        /// false — такий самий запит уже в обробці або щойно оброблений → це дублікат, відкинути.
        /// На кожен успішний TryEnter ОБОВ'ЯЗКОВО має бути <see cref="Exit"/> (у finally).
        /// </summary>
        public static bool TryEnter(string key)
        {
            var entry = _map.GetOrAdd(key, _ => new Entry());
            lock (entry)
            {
                if (entry.InFlight) return false; // вже обробляється — дублікат

                if (entry.LastDoneTicks != 0 && SecondsSince(entry.LastDoneTicks) < SuppressWindowSec)
                    return false; // щойно відповіли на такий самий запит — дублікат

                entry.InFlight = true;
                return true;
            }
        }

        /// <summary>Звільняє ключ після завершення обробки та фіксує час завершення.</summary>
        public static void Exit(string key)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                lock (entry)
                {
                    entry.InFlight = false;
                    entry.LastDoneTicks = Stopwatch.GetTimestamp();
                }
            }
            MaybeCleanup();
        }

        private static void MaybeCleanup()
        {
            if (_map.Count < CleanupThreshold) return;
            if (SecondsSince(_lastCleanupTicks) < 30) return;
            _lastCleanupTicks = Stopwatch.GetTimestamp();

            foreach (var kv in _map)
            {
                var e = kv.Value;
                bool evict;
                lock (e) { evict = !e.InFlight && e.LastDoneTicks != 0 && SecondsSince(e.LastDoneTicks) > IdleEvictSec; }
                if (evict) _map.TryRemove(kv.Key, out _);
            }
        }
    }
}
