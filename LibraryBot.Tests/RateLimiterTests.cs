using LibraryBot.Services;

namespace LibraryBot.Tests;

// Кожен тест використовує унікальний userId, бо RateLimiter тримає стан у статичному словнику.
public class RateLimiterTests
{
    [Fact]
    public void Admin_IsNeverThrottled()
    {
        const long id = 1001;
        for (int i = 0; i < 100; i++)
            Assert.Equal(RateDecision.Allowed, RateLimiter.Check(id, isAdmin: true));
    }

    [Fact]
    public void Burst_Allowed_ThenWarned_ThenSilentlyDropped()
    {
        const long id = 2002;
        // Перші 8 (ємність відра) — миттєвий сплеск дозволено
        for (int i = 0; i < 8; i++)
            Assert.Equal(RateDecision.Allowed, RateLimiter.Check(id, isAdmin: false));

        // Перше перевищення → одне попередження
        Assert.Equal(RateDecision.FirstThrottle, RateLimiter.Check(id, isAdmin: false));
        // Далі — тиха відмова
        Assert.Equal(RateDecision.Throttled, RateLimiter.Check(id, isAdmin: false));
    }

    [Fact]
    public void DifferentUsers_DoNotShareBudget()
    {
        const long a = 3003, b = 4004;
        for (int i = 0; i < 8; i++) RateLimiter.Check(a, isAdmin: false); // вичерпуємо ліміт A
        // B — окремий користувач, його перший запит має пройти
        Assert.Equal(RateDecision.Allowed, RateLimiter.Check(b, isAdmin: false));
    }
}
