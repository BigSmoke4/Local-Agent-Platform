namespace Platform.Web.Models;

public class CodeSymbol
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FilePath { get; set; } = string.Empty;

    public string SymbolName { get; set; } = string.Empty;

    /// <summary>Class, Interface, Struct, Record, Enum, Method, Property, Constructor.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? ContainingType { get; set; }

    public string? Namespace { get; set; }

    public int StartLine { get; set; }

    public int EndLine { get; set; }

    /// <summary>Hash of the file's content at the time this symbol was indexed,
    /// used for incremental re-indexing per §60 (only re-parse changed files).</summary>
    public string FileContentHash { get; set; } = string.Empty;

    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;
}
