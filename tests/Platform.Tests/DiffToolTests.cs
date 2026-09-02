using Platform.Web.Services.Tools;
using Xunit;

namespace Platform.Tests;

public class DiffToolTests
{
    private readonly DiffTool _tool = new();

    [Fact]
    public void Compute_DetectsAddedAndRemovedLines()
    {
        var oldText = "line1\nline2\nline3";
        var newText = "line1\nline2-changed\nline3\nline4";

        var result = _tool.Compute(oldText, newText);

        Assert.True(result.LinesAdded >= 1);
        Assert.True(result.LinesRemoved >= 1);
        Assert.Contains(result.Lines, l => l.Type == "Added" && l.Text == "line4");
    }

    [Fact]
    public void Compute_IdenticalText_HasNoChanges()
    {
        var result = _tool.Compute("same\ntext", "same\ntext");
        Assert.Equal(0, result.LinesAdded);
        Assert.Equal(0, result.LinesRemoved);
    }
}
