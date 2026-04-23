using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PowerPack.Options;

namespace PowerPack.Services;

public sealed class PowerPackApiAuthorizationService
{
    private readonly AuthOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly JwtSecurityTokenHandler _tokenHandler = new()
    {
        MapInboundClaims = false,
    };

    public PowerPackApiAuthorizationService(PowerPackOptions options)
        : this(options.Auth, CreateConfigurationManager(options.Auth))
    {
    }

    public PowerPackApiAuthorizationService(
        AuthOptions options,
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager)
    {
        _options = options;
        _configurationManager = configurationManager;
    }

    public async Task AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetBearerToken(request, out var token))
            throw new PowerPackUnauthorizedException("Bearer token is required.");

        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuers =
            [
                _options.Authority,
                $"https://sts.windows.net/{_options.TenantId}/",
            ],
            ValidAudiences =
            [
                _options.ApplicationIdUri,
                _options.ApplicationClientId,
            ],
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        ClaimsPrincipal principal;
        try
        {
            principal = _tokenHandler.ValidateToken(token, validationParameters, out _);
        }
        catch (SecurityTokenException exception)
        {
            throw new PowerPackUnauthorizedException($"Bearer token is invalid: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            throw new PowerPackUnauthorizedException($"Bearer token is invalid: {exception.Message}");
        }

        var roles = principal.FindAll("roles")
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(claim => claim.Value)
            .ToArray();

        if (roles.Contains(_options.RequiredRole, StringComparer.Ordinal))
            return;

        var scopes = principal.FindAll("scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        if (scopes.Contains(_options.RequiredScope, StringComparer.Ordinal))
            return;

        throw new PowerPackUnauthorizedException(
            $"Bearer token is missing required role '{_options.RequiredRole}' or delegated scope '{_options.RequiredScope}'."
        );
    }

    private static bool TryGetBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;

        if (!request.Headers.TryGetValue("Authorization", out var authorizationValues))
            return false;

        var authorization = authorizationValues.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        token = authorization[prefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private static IConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager(AuthOptions options) =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.Authority.TrimEnd('/')}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever
            {
                RequireHttps = true,
            }
        );
}

public sealed class PowerPackUnauthorizedException(string message) : Exception(message);
