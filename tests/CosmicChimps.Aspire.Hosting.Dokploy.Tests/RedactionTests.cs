using CosmicChimps.Aspire.Hosting.Dokploy;
using Xunit;

namespace CosmicChimps.Aspire.Hosting.Dokploy.Tests;

/// <summary>
/// Guards the redaction applied to request bodies in failure diagnostics.
/// </summary>
/// <remarks>
/// Failed API calls log the request body so a malformed payload can be diagnosed from CI. Those
/// bodies carry registry credentials and every service environment variable, so redaction is the
/// only thing standing between a failed deploy and a leaked secret in a shared log.
/// </remarks>
public class RedactionTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("apiKey")]
    [InlineData("Dashboard__Otlp__PrimaryApiKey")]
    public void Redact_MasksSecretJsonProperties(string key)
    {
        var redacted = DokployApiClient.Redact($$"""{"{{key}}":"s3cr3t-value"}""");

        Assert.DoesNotContain("s3cr3t-value", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_MasksSecretsInsideTheEnvBlob()
    {
        // The leak that a JSON-key-only implementation misses: Dokploy receives service environment
        // variables as KEY=value lines packed into ONE string property, so the secret is in the
        // value, not the key.
        const string body =
            """{"applicationId":"a1","env":"AUTH__SIGNINGKEY=abc123\nSMTP__PASSWORD=hunter2\nSMTP__HOST=smtp.example.com\nPORT=3000"}""";

        var redacted = DokployApiClient.Redact(body);

        Assert.DoesNotContain("abc123", redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public void Redact_LeavesNonSecretsReadable()
    {
        // Redaction that masks everything is useless for diagnosis — the whole point is to keep the
        // structure and the innocuous values legible.
        const string body =
            """{"applicationId":"a1","env":"SMTP__PASSWORD=hunter2\nSMTP__HOST=smtp.example.com\nPORT=3000"}""";

        var redacted = DokployApiClient.Redact(body);

        Assert.Contains("SMTP__HOST=smtp.example.com", redacted);
        Assert.Contains("PORT=3000", redacted);
        Assert.Contains("\"applicationId\":\"a1\"", redacted);
    }

    [Fact]
    public void Redact_ProducesWellFormedJson()
    {
        // A raw-string delimiter once swallowed the pattern's trailing quote, so redaction emitted
        // {"key":"***""} — invalid JSON in the very diagnostic meant to reveal a malformed body.
        var redacted = DokployApiClient.Redact("""{"buildSecrets":"","name":"app"}""");

        Assert.Equal("""{"buildSecrets":"***","name":"app"}""", redacted);
        var parsed = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal("app", parsed.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Redact_HandlesEmptyBody() => Assert.Equal("(empty)", DokployApiClient.Redact(""));
}
