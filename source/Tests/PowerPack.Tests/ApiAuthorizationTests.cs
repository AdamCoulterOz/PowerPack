using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PowerPack.Options;
using PowerPack.Services;

namespace PowerPack.Tests;

public sealed class ApiAuthorizationTests
{
    [Fact]
    public async Task AuthorizeAsync_Allows_Token_With_Required_Role()
    {
        var service = CreateAuthorizationService([new Claim("roles", "PowerPack.Access")], out var token);
        var request = CreateRequest(token);

        await service.AuthorizeAsync(request, default);
    }

    [Fact]
    public async Task AuthorizeAsync_Allows_User_Token_With_Required_Delegated_Scope()
    {
        var service = CreateAuthorizationService([new Claim("scp", "openid PowerPack.Access profile")], out var token);
        var request = CreateRequest(token);

        await service.AuthorizeAsync(request, default);
    }

    [Fact]
    public async Task AuthorizeAsync_Fails_Loudly_When_Bearer_Token_Is_Missing()
    {
        var service = CreateAuthorizationService([new Claim("roles", "PowerPack.Access")], out _);
        var request = new DefaultHttpContext().Request;

        var exception = await Assert.ThrowsAsync<PowerPackUnauthorizedException>(() => service.AuthorizeAsync(request, default));

        Assert.Equal("Bearer token is required.", exception.Message);
    }

    [Fact]
    public async Task AuthorizeAsync_Fails_Loudly_When_Role_Is_Missing()
    {
        var service = CreateAuthorizationService([new Claim("roles", "Other.Role")], out var token);
        var request = CreateRequest(token);

        var exception = await Assert.ThrowsAsync<PowerPackUnauthorizedException>(() => service.AuthorizeAsync(request, default));

        Assert.Equal(
            "Bearer token is missing required role 'PowerPack.Access' or delegated scope 'PowerPack.Access'.",
            exception.Message
        );
    }

    private static HttpRequest CreateRequest(string token)
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.Authorization = $"Bearer {token}";
        return request;
    }

    private static PowerPackApiAuthorizationService CreateAuthorizationService(IReadOnlyCollection<Claim> claims, out string token)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa.ExportParameters(true));
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = "https://login.microsoftonline.com/test-tenant/v2.0",
        };
        configuration.SigningKeys.Add(new RsaSecurityKey(rsa.ExportParameters(false)));

        token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: configuration.Issuer,
            audience: "api://powerpack.test",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
        ));

        return new PowerPackApiAuthorizationService(
            new AuthOptions
            {
                ApplicationClientId = "powerpack-client-id",
                ApplicationIdUri = "api://powerpack.test",
                TenantId = "test-tenant",
                RequiredRole = "PowerPack.Access",
                RequiredScope = "PowerPack.Access",
            },
            new StaticConfigurationManager(configuration)
        );
    }

    private sealed class StaticConfigurationManager(OpenIdConnectConfiguration configuration)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }
    }
}
