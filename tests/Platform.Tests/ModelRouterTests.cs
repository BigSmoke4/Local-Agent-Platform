using Microsoft.EntityFrameworkCore;
using Platform.Web.Data;
using Platform.Web.Services.Routing;
using Xunit;

namespace Platform.Tests;

public class ModelRouterTests
{
    private static PlatformDbContext CreateInMemoryDb()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PlatformDbContext(options);
    }

    [Theory]
    [InlineData("25 * 48")]
    [InlineData("(10 - 4) / 2")]
    public void Classify_Arithmetic_IsTrivial(string input)
    {
        var router = new ModelRouter(CreateInMemoryDb());
        Assert.Equal(TaskComplexity.Trivial, router.Classify(input));
    }

    [Fact]
    public void Classify_ShortPlainRequest_IsSimple()
    {
        var router = new ModelRouter(CreateInMemoryDb());
        Assert.Equal(TaskComplexity.Simple, router.Classify("Summarize this file"));
    }

    [Fact]
    public void Classify_DebuggingKeyword_IsComplex()
    {
        var router = new ModelRouter(CreateInMemoryDb());
        Assert.Equal(TaskComplexity.Complex, router.Classify("Please debug why the auth service throws a 500"));
    }
}
