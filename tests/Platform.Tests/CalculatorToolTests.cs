using Platform.Web.Services.Tools;
using Xunit;

namespace Platform.Tests;

public class CalculatorToolTests
{
    private readonly CalculatorTool _tool = new();

    [Theory]
    [InlineData("2 + 2", 4)]
    [InlineData("25 * 48", 1200)]
    [InlineData("(10 - 4) / 2", 3)]
    public void Evaluate_ReturnsCorrectResult(string expression, double expected)
    {
        var result = _tool.Evaluate(expression);
        Assert.Equal(expected, result);
    }
}
