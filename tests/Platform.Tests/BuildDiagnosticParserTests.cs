using Platform.Web.Services.CodeIntelligence;
using Xunit;

namespace Platform.Tests;

public class BuildDiagnosticParserTests
{
    // This is real dotnet/csc diagnostic output format, not an invented shape.
    private const string SampleOutput = """
        Restore complete (0.4s)
        Build FAILED.

        src/Web/Controllers/FooController.cs(23,13): error CS0103: The name 'bar' does not exist in the current context [/repo/src/Web/Platform.Web.csproj]
        src/Web/Controllers/FooController.cs(30,9): warning CS0168: The variable 'x' is declared but never used [/repo/src/Web/Platform.Web.csproj]

            0 Warning(s)
            1 Error(s)
        """;

    [Fact]
    public void Parse_ExtractsRealDiagnosticFields()
    {
        var diagnostics = BuildDiagnosticParser.Parse(SampleOutput);

        Assert.Equal(2, diagnostics.Count);

        var error = diagnostics[0];
        Assert.Equal("src/Web/Controllers/FooController.cs", error.FilePath);
        Assert.Equal(23, error.Line);
        Assert.Equal(13, error.Column);
        Assert.Equal("error", error.Severity);
        Assert.Equal("CS0103", error.Code);
    }

    [Fact]
    public void FindFirstErrorFile_ReturnsErrorNotWarningFile()
    {
        var file = BuildDiagnosticParser.FindFirstErrorFile(SampleOutput);
        Assert.Equal("src/Web/Controllers/FooController.cs", file);
    }

    [Fact]
    public void FindFirstErrorFile_NoErrors_ReturnsNull()
    {
        var file = BuildDiagnosticParser.FindFirstErrorFile("Build succeeded.\n0 Warning(s)\n0 Error(s)");
        Assert.Null(file);
    }

    [Fact]
    public void FindAllErrorFiles_ReturnsDistinctFilesWithErrorsOnly()
    {
        const string multiFileOutput = """
            src/A.cs(1,1): error CS0103: bad thing [proj]
            src/A.cs(5,1): error CS0103: another bad thing in same file [proj]
            src/B.cs(2,2): error CS0246: missing type [proj]
            src/C.cs(3,3): warning CS0168: unused variable [proj]
            """;

        var files = BuildDiagnosticParser.FindAllErrorFiles(multiFileOutput);

        Assert.Equal(2, files.Count);
        Assert.Contains("src/A.cs", files);
        Assert.Contains("src/B.cs", files);
        Assert.DoesNotContain("src/C.cs", files); // warning only, not an error
    }
}
