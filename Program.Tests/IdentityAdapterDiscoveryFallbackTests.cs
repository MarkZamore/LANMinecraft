namespace Minecraft.Tests;

public sealed class IdentityAdapterDiscoveryFallbackTests
{
    private static readonly IdentityAdapterTarget Screen = new("native.jar", "example/Screen");
    private static readonly IdentityAdapterTarget List = new("native.jar", "example/LanList");
    private static readonly IdentityAdapterTarget Entry = new("native.jar", "example/Entry");
    private static readonly IdentityAdapterTarget Login = new("native.jar", "example/Login");
    private static readonly IdentityAdapterTarget Publisher = new("native.jar", "example/Publisher");
    private static IdentityAdapterConfiguration Candidate() => new("mappings.txt", new Dictionary<string, string>
    {
        ["steamDiscoveryEnabled"] = "true", ["steamDiscoveryScreenClasses"] = Screen.ClassName,
        ["steamDiscoveryLanListClasses"] = List.ClassName, ["steamDiscoveryLanEntryClasses"] = Entry.ClassName,
        ["identityHooksEnabled"] = "true", ["lanPublishEnabled"] = "true"
    }, [Screen, List, Entry, Login, Publisher]);

    [Fact]
    public async Task FailedClientList_PreservesOnlyIndependentlyReverifiedLoginAndHostPublication()
    {
        var original = Candidate();
        var verified = new List<IdentityAdapterConfiguration>();
        var warnings = new List<string>();
        var result = await PortableIdentityAdapterService.PreflightWithDiscoveryFallbackAsync(original, candidate =>
        {
            verified.Add(candidate);
            return Task.FromResult<(IdentityAdapterTarget?, string?)>(verified.Count == 1 ? (List, "failed native list") : (null, null));
        }, warnings.Add);

        Assert.Null(result.Refusal);
        Assert.Equal(2, verified.Count);
        Assert.Equal(new[] { Login, Publisher }, verified[1].Targets);
        Assert.Equal("true", result.Configuration.Properties["steamDiscoveryEnabled"]);
        Assert.Equal("true", result.Configuration.Properties["identityHooksEnabled"]);
        Assert.Equal("true", result.Configuration.Properties["lanPublishEnabled"]);
        Assert.Equal("false", result.Configuration.Properties["steamDiscoveryClientEnabled"]);
        Assert.False(original.Properties.ContainsKey("steamDiscoveryClientEnabled"));
        Assert.Single(warnings);
    }

    [Fact]
    public async Task RemainingNativeFailure_RefusesThePartialCandidate()
    {
        var original = Candidate();
        var attempts = 0;
        var result = await PortableIdentityAdapterService.PreflightWithDiscoveryFallbackAsync(original, _ =>
            Task.FromResult<(IdentityAdapterTarget?, string?)>(++attempts == 1 ? (List, "list failed") : (Login, "login failed")),
            _ => throw new InvalidOperationException("An unverified fallback was announced."));
        Assert.Equal(2, attempts);
        Assert.Same(original, result.Configuration);
        Assert.Equal("login failed", result.Refusal);
    }

    [Fact]
    public async Task LoginFailure_IsNeverDowngradedAsAnOptionalBrowserFailure()
    {
        var original = Candidate();
        var attempts = 0;
        var result = await PortableIdentityAdapterService.PreflightWithDiscoveryFallbackAsync(original, _ =>
        {
            attempts++;
            return Task.FromResult<(IdentityAdapterTarget?, string?)>((Login, "login failed"));
        }, _ => throw new InvalidOperationException("Login failure was bypassed."));
        Assert.Equal(1, attempts);
        Assert.Same(original, result.Configuration);
        Assert.Equal("login failed", result.Refusal);
    }

    [Fact]
    public async Task ClientAliasCollisionWithLogin_CannotBypassLoginVerification()
    {
        var original = Candidate();
        var properties = original.Properties.ToDictionary(pair => pair.Key, pair => pair.Value);
        properties["loginClasses"] = List.ClassName;
        original = original with { Properties = properties };
        var attempts = 0;
        var result = await PortableIdentityAdapterService.PreflightWithDiscoveryFallbackAsync(original, _ =>
        {
            attempts++;
            return Task.FromResult<(IdentityAdapterTarget?, string?)>((List, "shared login target failed"));
        }, _ => throw new InvalidOperationException("A login alias collision was bypassed."));
        Assert.Equal(1, attempts);
        Assert.Same(original, result.Configuration);
        Assert.Equal("shared login target failed", result.Refusal);
    }

    [Fact]
    public async Task ClientAliasCollisionWithPublisher_CannotBypassPublisherVerification()
    {
        var original = Candidate();
        var properties = original.Properties.ToDictionary(pair => pair.Key, pair => pair.Value);
        properties["lanShareScreenClasses"] = List.ClassName;
        original = original with { Properties = properties };
        var attempts = 0;
        var result = await PortableIdentityAdapterService.PreflightWithDiscoveryFallbackAsync(original, _ =>
        {
            attempts++;
            return Task.FromResult<(IdentityAdapterTarget?, string?)>((List, "shared publisher target failed"));
        }, _ => throw new InvalidOperationException("A publisher alias collision was bypassed."));
        Assert.Equal(1, attempts);
        Assert.Same(original, result.Configuration);
        Assert.Equal("shared publisher target failed", result.Refusal);
    }
}
