using Flow4Tech.Infrastructure.Data;
using Npgsql;

namespace Flow4Tech.Tests;

public sealed class DatabaseUrlTests
{
    [Fact]
    public void Railway_style_url_maps_to_an_npgsql_connection_string()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize("postgres://railway:s3cret@db.internal:6543/app_db"));

        Assert.Equal("db.internal", parsed.Host);
        Assert.Equal(6543, parsed.Port);
        Assert.Equal("railway", parsed.Username);
        Assert.Equal("s3cret", parsed.Password);
        Assert.Equal("app_db", parsed.Database);
    }

    [Fact]
    public void Postgresql_scheme_and_default_port_are_accepted()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize("postgresql://u:p@host/db"));

        Assert.Equal("host", parsed.Host);
        Assert.Equal(5432, parsed.Port);
        Assert.Equal("db", parsed.Database);
    }

    [Fact]
    public void Percent_encoded_special_characters_in_the_password_are_decoded()
    {
        var password = "p@ss:w/rd?#&=% +é";
        var url = $"postgres://us%40er:{Uri.EscapeDataString(password)}@host:5432/db";

        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize(url));

        Assert.Equal(password, parsed.Password);
        Assert.Equal("us@er", parsed.Username);
    }

    [Fact]
    public void A_password_with_semicolons_survives_the_round_trip()
    {
        var url = $"postgres://u:{Uri.EscapeDataString("a;b=c;Host=evil")}@host:5432/db";

        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize(url));

        Assert.Equal("a;b=c;Host=evil", parsed.Password);
        Assert.Equal("host", parsed.Host);
    }

    [Fact]
    public void Sslmode_query_parameter_is_honored()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize("postgres://u:p@host:5432/db?sslmode=require"));

        Assert.Equal(SslMode.Require, parsed.SslMode);
    }

    [Fact]
    public void A_plain_connection_string_passes_through_unchanged()
    {
        const string plain = "Host=db;Port=5432;Username=u;Password=p;Database=d";

        Assert.Equal(plain, DatabaseUrl.Normalize(plain));
    }

    [Theory]
    [InlineData("postgres://user:TOPSECRET@/db")]
    [InlineData("postgres://user:TOPSECRET@host:notaport/db")]
    [InlineData("postgres://user:TOPSECRET@host:5432")]
    [InlineData("postgres://:TOPSECRET@host:5432/db")]
    [InlineData("postgres://host:5432/db")]
    [InlineData("postgres://user:TOPSECRET@host:5432/db?sslmode=bogus")]
    public void Invalid_urls_fail_with_a_clear_message_that_does_not_echo_the_secret(string value)
    {
        var ex = Assert.Throws<FormatException>(() => DatabaseUrl.Normalize(value));

        Assert.Contains("postgres://user:password@host:port/database", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_ipv6_host_is_given_to_npgsql_without_brackets()
    {
        var parsed = new NpgsqlConnectionStringBuilder(DatabaseUrl.Normalize("postgres://u:p@[fd12::1]:5432/db"));

        Assert.Equal("fd12::1", parsed.Host);
        Assert.Equal(5432, parsed.Port);
    }

    [Theory]
    [InlineData("postgres://u:p@host:5432/db?sslmod=require")]
    [InlineData("postgres://u:p@host:5432/db?options=SECRETVALUE")]
    [InlineData("postgres://u:p@host:5432/db?sslmode")]
    public void Unknown_or_malformed_query_parameters_are_rejected_without_echoing_them(string value)
    {
        var ex = Assert.Throws<FormatException>(() => DatabaseUrl.Normalize(value));

        Assert.Contains("only sslmode", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETVALUE", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sslmod=", ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("postgres://u:pa#ss@host:5432/db")]
    [InlineData("postgres://u:pa?ss=1@host:5432/db")]
    [InlineData("postgres://u:pa/ss@host:5432/db")]
    public void An_unencoded_hash_question_mark_or_slash_in_the_password_is_refused_not_truncated(string value)
    {
        var ex = Assert.Throws<FormatException>(() => DatabaseUrl.Normalize(value));

        Assert.DoesNotContain("pa", ex.Message.Replace("password", string.Empty, StringComparison.Ordinal).Replace("parsed", string.Empty, StringComparison.Ordinal).Replace("parameter", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_value_fails_clearly()
    {
        var ex = Assert.Throws<FormatException>(() => DatabaseUrl.Normalize("  "));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_prefers_the_explicit_connection_string_over_database_url()
    {
        Assert.Equal(
            "Host=explicit",
            DatabaseUrl.Resolve("Host=explicit", "postgres://u:p@other/db"));
    }

    [Fact]
    public void Resolve_falls_back_to_database_url_and_then_to_null()
    {
        var fromUrl = DatabaseUrl.Resolve(null, "postgres://u:p@host:5432/db");
        Assert.Equal("host", new NpgsqlConnectionStringBuilder(fromUrl).Host);
        Assert.Null(DatabaseUrl.Resolve("", "  "));
    }
}
