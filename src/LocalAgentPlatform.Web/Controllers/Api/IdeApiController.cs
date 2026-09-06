using LocalAgentPlatform.Modules.IdeIntegration.Domain;
using LocalAgentPlatform.Web.Ide;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LocalAgentPlatform.Web.Controllers.Api;

[ApiController]
[Route("api/ide")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class IdeApiController : ControllerBase
{
    private readonly IIdeIntegrationProvider _provider;
    public IdeApiController(IIdeIntegrationProvider provider) => _provider = provider;

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) => Ok(new { ide = _provider.IdeName, available = await _provider.IsAvailableAsync(ct) });

    [HttpPost("open")]
    public async Task<IActionResult> Open(OpenIdeFileRequest request, CancellationToken ct)
    {
        if (_provider is not VsCodeIdeIntegrationProvider vscode) return StatusCode(501);
        await vscode.OpenFileAsync(request.Path, request.Line, request.Column, ct);
        return Accepted();
    }
}

public sealed record OpenIdeFileRequest(string Path, int? Line, int? Column);
