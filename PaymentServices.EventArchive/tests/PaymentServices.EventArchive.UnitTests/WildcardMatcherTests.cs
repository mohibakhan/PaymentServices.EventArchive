using PaymentServices.EventArchive.Services;

namespace PaymentServices.EventArchive.UnitTests;

public class WildcardMatcherTests
{
    [Theory]
    [InlineData("ReceiveCredit - Success", "ReceiveCredit - Success", true)]
    [InlineData("ReceiveCredit - Success", "receivecredit - success", true)]
    [InlineData("ReceiveCredit - Success", "ReceiveCredit", false)]
    [InlineData("ReceiveCredit - Success", "ReceiveCredit*", true)]
    [InlineData("ReceiveCredit - Success", "*Success", true)]
    [InlineData("ReceiveCredit - Success", "*credit*", true)]
    [InlineData("ReceiveCredit - Success", "Receive*Success", true)]
    [InlineData("ReceiveCredit - Success", "Receive*Failure", false)]
    [InlineData("ReceiveCredit - Success", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("", "", true)]
    [InlineData("abc", "a*b*c", true)]
    [InlineData("abc", "a*c*b", false)]
    public void Matches_exactly_or_with_wildcards(string value, string pattern, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.IsMatch(value, pattern));
    }

    [Fact]
    public void A_missing_value_never_matches()
    {
        Assert.False(WildcardMatcher.IsMatch(null, "*"));
    }
}
