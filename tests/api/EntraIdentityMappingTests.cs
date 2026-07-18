using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CaseLedger.Api.Authentication;
using CaseLedger.Api.Contracts;
using CaseLedger.Api.Data;
using CaseLedger.Api.Domain;
using CaseLedger.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using CaseLedgerAuthenticationSchemes =
    CaseLedger.Api.Authentication.AuthenticationSchemes;

namespace CaseLedger.Api.Tests;

public sealed class EntraIdentityMappingTests
{
    private static readonly Guid TenantId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId =
        Guid.Parse("11111111-1111-1111-1111-111111111112");
    private static readonly Guid ObjectId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BootstrapAdministratorObjectId =
        Guid.Parse("22222222-2222-2222-2222-222222222223");

    [Fact]
    public async Task UnknownIdentityIsRejectedWithoutEmailLinking()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var service = CreateSignInService(db, autoProvisionAnalyst: false);

        var principal = await service.CreateSessionPrincipalAsync(
            ExternalPrincipal(
                TenantId,
                ObjectId,
                email: "analyst@caseledger.dev",
                role: "Admin"));

        Assert.Null(principal);
        Assert.False(await db.ExternalIdentities.AnyAsync());
    }

    [Fact]
    public async Task KnownTenantObjectMappingUsesOnlyLocalUserAndRoleClaims()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var analyst = await db.Users.SingleAsync(
            item => item.Id == DatabaseSeeder.AnalystId);
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            UserId = analyst.Id,
            User = analyst,
            Provider = ExternalIdentityService.MicrosoftEntraProvider,
            TenantId = TenantId,
            ObjectId = ObjectId,
            CreatedAt = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
        var service = CreateSignInService(db, autoProvisionAnalyst: false);

        var principal = await service.CreateSessionPrincipalAsync(
            ExternalPrincipal(
                TenantId,
                ObjectId,
                email: "unrelated@example.invalid",
                role: "Admin"));

        Assert.NotNull(principal);
        Assert.Equal(
            analyst.Id.ToString("D"),
            principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("Analyst", principal.FindFirstValue(ClaimTypes.Role));
        Assert.Equal(
            analyst.Email,
            principal.FindFirstValue(ClaimTypes.Email));
        Assert.Equal(
            SessionPrincipalFactory.EntraAuthenticationSource,
            principal.FindFirstValue(
                SessionPrincipalFactory.AuthenticationSourceClaim));
        Assert.DoesNotContain(
            principal.Claims,
            claim => claim.Value == "unrelated@example.invalid");
    }

    [Fact]
    public async Task MappingFromAnotherTenantIsRejected()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var analyst = await db.Users.SingleAsync(
            item => item.Id == DatabaseSeeder.AnalystId);
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333334"),
            UserId = analyst.Id,
            User = analyst,
            Provider = ExternalIdentityService.MicrosoftEntraProvider,
            TenantId = OtherTenantId,
            ObjectId = ObjectId,
            CreatedAt = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync();
        var service = CreateSignInService(db, autoProvisionAnalyst: false);

        var principal = await service.CreateSessionPrincipalAsync(
            ExternalPrincipal(OtherTenantId, ObjectId));

        Assert.Null(principal);
    }

    [Fact]
    public async Task ExplicitAutoProvisionCreatesPasswordDisabledAnalystOnce()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var service = CreateSignInService(db, autoProvisionAnalyst: true);
        var external = ExternalPrincipal(
            TenantId,
            ObjectId,
            email: "administrator@example.invalid",
            role: "Admin");

        var first = await service.CreateSessionPrincipalAsync(external);
        var second = await service.CreateSessionPrincipalAsync(external);

        Assert.NotNull(first);
        Assert.NotNull(second);
        var localUserId = Guid.Parse(
            Assert.IsType<string>(
                first.FindFirstValue(ClaimTypes.NameIdentifier)));
        Assert.Equal(
            first.FindFirstValue(ClaimTypes.NameIdentifier),
            second.FindFirstValue(ClaimTypes.NameIdentifier));

        db.ChangeTracker.Clear();
        var user = await db.Users.SingleAsync(item => item.Id == localUserId);
        Assert.Equal("Analyst", user.Role);
        Assert.True(user.IsActive);
        Assert.False(user.LocalLoginEnabled);
        Assert.EndsWith(
            "@identity.caseledger.invalid",
            user.Email,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "administrator",
            user.Email,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await db.ExternalIdentities.CountAsync(item =>
                item.Provider == ExternalIdentityService.MicrosoftEntraProvider &&
                item.TenantId == TenantId &&
                item.ObjectId == ObjectId));

        using var client = factory.CreateCookieClient();
        await client.RefreshAntiforgeryTokenAsync();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(user.Email, "not-an-external-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task EnabledEntraAddsNamedOidcWithoutReplacingSessionDefault()
    {
        var configuration = EntraConfiguration(autoProvisionAnalyst: false);
        var services = new ServiceCollection();
        services.AddLogging();
        services
            .AddAuthentication(CaseLedgerAuthenticationSchemes.Session)
            .AddCookie(CaseLedgerAuthenticationSchemes.Session);
        services.AddCaseLedgerExternalAuthentication(configuration);

        await using var provider = services.BuildServiceProvider();
        var authentication = provider
            .GetRequiredService<IOptions<AuthenticationOptions>>()
            .Value;
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
        var entraScheme = await schemes.GetSchemeAsync(
            CaseLedgerAuthenticationSchemes.Entra);
        var oidc = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(CaseLedgerAuthenticationSchemes.Entra);

        Assert.Equal(
            CaseLedgerAuthenticationSchemes.Session,
            authentication.DefaultScheme);
        Assert.NotNull(entraScheme);
        Assert.Equal(typeof(OpenIdConnectHandler), entraScheme.HandlerType);
        Assert.Equal(
            CaseLedgerAuthenticationSchemes.Session,
            oidc.SignInScheme);
        Assert.Equal(
            $"https://login.microsoftonline.com/{TenantId:D}/v2.0",
            oidc.Authority);
        Assert.True(oidc.UsePkce);
        Assert.True(oidc.RequireHttpsMetadata);
        Assert.False(oidc.SaveTokens);
        Assert.False(oidc.MapInboundClaims);
        Assert.Contains("openid", oidc.Scope);
        Assert.Contains("profile", oidc.Scope);
    }

    [Fact]
    public async Task DisabledEntraDoesNotRegisterNamedScheme()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services
            .AddAuthentication(CaseLedgerAuthenticationSchemes.Session)
            .AddCookie(CaseLedgerAuthenticationSchemes.Session);
        services.AddCaseLedgerExternalAuthentication(configuration);

        await using var provider = services.BuildServiceProvider();
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.Null(
            await schemes.GetSchemeAsync(
                CaseLedgerAuthenticationSchemes.Entra));
    }

    [Fact]
    public async Task DisabledEntraLoginEndpointReturnsNotFound()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/entra/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CapabilitiesAreAnonymousAndReportConfiguredMethods()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/capabilities");
        var capabilities = await response.Content
            .ReadFromJsonAsync<AuthCapabilitiesResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capabilities);
        Assert.True(capabilities.DemoLoginEnabled);
        Assert.False(capabilities.EntraEnabled);
        Assert.True(capabilities.ShowDemoCredentials);
    }

    [Fact]
    public async Task DisabledDemoAccountsCannotUseLocalLogin()
    {
        using var factory = new CaseLedgerFactory(demoLoginEnabled: false);
        using var client = factory.CreateClient();

        var capabilities = await client.GetFromJsonAsync<AuthCapabilitiesResponse>(
            "/api/auth/capabilities");
        await client.RefreshAntiforgeryTokenAsync();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("analyst@caseledger.dev", "Analyst123!"));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var demoUsers = await db.Users
            .Where(item =>
                item.Id == DatabaseSeeder.AdminId ||
                item.Id == DatabaseSeeder.AnalystId)
            .ToListAsync();

        Assert.NotNull(capabilities);
        Assert.False(capabilities.DemoLoginEnabled);
        Assert.False(capabilities.ShowDemoCredentials);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        Assert.Equal(2, demoUsers.Count);
        Assert.All(demoUsers, user => Assert.False(user.LocalLoginEnabled));
    }

    [Fact]
    public async Task InactiveUserIsRejectedOnTheNextCookieRequest()
    {
        using var factory = new CaseLedgerFactory();
        using var client = factory.CreateCookieClient();
        await client.RefreshAntiforgeryTokenAsync();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("analyst@caseledger.dev", "Analyst123!"));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
            var analyst = await db.Users.SingleAsync(
                item => item.Id == DatabaseSeeder.AnalystId);
            analyst.IsActive = false;
            await db.SaveChangesAsync();
        }

        var protectedRequest = await client.GetAsync("/api/cases?page=1&pageSize=1");

        Assert.Equal(HttpStatusCode.Unauthorized, protectedRequest.StatusCode);
    }

    [Fact]
    public void PasswordHashesUseFreshRandomSalts()
    {
        var passwords = new PasswordService();

        var first = passwords.HashPassword("same-test-password");
        var second = passwords.HashPassword("same-test-password");

        Assert.NotEqual(first, second);
        Assert.True(passwords.VerifyPassword("same-test-password", first));
        Assert.True(passwords.VerifyPassword("same-test-password", second));
    }

    [Fact]
    public async Task LegacyDeterministicDemoSaltIsRotatedDuringSeeding()
    {
        using var factory = new CaseLedgerFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var analyst = await db.Users.SingleAsync(
            item => item.Id == DatabaseSeeder.AnalystId);
        var legacyHash = LegacyPasswordHash(
            "Analyst123!",
            "caseledger-analyst");
        analyst.PasswordHash = legacyHash;
        await db.SaveChangesAsync();

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
        await db.Entry(analyst).ReloadAsync();

        Assert.NotEqual(legacyHash, analyst.PasswordHash);
        Assert.True(new PasswordService().VerifyPassword(
            "Analyst123!",
            analyst.PasswordHash));
    }

    [Fact]
    public async Task BootstrapObjectIdCreatesOneStrictAdministratorMapping()
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Authentication:Entra:Enabled"] = "true",
            ["Authentication:Entra:TenantId"] = TenantId.ToString("D"),
            ["Authentication:Entra:ClientId"] =
                "44444444-4444-4444-4444-444444444444",
            ["Authentication:Entra:ClientSecret"] =
                "test-only-placeholder",
            ["Authentication:Entra:BootstrapAdministratorObjectId"] =
                BootstrapAdministratorObjectId.ToString("D")
        };
        using var factory = new CaseLedgerFactory(
            demoLoginEnabled: false,
            configurationOverrides: configuration);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CaseLedgerDbContext>();
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();

        await seeder.SeedAsync();
        var mappings = await db.ExternalIdentities
            .Where(item => item.UserId == DatabaseSeeder.AdminId)
            .ToListAsync();
        var signIn = scope.ServiceProvider
            .GetRequiredService<EntraSignInService>();
        var principal = await signIn.CreateSessionPrincipalAsync(
            ExternalPrincipal(TenantId, BootstrapAdministratorObjectId));

        var mapping = Assert.Single(mappings);
        Assert.Equal(TenantId, mapping.TenantId);
        Assert.Equal(BootstrapAdministratorObjectId, mapping.ObjectId);
        Assert.NotNull(principal);
        Assert.Equal("Admin", principal.FindFirstValue(ClaimTypes.Role));

        mapping.ObjectId = ObjectId;
        await db.SaveChangesAsync();
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => seeder.SeedAsync());
        Assert.Contains(
            "immutable administrator identity mapping",
            mismatch.Message,
            StringComparison.Ordinal);
    }

    private static EntraSignInService CreateSignInService(
        CaseLedgerDbContext db,
        bool autoProvisionAnalyst)
    {
        var options = Options.Create(
            EntraConfigurationOptions(autoProvisionAnalyst));
        var identities = new ExternalIdentityService(
            db,
            options,
            TimeProvider.System);
        return new EntraSignInService(identities, options);
    }

    private static ClaimsPrincipal ExternalPrincipal(
        Guid tenantId,
        Guid objectId,
        string email = "external@example.invalid",
        string role = "Analyst")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("tid", tenantId.ToString("D")),
            new Claim("oid", objectId.ToString("D")),
            new Claim("email", email),
            new Claim("roles", role)
        ], "SyntheticOidc");
        return new ClaimsPrincipal(identity);
    }

    private static IConfiguration EntraConfiguration(bool autoProvisionAnalyst) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Entra:Enabled"] = "true",
                ["Authentication:Entra:TenantId"] = TenantId.ToString("D"),
                ["Authentication:Entra:ClientId"] =
                    "44444444-4444-4444-4444-444444444444",
                ["Authentication:Entra:ClientSecret"] =
                    "test-only-placeholder",
                ["Authentication:Entra:CallbackPath"] = "/signin-oidc",
                ["Authentication:Entra:AutoProvisionAnalyst"] =
                    autoProvisionAnalyst.ToString()
            })
            .Build();

    private static CaseLedgerAuthenticationOptions EntraConfigurationOptions(
        bool autoProvisionAnalyst) =>
        new()
        {
            Entra = new EntraAuthenticationOptions
            {
                Enabled = true,
                TenantId = TenantId.ToString("D"),
                ClientId = "44444444-4444-4444-4444-444444444444",
                ClientSecret = "test-only-placeholder",
                AutoProvisionAnalyst = autoProvisionAnalyst
            }
        };

    private static string LegacyPasswordHash(string password, string saltSeed)
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(saltSeed))[..16];
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            120_000,
            HashAlgorithmName.SHA256,
            32);
        return string.Join(
            '$',
            "pbkdf2-sha256",
            120_000,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }
}
