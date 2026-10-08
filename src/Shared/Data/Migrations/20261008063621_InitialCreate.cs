using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LocalAgentPlatform.Shared.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserRequest = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    ModelIdUsed = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    PlanJson = table.Column<string>(type: "text", nullable: true),
                    FinalSummary = table.Column<string>(type: "text", nullable: true),
                    MaxIterations = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    MaxDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IterationCount = table.Column<int>(type: "integer", nullable: false),
                    MaxRepairAttempts = table.Column<int>(type: "integer", nullable: false),
                    RepairAttemptCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    ModelId = table.Column<string>(type: "text", nullable: true),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CodeRelationships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromSymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToSymbolId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeRelationships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommandPermissionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExecutableName = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandPermissionRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MemoryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    EmbeddingModelId = table.Column<string>(type: "text", nullable: true),
                    EmbeddingJson = table.Column<string>(type: "text", nullable: true),
                    BaseImportance = table.Column<double>(type: "double precision", nullable: false),
                    SourceAgentSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAccessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AccessCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegisteredModels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelId = table.Column<string>(type: "text", nullable: false),
                    ProviderId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Quantization = table.Column<string>(type: "text", nullable: true),
                    ContextWindow = table.Column<int>(type: "integer", nullable: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegisteredModels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RepositoryIndexingJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    FilesScanned = table.Column<int>(type: "integer", nullable: false),
                    FilesChanged = table.Column<int>(type: "integer", nullable: false),
                    FilesDeleted = table.Column<int>(type: "integer", nullable: false),
                    SymbolsExtracted = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    QueuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepositoryIndexingJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    RiskLevel = table.Column<string>(type: "text", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    RegisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ToolExecutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToolName = table.Column<string>(type: "text", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceRootPath = table.Column<string>(type: "text", nullable: false),
                    ArgumentsJson = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    DecisionReason = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ExitCode = table.Column<int>(type: "integer", nullable: true),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToolExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    MfaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MfaSecretProtected = table.Column<string>(type: "text", nullable: true),
                    RecoveryCodeHash = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VerificationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepairAttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    BuildPassed = table.Column<bool>(type: "boolean", nullable: true),
                    BuildOutputSummary = table.Column<string>(type: "text", nullable: true),
                    CompilerErrorCount = table.Column<int>(type: "integer", nullable: true),
                    CompilerWarningCount = table.Column<int>(type: "integer", nullable: true),
                    TestsRan = table.Column<bool>(type: "boolean", nullable: true),
                    TestsPassed = table.Column<bool>(type: "boolean", nullable: true),
                    TestsTotal = table.Column<int>(type: "integer", nullable: true),
                    TestsFailed = table.Column<int>(type: "integer", nullable: true),
                    TestsSkipped = table.Column<int>(type: "integer", nullable: true),
                    TestOutputSummary = table.Column<string>(type: "text", nullable: true),
                    SecurityFindingCount = table.Column<int>(type: "integer", nullable: false),
                    SecurityFindingsJson = table.Column<string>(type: "text", nullable: true),
                    ReviewerVerdict = table.Column<string>(type: "text", nullable: true),
                    ReviewerReason = table.Column<string>(type: "text", nullable: true),
                    OverallResult = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentTaskNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StepKey = table.Column<string>(type: "text", nullable: false),
                    DependenciesJson = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    ToolName = table.Column<string>(type: "text", nullable: true),
                    ArgumentsJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTaskNodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTaskNodes_AgentSessions_AgentSessionId",
                        column: x => x.AgentSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TokenUsageRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    CachedTokens = table.Column<int>(type: "integer", nullable: false),
                    ContextWindowMax = table.Column<int>(type: "integer", nullable: false),
                    TokensPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    TimeToFirstToken = table.Column<TimeSpan>(type: "interval", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenUsageRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TokenUsageRecords_AgentSessions_AgentSessionId",
                        column: x => x.AgentSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Repositories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalPath = table.Column<string>(type: "text", nullable: false),
                    DefaultBranch = table.Column<string>(type: "text", nullable: true),
                    LastIndexedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repositories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Repositories_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FileSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelativePath = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    Language = table.Column<string>(type: "text", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    LastIndexedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileSnapshots_Repositories_RepositoryId",
                        column: x => x.RepositoryId,
                        principalTable: "Repositories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CodeSymbols",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileSnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ContainingNamespace = table.Column<string>(type: "text", nullable: true),
                    ContainingTypeName = table.Column<string>(type: "text", nullable: true),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    Signature = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeSymbols", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CodeSymbols_FileSnapshots_FileSnapshotId",
                        column: x => x.FileSnapshotId,
                        principalTable: "FileSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_OwnerUserId_CreatedAtUtc",
                table: "AgentSessions",
                columns: new[] { "OwnerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentSessions_State",
                table: "AgentSessions",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskNodes_AgentSessionId_OrderIndex",
                table: "AgentTaskNodes",
                columns: new[] { "AgentSessionId", "OrderIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTaskNodes_AgentSessionId_StepKey",
                table: "AgentTaskNodes",
                columns: new[] { "AgentSessionId", "StepKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogEntries_OccurredAtUtc",
                table: "AuditLogEntries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CodeRelationships_FromSymbolId",
                table: "CodeRelationships",
                column: "FromSymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeRelationships_ToSymbolId",
                table: "CodeRelationships",
                column: "ToSymbolId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeSymbols_FileSnapshotId",
                table: "CodeSymbols",
                column: "FileSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CodeSymbols_RepositoryId_Name",
                table: "CodeSymbols",
                columns: new[] { "RepositoryId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CommandPermissionRules_OwnerUserId_ExecutableName",
                table: "CommandPermissionRules",
                columns: new[] { "OwnerUserId", "ExecutableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileSnapshots_ContentHash",
                table: "FileSnapshots",
                column: "ContentHash");

            migrationBuilder.CreateIndex(
                name: "IX_FileSnapshots_RepositoryId_RelativePath",
                table: "FileSnapshots",
                columns: new[] { "RepositoryId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntries_CreatedAtUtc",
                table: "MemoryEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MemoryEntries_OwnerUserId_RepositoryId_Scope",
                table: "MemoryEntries",
                columns: new[] { "OwnerUserId", "RepositoryId", "Scope" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_OwnerUserId_Name",
                table: "Projects",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegisteredModels_ProviderId_ModelId",
                table: "RegisteredModels",
                columns: new[] { "ProviderId", "ModelId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_LocalPath",
                table: "Repositories",
                column: "LocalPath");

            migrationBuilder.CreateIndex(
                name: "IX_Repositories_ProjectId",
                table: "Repositories",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_RepositoryIndexingJobs_RepositoryId_QueuedAtUtc",
                table: "RepositoryIndexingJobs",
                columns: new[] { "RepositoryId", "QueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageRecords_AgentSessionId",
                table: "TokenUsageRecords",
                column: "AgentSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ToolDefinitions_Name",
                table: "ToolDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutions_OwnerUserId_RequestedAtUtc",
                table: "ToolExecutions",
                columns: new[] { "OwnerUserId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutions_RequestedAtUtc",
                table: "ToolExecutions",
                column: "RequestedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ToolExecutions_ToolName",
                table: "ToolExecutions",
                column: "ToolName");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Role",
                table: "Users",
                column: "Role");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VerificationRuns_AgentSessionId_RepairAttemptNumber",
                table: "VerificationRuns",
                columns: new[] { "AgentSessionId", "RepairAttemptNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTaskNodes");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "AuditLogEntries");

            migrationBuilder.DropTable(
                name: "CodeRelationships");

            migrationBuilder.DropTable(
                name: "CodeSymbols");

            migrationBuilder.DropTable(
                name: "CommandPermissionRules");

            migrationBuilder.DropTable(
                name: "MemoryEntries");

            migrationBuilder.DropTable(
                name: "RegisteredModels");

            migrationBuilder.DropTable(
                name: "RepositoryIndexingJobs");

            migrationBuilder.DropTable(
                name: "TokenUsageRecords");

            migrationBuilder.DropTable(
                name: "ToolDefinitions");

            migrationBuilder.DropTable(
                name: "ToolExecutions");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "VerificationRuns");

            migrationBuilder.DropTable(
                name: "FileSnapshots");

            migrationBuilder.DropTable(
                name: "AgentSessions");

            migrationBuilder.DropTable(
                name: "Repositories");

            migrationBuilder.DropTable(
                name: "Projects");
        }
    }
}
