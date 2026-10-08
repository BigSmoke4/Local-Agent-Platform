using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Shared.Kernel.Files;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers.Api;

/// <summary>Owner-scoped repository discovery for local IDE clients. Paths are returned
/// only for repositories that still satisfy the configured workspace-root policy.</summary>
[ApiController]
[Route("api/repositories")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class RepositoriesApiController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IWorkspaceRootPolicy _workspacePolicy;

    public RepositoriesApiController(PlatformDbContext db, IWorkspaceRootPolicy workspacePolicy)
    {
        _db = db;
        _workspacePolicy = workspacePolicy;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RepositoryDto>>> List(CancellationToken ct)
    {
        var userId = User.RequireUserId();
        var repositories = await _db.Repositories
            .Where(repository => repository.Project!.OwnerUserId == userId)
            .OrderBy(repository => repository.Project!.Name)
            .ThenBy(repository => repository.LocalPath)
            .Select(repository => new RepositoryDto(
                repository.Id,
                repository.Project!.Name,
                repository.LocalPath,
                repository.LastIndexedAtUtc))
            .ToListAsync(ct);

        return Ok(repositories.Where(repository => _workspacePolicy.IsAllowed(repository.LocalPath)).ToArray());
    }
}

public sealed record RepositoryDto(Guid Id, string ProjectName, string LocalPath, DateTimeOffset? LastIndexedAtUtc);
