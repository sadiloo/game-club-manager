using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskCustomerLoginMessageMappingTests
{
    [Fact]
    public void every_mapped_key_has_distinct_placeholder_display_text()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in KioskLoginUserMessages.MappedKeys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var text = KioskLoginUserMessages.DisplayText(key);
            Assert.StartsWith(KioskLoginUserMessages.PlaceholderPrefix, text, StringComparison.Ordinal);
            Assert.EndsWith(key, text, StringComparison.Ordinal);
            Assert.True(seen.Add(text), $"duplicate placeholder for key {key}");
        }

        Assert.Equal(KioskLoginUserMessages.MappedKeys.Count, seen.Count);
    }

    [Theory]
    [InlineData(KioskLoginUserMessages.Keys.InvalidLogin)]
    [InlineData(KioskLoginUserMessages.Keys.StationBusy)]
    [InlineData(KioskLoginUserMessages.Keys.EmptyFields)]
    public void known_key_resolves_to_own_placeholder(string key)
    {
        Assert.Equal(
            KioskLoginUserMessages.PlaceholderPrefix + key,
            KioskLoginUserMessages.DisplayText(key));
    }

    [Fact]
    public void unknown_server_code_maps_to_unknown_key_not_http_generic()
    {
        const string body = """{"code":"not_a_real_code","message":"x"}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 418);
        Assert.Equal(KioskLoginUserMessages.Keys.Unknown, key);
    }

    [Fact]
    public void server_semantic_code_wins_over_http_status_bucket()
    {
        const string body = """{"code":"invalid_login","message":"Invalid login or password."}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 401);
        Assert.Equal(KioskLoginUserMessages.Keys.InvalidLogin, key);
    }

    [Fact]
    public void http_failure_without_parseable_code_uses_http_generic_key()
    {
        var key = KioskLoginUserMessages.ResolveFailureKey(responseBody: null, httpStatus: 503);
        Assert.Equal(KioskLoginUserMessages.Keys.HttpGeneric, key);
    }

    [Theory]
    [InlineData(KioskLoginUserMessages.LoginTransportFailure.Unreachable, KioskLoginUserMessages.Keys.Unreachable)]
    [InlineData(KioskLoginUserMessages.LoginTransportFailure.Timeout, KioskLoginUserMessages.Keys.Timeout)]
    public void transport_failure_maps_before_http(
        KioskLoginUserMessages.LoginTransportFailure transport,
        string expectedKey)
    {
        const string body = """{"code":"invalid_login"}""";
        var key = KioskLoginUserMessages.ResolveFailureKey(body, 401, transport);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData("", "pass")]
    [InlineData("user", "")]
    [InlineData("   ", "pass")]
    [InlineData("user", "   ")]
    public void empty_or_whitespace_credentials_map_to_empty_fields_key(string login, string password)
    {
        var key = KioskLoginUserMessages.TryGetEmptyFieldsKey(login, password);
        Assert.Equal(KioskLoginUserMessages.Keys.EmptyFields, key);
    }

    [Fact]
    public void non_empty_credentials_pass_empty_fields_gate()
    {
        Assert.Null(KioskLoginUserMessages.TryGetEmptyFieldsKey("ali", "secret"));
    }

    [Fact]
    public void login_error_survives_lock_line_refresh_cycle_using_placeholder_text()
    {
        const string lockBase = "Locked — PC-1 · no server response";
        var error = KioskLoginUserMessages.DisplayText(KioskLoginUserMessages.Keys.Timeout);
        var first = KioskLoginUserMessages.AppendToLockLine(lockBase, error);
        var second = KioskLoginUserMessages.AppendToLockLine(lockBase, error);
        Assert.Equal(first, second);
    }
}