namespace LocalAgentPlatform.Modules.Projects.Domain;

/// <summary>Extension point for Project/Repository domain rules (workspace membership,
/// branch metadata, project-level policy). Entities live in Shared.Data for now.
/// Repository *analysis* (scanning/hashing/Roslyn) is implemented separately in the
/// RepositoryAnalysis module — see docs/STATUS.md.</summary>
public static class ModuleMarker
{
    public const string Status = "Not yet implemented — Project workspace domain rules (Section 36).";
}
