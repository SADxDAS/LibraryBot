using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LibraryBot.Services
{
    /// <summary>
    /// Іменовані асинхронні блокування (критичні секції за ключем).
    ///
    /// Операції з ОДНАКОВИМ ключем виконуються строго по черзі (одна за одною),
    /// а з РІЗНИМИ ключами — паралельно, у різних потоках. Це дозволяє різним
    /// користувачам / різним книгам працювати одночасно, не заважаючи одне одному,
    /// але гарантує, що конкурентні дії над одним ресурсом не зіпсують стан.
    ///
    /// Ключі-приклади: "user:{chatId}", "book:{rowIndex}", "request:{reqId}".
    ///
    /// Семафори створюються лінькувато й видаляються, щойно зникає останній охочий,
    /// тож пам'ять не тече навіть від унікальних ключів (як-от GUID запитів).
    ///
    /// Використання:
    ///   using (await AsyncKeyedLock.LockAsync($"book:{rowIndex}", ct))
    ///   {
    ///       // критична секція для цієї книги
    ///   }
    /// </summary>
    public static class AsyncKeyedLock
    {
        private sealed class Entry
        {
            public readonly SemaphoreSlim Semaphore = new(1, 1);
            public int RefCount; // скільки потоків зараз тримають або чекають на цей ключ (під _gate)
        }

        private static readonly Dictionary<string, Entry> _entries = new();
        private static readonly object _gate = new();

        public static async Task<IDisposable> LockAsync(string key, CancellationToken cancellationToken = default)
        {
            Entry entry;
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out entry!))
                {
                    entry = new Entry();
                    _entries[key] = entry;
                }
                entry.RefCount++;
            }

            try
            {
                await entry.Semaphore.WaitAsync(cancellationToken);
            }
            catch
            {
                // Не вдалося зайти (скасування) — відкочуємо лічильник і прибираємо за потреби.
                Cleanup(key, entry);
                throw;
            }

            return new Releaser(key, entry);
        }

        private static void Cleanup(string key, Entry entry)
        {
            lock (_gate)
            {
                entry.RefCount--;
                if (entry.RefCount == 0)
                {
                    _entries.Remove(key);
                    entry.Semaphore.Dispose();
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private readonly string _key;
            private readonly Entry _entry;
            private bool _disposed;

            public Releaser(string key, Entry entry)
            {
                _key = key;
                _entry = entry;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _entry.Semaphore.Release();
                lock (_gate)
                {
                    _entry.RefCount--;
                    if (_entry.RefCount == 0)
                    {
                        _entries.Remove(_key);
                        _entry.Semaphore.Dispose();
                    }
                }
            }
        }
    }
}
