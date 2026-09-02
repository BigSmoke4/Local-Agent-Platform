using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services;

namespace Platform.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/models")]
public class ModelsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly IModelProvider _modelProvider;
    private readonly ILogger<ModelsController> _logger;

    public ModelsController(PlatformDbContext db, IModelProvider modelProvider, ILogger<ModelsController> logger)
    {
        _db = db;
        _modelProvider = modelProvider;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ModelDescriptor>>> List(CancellationToken ct)
        => Ok(await _db.Models.OrderByDescending(m => m.RegisteredAtUtc).ToListAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ModelDescriptor>> Get(Guid id, CancellationToken ct)
    {
        var model = await _db.Models.FindAsync(new object[] { id }, ct);
        return model is null ? NotFound() : Ok(model);
    }

    public record RegisterModelRequest(string Name, string RuntimeId, int ContextWindow, string? Quantization);

    [HttpPost]
    public async Task<ActionResult<ModelDescriptor>> Register(RegisterModelRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RuntimeId))
            return BadRequest("Name and RuntimeId are required.");

        var exists = await _db.Models.AnyAsync(m => m.RuntimeId == request.RuntimeId, ct);
        if (exists)
            return Conflict($"A model with runtime id '{request.RuntimeId}' is already registered.");

        var model = new ModelDescriptor
        {
            Name = request.Name,
            RuntimeId = request.RuntimeId,
            ContextWindow = request.ContextWindow > 0 ? request.ContextWindow : 4096,
            Quantization = request.Quantization,
            IsDefault = !await _db.Models.AnyAsync(ct)
        };

        _db.Models.Add(model);
        _db.AuditLogs.Add(new AuditLog { Action = "ModelRegistered", Details = model.RuntimeId });
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Registered model {Name} ({RuntimeId})", model.Name, model.RuntimeId);
        return CreatedAtAction(nameof(Get), new { id = model.Id }, model);
    }

    [HttpPost("{id:guid}/set-default")]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        var model = await _db.Models.FindAsync(new object[] { id }, ct);
        if (model is null) return NotFound();

        var current = await _db.Models.Where(m => m.IsDefault).ToListAsync(ct);
        foreach (var m in current) m.IsDefault = false;
        model.IsDefault = true;

        _db.AuditLogs.Add(new AuditLog { Action = "ModelSetDefault", Details = model.RuntimeId });
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("runtime-status")]
    public async Task<IActionResult> RuntimeStatus(CancellationToken ct)
    {
        var health = await _modelProvider.CheckHealthAsync(ct);
        var available = health.IsHealthy
            ? await _modelProvider.ListAvailableModelsAsync(ct)
            : Array.Empty<string>();

        return Ok(new { healthy = health.IsHealthy, message = health.Message, availableModels = available });
    }
}
