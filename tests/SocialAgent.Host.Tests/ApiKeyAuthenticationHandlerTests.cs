using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SocialAgent.Host.Auth;

namespace SocialAgent.Host.Tests;

[TestClass]
public class ApiKeyAuthenticationHandlerTests
{
    private const string ConfiguredKey = "super-secret-key";

    private static async Task<AuthenticateResult> AuthenticateAsync(
        Action<HttpRequest> configureRequest, string configuredKey = ConfiguredKey)
    {
        var options = new ApiKeyAuthenticationOptions { ApiKey = configuredKey };
        var monitor = new StaticOptionsMonitor(options);
        var handler = new ApiKeyAuthenticationHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default);

        var context = new DefaultHttpContext();
        configureRequest(context.Request);

        var scheme = new AuthenticationScheme(
            ApiKeyAuthenticationHandler.SchemeName, null, typeof(ApiKeyAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    [TestMethod]
    public async Task ValidKey_InXApiKeyHeader_Succeeds()
    {
        var result = await AuthenticateAsync(r => r.Headers["X-Api-Key"] = ConfiguredKey);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("ApiKeyClient", result.Principal!.Identity!.Name);
    }

    [TestMethod]
    public async Task ValidKey_InAuthorizationHeader_Succeeds()
    {
        var result = await AuthenticateAsync(r => r.Headers.Authorization = $"ApiKey {ConfiguredKey}");

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task WrongKey_Fails()
    {
        var result = await AuthenticateAsync(r => r.Headers["X-Api-Key"] = "wrong-key");

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Invalid API key.", result.Failure!.Message);
    }

    [TestMethod]
    public async Task KeyOfDifferentLength_Fails_WithoutThrowing()
    {
        // FixedTimeEquals requires equal-length spans; a short key must fail cleanly.
        var result = await AuthenticateAsync(r => r.Headers["X-Api-Key"] = "s");

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task NoCredentials_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(_ => { });

        Assert.IsFalse(result.Succeeded);
        Assert.IsNull(result.Failure);
        Assert.IsTrue(result.None);
    }

    [TestMethod]
    public async Task ServerWithoutConfiguredKey_Fails()
    {
        var result = await AuthenticateAsync(r => r.Headers["X-Api-Key"] = ConfiguredKey, configuredKey: string.Empty);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.Failure!.Message, "not configured");
    }

    [TestMethod]
    public async Task DuplicateApiKeyHeaders_AreRejected()
    {
        var result = await AuthenticateAsync(r => r.Headers["X-Api-Key"] = new[] { "wrong", ConfiguredKey });

        Assert.IsFalse(result.Succeeded, "a repeated header must not be joined into a candidate key");
    }

    [TestMethod]
    public async Task NonApiKeyAuthorizationScheme_IsIgnored()
    {
        var result = await AuthenticateAsync(r => r.Headers.Authorization = $"Bearer {ConfiguredKey}");

        Assert.IsTrue(result.None);
    }

    private sealed class StaticOptionsMonitor(ApiKeyAuthenticationOptions options)
        : IOptionsMonitor<ApiKeyAuthenticationOptions>
    {
        public ApiKeyAuthenticationOptions CurrentValue => options;
        public ApiKeyAuthenticationOptions Get(string? name) => options;
        public IDisposable? OnChange(Action<ApiKeyAuthenticationOptions, string?> listener) => null;
    }
}

[TestClass]
public class AuthServiceExtensionsTests
{
    private static IConfiguration Config(string? apiKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(apiKey is null
                ? []
                : new Dictionary<string, string?> { ["Authentication:ApiKey"] = apiKey })
            .Build();

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SocialAgent.Host";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [TestMethod]
    public void Production_WithoutApiKey_FailsAtStartup()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddApiKeyAuthentication(Config(null), new Env("Production")));

        StringAssert.Contains(ex.Message, "Authentication:ApiKey");
    }

    [TestMethod]
    public void Production_WithApiKey_Starts()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApiKeyAuthentication(Config("a-key"), new Env("Production"));

        Assert.IsTrue(services.Count > 0);
    }

    [TestMethod]
    public void Development_WithoutApiKey_Starts()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddApiKeyAuthentication(Config(null), new Env("Development"));

        Assert.IsTrue(services.Count > 0);
    }
}
