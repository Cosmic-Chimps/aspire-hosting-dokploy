using Aspire.Hosting.ApplicationModel;

namespace CosmicChimps.Aspire.Hosting.Dokploy;

/// <summary>
/// A configuration value that is either known when the application model is built (a literal
/// string) or only when the deployment runs (an Aspire <see cref="ParameterResource"/> or any other
/// <see cref="IValueProvider"/>).
/// </summary>
/// <remarks>
/// <para>
/// Deployment settings such as the Dokploy URL, the API token, and public domains often belong in
/// Aspire parameters rather than <c>IConfiguration</c> — parameters are prompted for, can be marked
/// secret, are surfaced in the manifest, and can be supplied per-environment. Reading them through
/// <c>builder.Configuration</c> at model-build time forces the value to exist too early and loses
/// all of that (issue #1).
/// </para>
/// <para>
/// Both forms are accepted implicitly, so existing string assignments keep working unchanged:
/// </para>
/// <code>
/// settings.DokployUrl = "https://paas.example.com";     // literal
/// settings.DokployUrl = builder.AddParameter("dokploy-url");  // deferred
/// </code>
/// <para>
/// Resolution happens once, at the start of the deploy step — never at model-build time, which is
/// the whole point.
/// </para>
/// </remarks>
public sealed class DokployValue
{
    private readonly string? _literal;
    private readonly IValueProvider? _provider;
    private readonly string? _providerName;

    private DokployValue(string literal) => _literal = literal;

    private DokployValue(IValueProvider provider, string? providerName)
    {
        _provider = provider;
        _providerName = providerName;
    }

    /// <summary>True when this holds a deferred value that has not been resolved yet.</summary>
    public bool IsDeferred => _provider is not null;

    /// <summary>Wraps a literal string.</summary>
    public static DokployValue FromLiteral(string value) => new(value);

    /// <summary>Wraps an Aspire parameter, resolved when the deployment runs.</summary>
    public static DokployValue FromParameter(IResourceBuilder<ParameterResource> parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new DokployValue(parameter.Resource, parameter.Resource.Name);
    }

    /// <summary>Wraps any value provider (a parameter, a reference expression, an endpoint).</summary>
    public static DokployValue FromValueProvider(IValueProvider provider, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new DokployValue(provider, name);
    }

    public static implicit operator DokployValue?(string? value) =>
        value is null ? null : new DokployValue(value);

    public static implicit operator DokployValue(ParameterResource parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        return new DokployValue(parameter, parameter.Name);
    }

    /// <summary>Resolves the value. Call during deployment, not while building the model.</summary>
    public async ValueTask<string?> GetValueAsync(CancellationToken ct = default) =>
        _provider is not null ? await _provider.GetValueAsync(ct).ConfigureAwait(false) : _literal;

    /// <summary>
    /// Safe for logs: a deferred value renders as its parameter name, never its resolved content.
    /// Several of these are secrets, so this must never return the value itself.
    /// </summary>
    public override string ToString() =>
        _provider is not null ? $"<parameter:{_providerName ?? "unnamed"}>" : _literal ?? string.Empty;
}

/// <summary>
/// Turns an Aspire parameter builder into a <see cref="DokployValue"/>.
/// </summary>
/// <remarks>
/// C# forbids user-defined conversions from an interface type, and <c>AddParameter</c> returns
/// <c>IResourceBuilder&lt;ParameterResource&gt;</c> — so an implicit conversion is impossible and
/// this (or <c>.Resource</c>, which converts implicitly) is the way to assign a parameter to a
/// setting.
/// </remarks>
/// <example>
/// <code>
/// var url = builder.AddParameter("dokploy-url");
/// var token = builder.AddParameter("dokploy-token", secret: true);
///
/// builder.PublishToDokploy("myapp", s =>
/// {
///     s.DokployUrl = url.AsDokployValue();
///     s.ApiToken = token.AsDokployValue();   // or: token.Resource
/// });
/// </code>
/// </example>
public static class DokployParameterExtensions
{
    /// <summary>Wraps the parameter so it resolves when the deployment runs.</summary>
    public static DokployValue AsDokployValue(this IResourceBuilder<ParameterResource> parameter) =>
        DokployValue.FromParameter(parameter);
}

/// <summary>Convenience helpers for resolving optional <see cref="DokployValue"/> instances.</summary>
internal static class DokployValueExtensions
{
    internal static async ValueTask<string?> ResolveAsync(
        this DokployValue? value,
        CancellationToken ct
    ) => value is null ? null : await value.GetValueAsync(ct).ConfigureAwait(false);

    /// <summary>Resolves, falling back to <paramref name="fallback"/> when null or whitespace.</summary>
    internal static async ValueTask<string> ResolveOrDefaultAsync(
        this DokployValue? value,
        string fallback,
        CancellationToken ct
    )
    {
        var resolved = await value.ResolveAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved) ? fallback : resolved;
    }

    /// <summary>Resolves a required value, throwing with the setting's name when it is absent.</summary>
    internal static async ValueTask<string> ResolveRequiredAsync(
        this DokployValue? value,
        string settingName,
        CancellationToken ct
    )
    {
        var resolved = await value.ResolveAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(resolved)
            ? throw new InvalidOperationException(
                $"Dokploy setting '{settingName}' resolved to an empty value. Set it to a literal "
                    + "string or an Aspire parameter (builder.AddParameter(...))."
            )
            : resolved;
    }
}
