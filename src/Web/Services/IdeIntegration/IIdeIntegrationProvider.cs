namespace Platform.Web.Services.IdeIntegration;

public record IdeCapabilities(bool SupportsStreaming, bool SupportsFileEdit, bool SupportsDiagnostics);

/// <summary>
/// Real extension point per §19. No concrete Cursor/VS Code/JetBrains
/// adapter exists — each of those needs that IDE's actual extension or
/// protocol surface implemented (VS Code: Language Server Protocol +
/// extension manifest + activation events; JetBrains: their Plugin SDK in
/// Kotlin/Java, a different language entirely; Cursor: an undocumented
/// superset of VS Code's protocol). None of that is buildable as a
/// same-session addition without the actual IDE's SDK to develop and test
/// against — attempting it here would mean writing protocol code with no
/// way to verify it against a real IDE, which is the "pretend it works"
/// failure mode this whole project has tried to avoid.
///
/// What IS real: a generic, IDE-agnostic local HTTP API
/// (GenericIdeController) that any editor/tool capable of making HTTP
/// requests can call today — this satisfies the spec's fallback
/// requirement ("support a generic local endpoint so compatible clients
/// can connect without a custom integration") without pretending to be a
/// specific IDE's native plugin.
/// </summary>
public interface IIdeIntegrationProvider
{
    string Name { get; }
    IdeCapabilities Capabilities { get; }
}

/// <summary>
/// The one real, working adapter: generic JSON-over-HTTP, no IDE-specific
/// protocol assumptions. Any client — a VS Code extension someone writes
/// later, a curl script, a JetBrains plugin — can drive the platform
/// through GenericIdeController using this shape.
/// </summary>
public class GenericHttpIdeProvider : IIdeIntegrationProvider
{
    public string Name => "GenericHttp";
    public IdeCapabilities Capabilities => new(SupportsStreaming: false, SupportsFileEdit: true, SupportsDiagnostics: true);
}
