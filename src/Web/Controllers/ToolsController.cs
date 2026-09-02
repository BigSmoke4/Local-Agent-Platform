using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Data;
using Platform.Web.Models;
using Platform.Web.Services.Tools;

namespace Platform.Web.Controllers;

/// <summary>
/// Direct tool invocation endpoints, independent of the agent loop, so tools
/// can be exercised and audited on their own. Every call is logged to
/// AuditLogs — nothing here is simulated.
/// </summary>
[Authorize]
[ApiController]
[Route("api/tools")]
public class ToolsController : ControllerBase
{
    private readonly PlatformDbContext _db;
    private readonly GitTool _git;
    private readonly BuildTool _build;
    private readonly TestTool _test;
    private readonly TerminalTool _terminal;
    private readonly FileReadTool _fileRead;
    private readonly ProjectStructureTool _projectStructure;
    private readonly SearchSymbolTool _searchSymbol;
    private readonly DependencyAnalysisTool _dependencyAnalysis;
    private readonly SafeFileEditService _safeEdit;

    public ToolsController(
        PlatformDbContext db,
        GitTool git,
        BuildTool build,
        TestTool test,
        TerminalTool terminal,
        FileReadTool fileRead,
        ProjectStructureTool projectStructure,
        SearchSymbolTool searchSymbol,
        DependencyAnalysisTool dependencyAnalysis,
        SafeFileEditService safeEdit)
    {
        _db = db;
        _git = git;
        _build = build;
        _test = test;
        _terminal = terminal;
        _fileRead = fileRead;
        _projectStructure = projectStructure;
        _searchSymbol = searchSymbol;
        _dependencyAnalysis = dependencyAnalysis;
        _safeEdit = safeEdit;
    }

    [HttpGet("git/status")]
    public async Task<IActionResult> GitStatus(CancellationToken ct)
        => Ok(await AuditAndReturn("GitTool.Status", await _git.StatusAsync(ct), ct));

    [HttpGet("git/diff")]
    public async Task<IActionResult> GitDiff([FromQuery] string? path, CancellationToken ct)
        => Ok(await AuditAndReturn("GitTool.Diff", await _git.DiffAsync(path, ct), ct));

    [HttpGet("git/log")]
    public async Task<IActionResult> GitLog([FromQuery] int max = 20, CancellationToken ct = default)
        => Ok(await AuditAndReturn("GitTool.Log", await _git.LogAsync(max, ct), ct));

    public record CheckpointRequest(string Message);

    [HttpPost("git/checkpoint")]
    public async Task<IActionResult> GitCheckpoint(CheckpointRequest request, CancellationToken ct)
        => Ok(await AuditAndReturn("GitTool.Checkpoint", await _git.CreateCheckpointAsync(request.Message, ct), ct));

    [HttpPost("build")]
    public async Task<IActionResult> Build([FromQuery] string? project, CancellationToken ct)
    {
        var result = await _build.RunAsync(project, ct);
        await LogAudit("BuildTool.Run", $"succeeded={result.Succeeded} errors={result.ErrorCount} warnings={result.WarningCount}", ct);
        return Ok(result);
    }

    [HttpPost("test")]
    public async Task<IActionResult> Test([FromQuery] string? project, CancellationToken ct)
    {
        var result = await _test.RunAsync(project, ct);
        await LogAudit("TestTool.Run", $"succeeded={result.Succeeded} passed={result.Passed} failed={result.Failed} skipped={result.Skipped}", ct);
        return Ok(result);
    }

    public record TerminalRequest(string Command, bool Approved);

    [HttpPost("terminal")]
    public async Task<IActionResult> Terminal(TerminalRequest request, CancellationToken ct)
    {
        var result = await _terminal.ExecuteAsync(request.Command, request.Approved, ct);
        await LogAudit("TerminalTool.Execute", $"command={request.Command} decision={result.Decision}", ct);
        return Ok(result);
    }

    [HttpGet("file/read")]
    public async Task<IActionResult> ReadFile([FromQuery] string path, CancellationToken ct)
    {
        try
        {
            var content = await _fileRead.ReadAsync(path, ct);
            await LogAudit("FileReadTool.Read", path, ct);
            return Ok(new { path, content });
        }
        catch (FileReadToolException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("structure")]
    public IActionResult Structure([FromQuery] int maxDepth = 6)
        => Ok(_projectStructure.Scan(maxDepth));

    [HttpGet("search-symbol")]
    public async Task<IActionResult> SearchSymbol([FromQuery] string name, CancellationToken ct)
        => Ok(await _searchSymbol.FindAsync(name, ct));

    [HttpGet("dependencies")]
    public async Task<IActionResult> Dependencies(CancellationToken ct)
        => Ok(await _dependencyAnalysis.AnalyzeAsync(ct));

    public record EditFileRequest(string Path, string Content, Guid? AgentSessionId);

    [HttpPost("file/edit")]
    public async Task<IActionResult> EditFile(EditFileRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await _safeEdit.ApplyAsync(request.AgentSessionId, request.Path, request.Content, ct);
            await LogAudit("SafeFileEditService.Apply", $"{request.Path} +{outcome.Diff.LinesAdded}/-{outcome.Diff.LinesRemoved}", ct);
            return Ok(outcome);
        }
        catch (FileWriteToolException ex)
        {
            return Conflict(ex.Message);
        }
    }

    public record RollbackRequest(Guid SnapshotId);

    [HttpPost("file/rollback")]
    public async Task<IActionResult> Rollback(RollbackRequest request, CancellationToken ct)
    {
        var success = await _safeEdit.RollbackAsync(request.SnapshotId, ct);
        await LogAudit("SafeFileEditService.Rollback", request.SnapshotId.ToString(), ct);
        return success ? Ok() : NotFound();
    }

    private async Task<T> AuditAndReturn<T>(string action, T result, CancellationToken ct)
    {
        await LogAudit(action, result?.ToString(), ct);
        return result;
    }

    private async Task LogAudit(string action, string? details, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog { Action = action, Details = details });
        await _db.SaveChangesAsync(ct);
    }
}
