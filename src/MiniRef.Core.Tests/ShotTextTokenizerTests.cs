using MiniRef.Core.Services;

namespace MiniRef.Core.Tests;

public class ShotTextTokenizerTests
{
    [Fact]
    public void EmptyString_YieldsNoTokens()
    {
        Assert.Empty(ShotTextTokenizer.Tokenize(""));
    }

    [Fact]
    public void PlainTextOnly_YieldsOnePlainToken()
    {
        var tokens = ShotTextTokenizer.Tokenize("Just a sentence.").ToList();

        var token = Assert.Single(tokens);
        Assert.Null(token.Kind);
        Assert.Equal("Just a sentence.", token.Text);
    }

    [Fact]
    public void TagSurroundedByText_SplitsIntoThreeTokens()
    {
        var tokens = ShotTextTokenizer.Tokenize("before <Subject 2> after").ToList();

        Assert.Equal(3, tokens.Count);
        Assert.Equal(("before ", (ShotTagKind?)null, 0), (tokens[0].Text, tokens[0].Kind, tokens[0].Number));
        Assert.Equal(("<Subject 2>", (ShotTagKind?)ShotTagKind.Subject, 2), (tokens[1].Text, tokens[1].Kind, tokens[1].Number));
        Assert.Equal((" after", (ShotTagKind?)null, 0), (tokens[2].Text, tokens[2].Kind, tokens[2].Number));
    }

    [Fact]
    public void AdjacentTags_YieldNoPlainTokenBetween()
    {
        var tokens = ShotTextTokenizer.Tokenize("<Subject 1><Picture 3>").ToList();

        Assert.Equal(2, tokens.Count);
        Assert.Equal(ShotTagKind.Subject, tokens[0].Kind);
        Assert.Equal(ShotTagKind.Picture, tokens[1].Kind);
        Assert.Equal(3, tokens[1].Number);
    }

    [Theory]
    [InlineData("<Subject 5>", ShotTagKind.Subject, 5)]
    [InlineData("<Picture 12>", ShotTagKind.Picture, 12)]
    [InlineData("<Audio 1>", ShotTagKind.Audio, 1)]
    [InlineData("<Video 9>", ShotTagKind.Video, 9)]
    public void RecognizesAllFourTagKinds(string tag, ShotTagKind expectedKind, int expectedNumber)
    {
        var token = Assert.Single(ShotTextTokenizer.Tokenize(tag));
        Assert.Equal(expectedKind, token.Kind);
        Assert.Equal(expectedNumber, token.Number);
    }

    [Fact]
    public void UnrecognizedBracketedSyntax_StaysPlainText()
    {
        // <d>...</d> dialogue tags and other bracketed syntax aren't reference tags and must not
        // be chipified -- only the four reference kinds above are.
        var tokens = ShotTextTokenizer.Tokenize("<Subject 1> says, <d>[English] hi</d>").ToList();

        Assert.Equal(2, tokens.Count);
        Assert.Equal(ShotTagKind.Subject, tokens[0].Kind);
        Assert.Null(tokens[1].Kind);
        Assert.Equal(" says, <d>[English] hi</d>", tokens[1].Text);
    }

    [Theory]
    [InlineData("plain text only")]
    [InlineData("before <Subject 2> after")]
    [InlineData("<Subject 1><Picture 3>")]
    [InlineData("<Subject 1> says, <d>[English] hi</d>")]
    public void ConcatenatingTokenText_ReconstructsOriginalString(string original)
    {
        var reconstructed = string.Concat(ShotTextTokenizer.Tokenize(original).Select(t => t.Text));
        Assert.Equal(original, reconstructed);
    }
}
