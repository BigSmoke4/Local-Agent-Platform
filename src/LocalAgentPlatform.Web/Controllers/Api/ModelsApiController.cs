using LocalAgentPlatform.Shared.Data;
using LocalAgentPlatform.Web.Models.Api;
using LocalAgentPlatform.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LocalAgentPlatform.Web.Controllers.Api;

/// <summary>IDE-agnostic JSON API (spec Section 18). Any editor or tool capable of a
/// local HTTP call can use this without a purpose-built adapter.</summary>
[ApiController]
[Route("api/models")]
[EnableRateLimiting("api")]
[Authorize(AuthenticationSchemes = ApiKeyAuthenticationOptions.SchemeName)]
public sealed class ModelsApiController : ControllerBase
{
    private readonly PlatformDbContext _db;
    public ModelsApiController(PlatformDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RegisteredModelDto>>> List(CancellationToken ct)
    {
        var models = await _db.RegisteredModels
            .OrderByDescending(m => m.IsDefault).ThenBy(m => m.Name)
            .Select(m => new RegisteredModelDto(m.Id, m.ProviderId, m.ModelId, m.Name, m.Quantization, m.ContextWindow, m.IsDefault))
            .ToListAsync(ct);
        return Ok(models);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RegisteredModelDto>> Get(Guid id, CancellationToken ct)
    {
        var m = await _db.RegisteredModels.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return NotFound();
        return Ok(new RegisteredModelDto(m.Id, m.ProviderId, m.ModelId, m.Name, m.Quantization, m.ContextWindow, m.IsDefault));
    }
}
