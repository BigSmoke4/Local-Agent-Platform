using LocalAgentPlatform.Modules.IdeIntegration.Domain;
using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Ide;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers.Api;

[ApiController]
[Route("api/ide")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class IdeApiController : ControllerBase
{
    private readonly IIdeIntegrationProvider _provider;
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public IdeApiController(IIdeIntegrationProvider provider, PlatformDbContext db, IWorkspaceRootPolicy workspacePolicy)
    {
        _provider = provider;
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(new { ide = _provider.IdeName, available = await _provider.IsAvailableAsync(ct) });

    [HttpPost("open")]
    public async Task<IActionResult> Open(OpenIdeFileRequest request, CancellationToken ct)
    {
        if (_provider is not VsCodeIdeIntegrationProvider vscode) return StatusCode(501);
        var userId = User.RequireUserId();
        var repository = await _db.Repositories
            .Include(r => r.Project)
            .FirstOrDefaultAsync(r => r.Id == request.RepositoryId && r.Project!.OwnerUserId == userId, ct);
        if (repository is null || !_workspacePolicy.IsAllowed(repository.LocalPath)) return NotFound();
        if (string.IsNullOrWhiteSpace(request.RelativePath) ||
            !WorkspacePathGuard.IsWithinWorkspace(repository.LocalPath, request.RelativePath))
            return BadRequest(new { error = "Path must resolve inside the selected repository." });

        var fullPath = Path.GetFullPath(Path.Combine(repository.LocalPath, request.RelativePath));
        await vscode.OpenFileAsync(fullPath, request.Line, request.Column, ct);
        return Accepted();
    }
}

public sealed record OpenIdeFileRequest(Guid RepositoryId, string RelativePath, int? Line, int? Column);
