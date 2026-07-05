using MyGame.Core.AI.Agents;

namespace MyGame.Tests.AI.Agents;

/// <summary>
/// Unit tests for <see cref="GameMaster"/>'s context-window-management
/// helpers (issue #25). These tests exercise the pure helpers that
/// don't require an AI call — the full summarization loop is covered
/// by code review (it requires mocking the AI HTTP responses, which is
/// a larger fixture than this small math warrants).
/// </summary>
public class GameMasterContextWindowTests
{
    [Fact]
    public void EstimateTokens_NullOrEmpty_ReturnsZero()
    {
        // Null + empty + whitespace-only strings all estimate to 0
        // tokens (no content to bill). This avoids spurious triggers
        // when the summary or a message content is null.
        Assert.Equal(0, GameMaster.EstimateTokens(null));
        Assert.Equal(0, GameMaster.EstimateTokens(string.Empty));
    }

    [Fact]
    public void EstimateTokens_FourChars_OneToken()
    {
        // The rough estimate is 4 chars ≈ 1 token. A 4-char string is
        // exactly 1 token.
        Assert.Equal(1, GameMaster.EstimateTokens("abcd"));
    }

    [Fact]
    public void EstimateTokens_RoundsUp()
    {
        // Sub-4-char strings round UP to 1 token — a 1-char string is
        // billed as 1 token, not 0. This matches the typical tokenizer
        // behaviour (every fragment is at least 1 token) and avoids
        // underestimating short messages.
        Assert.Equal(1, GameMaster.EstimateTokens("a"));
        Assert.Equal(1, GameMaster.EstimateTokens("ab"));
        Assert.Equal(1, GameMaster.EstimateTokens("abc"));
        Assert.Equal(1, GameMaster.EstimateTokens("abcd"));
        Assert.Equal(2, GameMaster.EstimateTokens("abcde"));
    }

    [Fact]
    public void EstimateTokens_LongString_ScalesLinearly()
    {
        // The estimate scales linearly with character count (no surprises).
        // A 4000-char string ≈ 1000 tokens — typical for a few paragraphs
        // of Russian narrative.
        var text = new string('а', 4000);
        Assert.Equal(1000, GameMaster.EstimateTokens(text));
    }

    [Fact]
    public void EstimateTokens_RussianText_EstimatesReasonably()
    {
        // A typical Russian sentence is ~100 chars → ~25 tokens. The
        // estimate is rough (the real tokenizer is provider-specific)
        // but adequate for triggering summarization — we only need a
        // reasonable upper bound.
        var sentence = "Игрок входит в таверну и осматривается по сторонам в поисках торговца.";
        var estimate = GameMaster.EstimateTokens(sentence);
        Assert.True(estimate > 10 && estimate < 50,
            $"Expected ~10-50 tokens for a 71-char Russian sentence, got {estimate}.");
    }

    [Fact]
    public void SummarizeAfterMessages_DefaultIs30()
    {
        // The default threshold is 30 messages — a conservative value
        // that fires summarization after ~5-10 turns of back-and-forth.
        // Guard against accidental changes.
        Assert.Equal(30, GameMaster.SummarizeAfterMessages);
    }

    [Fact]
    public void SummaryMaxTokens_DefaultIs500()
    {
        // The summarization call's completion-token cap. ~500 tokens
        // leaves headroom for a 300-word summary (~400 tokens) + the
        // model's preamble without burning the full GM MaxTokens budget.
        Assert.Equal(500, GameMaster.SummaryMaxTokens);
    }

    [Fact]
    public void DefaultMaxContextTokens_DefaultIs12000()
    {
        // The default context-window threshold. 12000 is conservative
        // for the typical 16k-context model class; larger models can
        // override via Settings.MaxContextTokens.
        Assert.Equal(12000, GameMaster.DefaultMaxContextTokens);
    }
}
