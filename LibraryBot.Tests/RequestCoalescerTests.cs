using LibraryBot.Services;

namespace LibraryBot.Tests;

public class RequestCoalescerTests
{
    [Fact]
    public void DuplicateWhileInFlight_IsDropped()
    {
        const string key = "cb:5005:act_b_1";
        Assert.True(RequestCoalescer.TryEnter(key));   // перший — єдиний власник
        Assert.False(RequestCoalescer.TryEnter(key));  // дубль під час обробки → відкинуто
        RequestCoalescer.Exit(key);
    }

    [Fact] // вікно придушення дублікатів ~1с після завершення
    public void DuplicateRightAfterFinish_IsDropped()
    {
        const string key = "cb:6006:act_b_2";
        Assert.True(RequestCoalescer.TryEnter(key));
        RequestCoalescer.Exit(key);
        Assert.False(RequestCoalescer.TryEnter(key)); // одразу після завершення — ще дубль
    }

    [Fact]
    public void DifferentKeys_BothProceed()
    {
        Assert.True(RequestCoalescer.TryEnter("k:7007:a"));
        Assert.True(RequestCoalescer.TryEnter("k:7007:b"));
        RequestCoalescer.Exit("k:7007:a");
        RequestCoalescer.Exit("k:7007:b");
    }
}
