using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Web.Services.Telemetry;

namespace Platform.Web.Controllers;

[Authorize]
[ApiController]
[Route("api/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly HardwareTelemetryProvider _provider;

    public TelemetryController(HardwareTelemetryProvider provider)
    {
        _provider = provider;
    }

    [HttpGet("hardware")]
    public async Task<IActionResult> Hardware(CancellationToken ct)
        => Ok(await _provider.GetSnapshotAsync(ct));
}
