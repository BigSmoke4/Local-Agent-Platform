using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services.CodeIntelligence;

namespace Platform.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/code-intelligence")]
public class CodeIntelligenceController : ControllerBase
{
    private readonly RepositoryIndexService _indexService;
    private readonly PlatformDbContext _db;
    private readonly SemanticCodeGraphService _semantic;

    public CodeIntelligenceController(RepositoryIndexService indexService, PlatformDbContext db, SemanticCodeGraphService semantic)
    {
        _indexService = indexService;
        _db = db;
        _semantic = semantic;
    }

    [HttpPost("index")]
    public async Task<IActionResult> RunIndex(CancellationToken ct)
    {
        var result = await _indexService.RunAsync(ct);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "RepositoryIndexService.Run",
            Details = $"scanned={result.FilesScanned} reindexed={result.FilesReindexed} skipped={result.FilesSkippedUnchanged} symbols={result.SymbolsIndexed}"
        });
        await _db.SaveChangesAsync(ct);
        return Ok(result);
    }

    [HttpGet("symbols")]
    public async Task<IActionResult> FindSymbols([FromQuery] string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return BadRequest("name is required.");

        var results = await _db.CodeSymbols
            .Where(s => EF.Functions.ILike(s.SymbolName, $"%{name}%"))
            .OrderBy(s => s.FilePath).ThenBy(s => s.StartLine)
            .Take(200)
            .ToListAsync(ct);

        return Ok(results);
    }

    [HttpGet("symbols/by-file")]
    public async Task<IActionResult> SymbolsByFile([FromQuery] string path, CancellationToken ct)
        => Ok(await _db.CodeSymbols
            .Where(s => s.FilePath == path)
            .OrderBy(s => s.StartLine)
            .ToListAsync(ct));

    // ---- Real semantic (MSBuildWorkspace-backed) endpoints ----

    [HttpPost("semantic/load")]
    public async Task<IActionResult> LoadSemantic([FromQuery] string path, CancellationToken ct)
    {
        var result = await _semantic.LoadSolutionAsync(path, ct);
        _db.AuditLogs.Add(new AuditLog
        {
            Action = "SemanticCodeGraphService.Load",
            Details = result.Succeeded
                ? $"projects={result.ProjectsLoaded} documents={result.DocumentsLoaded} warnings={result.LoadWarnings.Count}"
                : $"FAILED: {result.Error}"
        });
        await _db.SaveChangesAsync(ct);
        return result.Succeeded ? Ok(result) : UnprocessableEntity(result);
    }

    [HttpGet("semantic/type")]
    public async Task<IActionResult> FindType([FromQuery] string name, CancellationToken ct)
    {
        try
        {
            return Ok(await _semantic.FindTypeAsync(name, ct));
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(ex.Message);
        }
    }

    [HttpGet("semantic/references")]
    public async Task<IActionResult> FindReferences([FromQuery] string name, CancellationToken ct)
    {
        try
        {
            return Ok(await _semantic.FindReferencesAsync(name, ct));
        }
        catch (InvalidOperationException ex)
        {
            return UnprocessableEntity(ex.Message);
        }
    }
}
