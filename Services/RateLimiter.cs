using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace LibraryBot.Services
{
    public enum RateDecision
    {
        /// <summary>Запит дозволено — обробляємо як завжди.</summary>
        Allowed,
        /// <summary>Користувач щойно перевищив ліміт — попереджаємо ОДИН раз і ігноруємо запит.</summary>
        FirstThrottle,
        /// <summary>Користувач і далі флудить — тихо відкидаємо (без відповіді, щоб не підсилювати атаку).</summary>
        Throttled,
        /// <summary>Бот під загальним перевантаженням — скидаємо навантаження.</summary>
        GlobalOverload
    }

    /// <summary>
    /// Розумний захист від флуду/спаму на рівні застосунку (anti-flood).
    ///
    /// • Per-user "token bucket": дозволяє нормальні сплески (кілька швидких натискань),
    ///   але обмежує СТАЛУ інтенсивність. Перевищив — попередження раз, далі тиша.
    /// • Ескалація: хто продовжує флудити — отримує дедалі довший тайм-аут (cooldown).
    /// • Глобальний "token bucket": страхує БД/бота від сукупного флуду багатьох акаунтів.
    /// • Адмінів не лімітуємо (вони керують ботом навіть під атакою).
    /// • Прибирання простійних відер — пам'ять не тече навіть за тисяч користувачів.
    ///
    /// ПРИМІТКА: мережевий DDoS відбивається на рівні інфраструктури (Telegram edge + хостинг).
    /// Тут — захист рівня застосунку: ніхто не може вичерпати ресурси бота частими запитами.
    ///
    /// Потокобезпека: кожне відро мутуємо під lock(bucket); словники — Concurrent.
    /// </summary>
    public static class RateLimiter
    {
        // ── Налаштування per-user ─────────────────────────────────────────
        private const double UserCapacity = 8;       // запас на сплеск (кількість «жетонів»)
        private const double UserRefillPerSec = 1.5; // стала дозволена інтенсивність (запитів/сек)
        private const double BaseCooldownSec = 5;    // базовий тайм-аут після перевищення
        private const double MaxCooldownSec = 300;   // стеля тайм-ауту (5 хв)
        private static readonly double ViolationDecaySec = 30; // тиша стільки секунд → «провини» забуваються

        // ── Налаштування глобального ліміту (страхувальник від масового флуду) ─
        private const double GlobalCapacity = 60;
        private const double GlobalRefillPerSec = 40;

        // ── Прибирання простійних відер ───────────────────────────────────
        private const int CleanupThreshold = 5000;          // коли відер більше — пробуємо підмести
        private static readonly double IdleEvictSec = 600;  // прибираємо відра, тихі 10+ хв

        private sealed class Bucket
        {
            public double Tokens = UserCapacity;
            public long LastTicks = Stopwatch.GetTimestamp();
            public int Violations;
            public long BlockedUntilTicks;
            public bool Warned; // чи вже надсилали попередження в поточному епізоді флуду
        }

        private static readonly ConcurrentDictionary<long, Bucket> _users = new();
        private static readonly object _globalGate = new();
        private static double _globalTokens = GlobalCapacity;
        private static long _globalLastTicks = Stopwatch.GetTimestamp();
        private static long _lastCleanupTicks = Stopwatch.GetTimestamp();

        private static double SecondsSince(long ticks)
            => (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;

        /// <summary>Вирішує, що робити із запитом користувача. Адмінів пропускає завжди.</summary>
        public static RateDecision Check(long userId, bool isAdmin)
        {
            if (isAdmin) return RateDecision.Allowed;

            var bucket = _users.GetOrAdd(userId, _ => new Bucket());

            lock (bucket)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - bucket.LastTicks) / (double)Stopwatch.Frequency;
                bucket.LastTicks = now;

                // Поповнюємо жетони відповідно до часу, що минув.
                bucket.Tokens = Math.Min(UserCapacity, bucket.Tokens + elapsed * UserRefillPerSec);

                // Достатньо «тиші» — забуваємо попередні провини й попередження.
                if (elapsed > ViolationDecaySec)
                {
                    bucket.Violations = 0;
                    bucket.Warned = false;
                }

                // Активний тайм-аут?
                if (now < bucket.BlockedUntilTicks)
                    return RateDecision.Throttled;

                if (bucket.Tokens >= 1.0)
                {
                    bucket.Tokens -= 1.0;
                    bucket.Warned = false; // нормальний темп — скидаємо прапорець попередження
                    return CheckGlobal();
                }

                // Жетони скінчилися — це флуд. Ескалуємо тайм-аут.
                bucket.Violations++;
                double cooldown = Math.Min(MaxCooldownSec, BaseCooldownSec * bucket.Violations);
                bucket.BlockedUntilTicks = now + (long)(cooldown * Stopwatch.Frequency);

                if (!bucket.Warned)
                {
                    bucket.Warned = true;
                    return RateDecision.FirstThrottle; // попереджаємо один раз
                }
                return RateDecision.Throttled;
            }
        }

        private static RateDecision CheckGlobal()
        {
            lock (_globalGate)
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = (now - _globalLastTicks) / (double)Stopwatch.Frequency;
                _globalLastTicks = now;
                _globalTokens = Math.Min(GlobalCapacity, _globalTokens + elapsed * GlobalRefillPerSec);

                if (_globalTokens >= 1.0)
                {
                    _globalTokens -= 1.0;
                    MaybeCleanup();
                    return RateDecision.Allowed;
                }
                return RateDecision.GlobalOverload;
            }
        }

        /// <summary>Зрідка підмітаємо простійні відра, щоб словник не ріс безмежно.</summary>
        private static void MaybeCleanup()
        {
            if (_users.Count < CleanupThreshold) return;
            if (SecondsSince(_lastCleanupTicks) < 60) return; // не частіше разу на хвилину
            _lastCleanupTicks = Stopwatch.GetTimestamp();

            foreach (var kv in _users)
            {
                var b = kv.Value;
                bool idle;
                lock (b) { idle = SecondsSince(b.LastTicks) > IdleEvictSec; }
                if (idle) _users.TryRemove(kv.Key, out _);
            }
        }
    }
}
