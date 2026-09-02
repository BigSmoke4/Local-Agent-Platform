using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Services.CodeIntelligence;
using Platform.Web.Services.IdeIntegration;
using Platform.Web.Services.Tools;

namespace Platform.Web.Controllers;

/// <summary>
/// Real, working, IDE-agnostic integration surface. Any editor/tool that
/// can make HTTP requests can use this today — no IDE-specific extension
/// needed. Bundles the operations an editor integration would realistically
/// need (open-file diagnostics, symbol lookup, safe edit, build/test) behind
/// one documented, versioned surface instead of scattering them across the
/// other controllers with less predictable shapes.
/// </summary>
[Authorize]
[ApiController]
[Route("api/ide")]
public class GenericIdeController : ControllerBase
{
    private readonly IIdeIntegrationProvider _provider;
    private readonly ProjectStructureTool _structure;
    private readonly SearchSymbolTool _search;
    private readonly SafeFileEditService _safeEdit;
    private readonly BuildTool _build;

    public GenericIdeController(
        IIdeIntegrationProvider provider,
        ProjectStructureTool structure,
        SearchSymbolTool search,
        SafeFileEditService safeEdit,
        BuildTool build)
    {
        _provider = provider;
        _structure = structure;
        _search = search;
        _safeEdit = safeEdit;
        _build = build;
    }

    [HttpGet("capabilities")]
    public IActionResult Capabilities() => Ok(new { provider = _provider.Name, capabilities = _provider.Capabilities });

    [HttpGet("workspace")]
    public IActionResult Workspace() => Ok(_structure.Scan());

    [HttpGet("symbols")]
    public async Task<IActionResult> Symbols([FromQuery] string name, CancellationToken ct)
        => Ok(await _search.FindAsync(name, ct));

    public record EditRequest(string Path, string Content);

    [HttpPost("edit")]
    public async Task<IActionResult> Edit(EditRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await _safeEdit.ApplyAsync(null, request.Path, request.Content, ct);
            return Ok(outcome);
        }
        catch (FileWriteToolException ex)
        {
            return Conflict(ex.Message);
        }
    }

    [HttpPost("diagnostics")]
    public async Task<IActionResult> Diagnostics([FromQuery] string? project, CancellationToken ct)
    {
        var result = await _build.RunAsync(project, ct);
        var diagnostics = BuildDiagnosticParser.Parse(result.RawOutput);
        return Ok(new { result.Succeeded, diagnostics });
    }
}
