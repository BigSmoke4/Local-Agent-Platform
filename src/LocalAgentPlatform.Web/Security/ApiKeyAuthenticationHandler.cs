using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LocalAgentPlatform.Web.Security;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Validates the real X-Api-Key header against ApiKeyService (which checks a SHA-256
/// hash in Postgres — never a hard-coded or bypassable key). Applied to every
/// controller under /api/* via [Authorize(AuthenticationSchemes = "ApiKey")].
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiKeyService apiKeyService)
        : base(options, logger, encoder)
    {
        _apiKeyService = apiKeyService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var provided) ||
            string.IsNullOrWhiteSpace(provided))
        {
            return AuthenticateResult.Fail($"Missing {ApiKeyAuthenticationOptions.HeaderName} header.");
        }

        var key = await _apiKeyService.ValidateAsync(provided.ToString());
        if (key is null) return AuthenticateResult.Fail("Invalid or revoked API key.");

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, key.OwnerUserId.ToString()),
            new Claim("api_key_id", key.Id.ToString())
        }, Scheme.Name);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
