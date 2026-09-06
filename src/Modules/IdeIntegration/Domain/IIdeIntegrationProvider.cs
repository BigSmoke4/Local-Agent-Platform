namespace LocalAgentPlatform.Modules.IdeIntegration.Domain;

/// <summary>
/// Extension point for editor-specific integrations (spec Section 19). NO concrete
/// implementation of this interface exists yet — building a real Cursor, VS Code,
/// JetBrains, or Antigravity adapter requires that editor's actual documented
/// extension/protocol surface, which this project cannot fabricate or claim to
/// support without it. See docs/STATUS.md.
/// <para/>
/// What DOES exist today and satisfies the spec's fallback requirement ("support a
/// generic local endpoint so compatible clients can connect without a custom
/// integration"): the plain JSON REST API under <c>/api/*</c> (see
/// Controllers/Api/*.cs) plus the OpenAPI document at <c>/swagger</c>. Any editor or
/// tool that can make local HTTP calls can already drive this platform through that
/// API — it just isn't a purpose-built adapter for a specific IDE's UI.
/// </summary>
public interface IIdeIntegrationProvider
{
    string IdeName { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
