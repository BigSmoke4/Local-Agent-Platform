using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Models;
using Platform.Web.Services.Memory;

namespace Platform.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/memory")]
public class MemoryController : ControllerBase
{
    private readonly MemoryService _memory;

    public MemoryController(MemoryService memory)
    {
        _memory = memory;
    }

    public record StoreMemoryRequest(MemoryType Type, string Content, string Tags, Guid? AgentSessionId);

    [HttpPost]
    public async Task<IActionResult> Store(StoreMemoryRequest request, CancellationToken ct)
    {
        var memory = await _memory.StoreAsync(request.Type, request.Content, request.Tags, request.AgentSessionId, ct);
        return Ok(memory);
    }

    [HttpGet("retrieve")]
    public async Task<IActionResult> Retrieve([FromQuery] string query, [FromQuery] int max = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return BadRequest("query is required.");
        var results = await _memory.RetrieveAsync(query, max, ct);
        return Ok(results.Select(r => new { r.Memory, r.RelevanceScore, r.ScoringMethod }));
    }
}
