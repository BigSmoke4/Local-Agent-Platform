using LocalAgentPlatform.Modules.Models.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace LocalAgentPlatform.Web.Controllers;

public class ModelManagerController : Controller
{
    private readonly ModelManagerAppService _appService;

    public ModelManagerController(ModelManagerAppService appService)
    {
        _appService = appService;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var vm = await _appService.BuildViewDataAsync(ct);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string runtimeModelId, CancellationToken ct)
    {
        var vm = await _appService.BuildViewDataAsync(ct);
        var descriptor = vm.UnregisteredRuntimeModels.FirstOrDefault(m => m.Id == runtimeModelId);
        if (descriptor is not null)
        {
            await _appService.RegisterFromRuntimeAsync(descriptor, ct);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        await _appService.SetDefaultAsync(id, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _appService.DeleteAsync(id, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Load(string modelId, CancellationToken ct)
    {
        var result = await _appService.LoadAsync(modelId, ct);
        TempData["LoadResult"] = result.Success
            ? $"Loaded {modelId} in {result.LoadDuration.TotalSeconds:0.0}s"
            : $"Failed to load {modelId}: {result.Message}";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unload(string modelId, CancellationToken ct)
    {
        await _appService.UnloadAsync(modelId, ct);
        TempData["LoadResult"] = $"Unload requested for {modelId} (see server logs — Ollama's API has no unload endpoint yet).";
        return RedirectToAction(nameof(Index));
    }
}
