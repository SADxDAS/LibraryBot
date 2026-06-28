using LibraryBot.Services;

namespace LibraryBot.Tests;

public class BookSearchTests
{
    [Fact]
    public void ExactTitleMatch_IsFound()
        => Assert.True(BookSearch.IsMatch("Гаррі Поттер", "Гаррі Поттер і філософський камінь", "Дж. Роулінг"));

    [Fact] // пропущена літера 'т' у "поттер"
    public void TypoInWord_StillMatches()
        => Assert.True(BookSearch.IsMatch("гаррі потер", "Гаррі Поттер", "Роулінг"));

    [Fact]
    public void Prefix_Matches()
        => Assert.True(BookSearch.IsMatch("гар", "Гаррі Поттер", ""));

    [Fact]
    public void CaseInsensitive_Matches()
        => Assert.True(BookSearch.IsMatch("КОБЗАР", "кобзар", ""));

    [Fact]
    public void MatchesByAuthor()
        => Assert.True(BookSearch.IsMatch("шевченко", "Кобзар", "Тарас Шевченко"));

    [Fact]
    public void TokensInAnyOrder_Match()
        => Assert.True(BookSearch.IsMatch("поттер гаррі", "Гаррі Поттер", ""));

    [Fact]
    public void Garbage_DoesNotMatch()
        => Assert.False(BookSearch.IsMatch("zzzzz qqqqq", "Кобзар", "Тарас Шевченко"));

    [Fact]
    public void EmptyQuery_ScoresZero()
        => Assert.True(BookSearch.Score("", "Кобзар", "Шевченко") == 0);

    [Fact]
    public void ExactRanksHigherThanTypo()
    {
        double exact = BookSearch.Score("кобзар", "Кобзар", "Шевченко");
        double typo = BookSearch.Score("кобзур", "Кобзар", "Шевченко"); // одна одруківка
        Assert.True(typo > 0);
        Assert.True(exact > typo);
    }

    [Fact] // скомпільований запит має давати ті самі результати, що й разовий
    public void Compile_ReusableAcrossBooks()
    {
        var q = BookSearch.Compile("кобзар");
        Assert.True(q.Score("Кобзар", "Шевченко") > 0);
        Assert.True(q.Score("Гаррі Поттер", "Роулінг") == 0);
    }
}
