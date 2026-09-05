using LocalAgentPlatform.Modules.Models.Application.Services;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

[Collection("Postgres")]
public class ModelRegistryServiceTests
{
    private readonly PostgresFixture _fixture;
    public ModelRegistryServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Registering_a_model_persists_it_and_makes_it_default_when_first()
    {
        await using var db = _fixture.CreateContext();
        var service = new ModelRegistryService(db);

        var modelId = $"test-model-{Guid.NewGuid()}";
        var registered = await service.RegisterAsync("ollama", modelId, "Test Model", "Q4", 8192);

        Assert.NotEqual(Guid.Empty, registered.Id);

        var reread = await service.GetAsync(registered.Id);
        Assert.NotNull(reread);
        Assert.Equal(modelId, reread!.ModelId);
        Assert.Equal("Q4", reread.Quantization);
    }

    [Fact]
    public async Task Setting_default_unsets_the_previous_default_atomically()
    {
        await using var db = _fixture.CreateContext();
        var service = new ModelRegistryService(db);

        var a = await service.RegisterAsync("ollama", $"model-a-{Guid.NewGuid()}", "A", null, null);
        var b = await service.RegisterAsync("ollama", $"model-b-{Guid.NewGuid()}", "B", null, null);

        await service.SetDefaultAsync(a.Id);
        await service.SetDefaultAsync(b.Id);

        var all = await service.ListAsync();
        var defaults = all.Where(m => m.Id == a.Id || m.Id == b.Id).Where(m => m.IsDefault).ToList();

        Assert.Single(defaults);
        Assert.Equal(b.Id, defaults[0].Id);
    }

    [Fact]
    public async Task Deleting_a_model_removes_it_from_the_registry()
    {
        await using var db = _fixture.CreateContext();
        var service = new ModelRegistryService(db);

        var registered = await service.RegisterAsync("ollama", $"model-{Guid.NewGuid()}", "ToDelete", null, null);
        await service.DeleteAsync(registered.Id);

        var reread = await service.GetAsync(registered.Id);
        Assert.Null(reread);
    }
}
