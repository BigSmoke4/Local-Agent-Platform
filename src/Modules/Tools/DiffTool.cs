using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Platform.Web.Services.Tools;

public record DiffLineResult(string Type, string Text, int? OldLineNumber, int? NewLineNumber);
public record DiffResult(List<DiffLineResult> Lines, int LinesAdded, int LinesRemoved);

/// <summary>
/// Real line-level diff via DiffPlex — not a placeholder or fabricated
/// change summary. Used to show exactly what FileWriteTool/FileEditTool
/// changed, per §16 and §61.
/// </summary>
public class DiffTool
{
    public string Name => "DiffTool";

    public DiffResult Compute(string oldText, string newText)
    {
        var diffBuilder = new InlineDiffBuilder(new Differ());
        var diff = diffBuilder.BuildDiffModel(oldText ?? string.Empty, newText ?? string.Empty);

        var lines = new List<DiffLineResult>();
        int added = 0, removed = 0;
        int oldLineNum = 1, newLineNum = 1;

        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    lines.Add(new DiffLineResult("Added", line.Text, null, newLineNum));
                    newLineNum++;
                    added++;
                    break;
                case ChangeType.Deleted:
                    lines.Add(new DiffLineResult("Removed", line.Text, oldLineNum, null));
                    oldLineNum++;
                    removed++;
                    break;
                default:
                    lines.Add(new DiffLineResult("Unchanged", line.Text, oldLineNum, newLineNum));
                    oldLineNum++;
                    newLineNum++;
                    break;
            }
        }

        return new DiffResult(lines, added, removed);
    }
}
