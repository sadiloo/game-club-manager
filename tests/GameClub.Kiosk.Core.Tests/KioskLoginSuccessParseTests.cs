using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskLoginSuccessParseTests
{
  private const string Unknown = KioskLoginUserMessages.Keys.Unknown;

    [Fact]
    public void missing_session_id_returns_unknown_key()
    {
        Assert.False(KioskLoginUserMessages.TryParseLoginSessionId("""{"customerId":"x"}""", "application/json", out _, out var key));
        Assert.Equal(Unknown, key);
    }

    [Fact]
    public void null_session_id_returns_unknown_key()
    {
        Assert.False(KioskLoginUserMessages.TryParseLoginSessionId("""{"sessionId":null}""", "application/json", out _, out var key));
        Assert.Equal(Unknown, key);
    }

    [Fact]
    public void empty_body_returns_unknown_key()
    {
        Assert.False(KioskLoginUserMessages.TryParseLoginSessionId("", "application/json", out _, out var key));
        Assert.Equal(Unknown, key);
    }

    [Fact]
    public void malformed_json_returns_unknown_key()
    {
        Assert.False(KioskLoginUserMessages.TryParseLoginSessionId("{not json", "application/json", out _, out var key));
        Assert.Equal(Unknown, key);
    }

    [Fact]
    public void wrong_content_type_returns_unknown_key()
    {
        var id = Guid.NewGuid();
        Assert.False(KioskLoginUserMessages.TryParseLoginSessionId(
            $$"""{"sessionId":"{{id}}"}""",
            "text/plain",
            out _,
            out var key));
        Assert.Equal(Unknown, key);
    }

    [Fact]
    public void valid_session_id_parses()
    {
        var id = Guid.Parse("a3b2c1d0-e5f4-3210-abcd-ef1234567890");
        Assert.True(KioskLoginUserMessages.TryParseLoginSessionId(
            $$"""{"sessionId":"{{id}}"}""",
            "application/json",
            out var parsed,
            out var key));
        Assert.Equal(id, parsed);
        Assert.Equal(Unknown, key);
    }
}