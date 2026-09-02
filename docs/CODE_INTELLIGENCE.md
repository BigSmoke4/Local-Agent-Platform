# Code Intelligence

Two distinct mechanisms exist, deliberately not conflated:

## 1. `SearchSymbolTool` — text-based, real but not semantic

Regex-matches lines shaped like declarations (`class Foo`, `public void
Bar(`, etc.) against source files. This is a genuine, working full-text
symbol search — but it has no understanding of scope, inheritance, or
overloads. Searching for `Save` will match every method named `Save` in
every unrelated class.

## 2. `RoslynSyntaxIndexer` + `RepositoryIndexService` — real AST, syntax-level

Uses `Microsoft.CodeAnalysis.CSharp` (`CSharpSyntaxTree.ParseText`) to
actually parse each `.cs` file and extract real declarations
(class/interface/struct/record/enum/method/property/constructor) with:

- Real line spans (`SyntaxTree.GetLineSpan`)
- Real containing-type tracking (a `Stack<string>` pushed/popped as the
  walker descends into type declarations)
- Real namespace tracking (both classic and file-scoped namespace syntax)

Persisted to `CodeSymbol` rows via `RepositoryIndexService.RunAsync`, which
is genuinely incremental: each file's SHA-256 content hash is compared
against what's stored, and files whose hash hasn't changed are skipped
entirely rather than re-parsed (§60).

### The honest limitation

This is **syntax-level analysis only** — it parses one file's text into a
tree with no knowledge of other files, references, or the compiler's
actual type-resolution rules. It cannot answer:

- "What does this method override?"
- "Which overload does this call resolve to?"
- "What implementations exist of this interface across the solution?"
- Anything requiring semantic binding across files/assemblies

A real semantic graph needs `Microsoft.CodeAnalysis.MSBuild` +
`MSBuildWorkspace.OpenSolutionAsync`, which requires:

- MSBuild locator/resolution (handling multiple installed SDKs)
- Full NuGet restore awareness
- Actual project-to-project reference graph construction
- Meaningfully more error handling (a workspace can partially fail to load)

That is a materially larger, separate piece of engineering — not attempted
here, and not faked with a syntax-only index dressed up as more than it is.

## 3. `SemanticCodeGraphService` — real cross-file semantic analysis (MSBuildWorkspace)

Uses `Microsoft.CodeAnalysis.MSBuild` (`MSBuildWorkspace`) plus
`Microsoft.Build.Locator` — the actual mechanism a full semantic index
needs. `POST /api/code-intelligence/semantic/load?path=...` loads a real
`.sln` or `.csproj` into a Roslyn `Compilation`. Once loaded:

- `GET /api/code-intelligence/semantic/type?name=X` — real semantic type
  lookup: actual resolved base type and actual resolved implemented
  interfaces (`INamedTypeSymbol.BaseType`, `.AllInterfaces`), not
  text-guessed
- `GET /api/code-intelligence/semantic/references?name=X` — real
  "find all references" across the loaded solution via Roslyn's
  `SymbolFinder.FindReferencesAsync`

### Environment requirement, stated honestly

`MSBuildWorkspace` needs a real MSBuild installation resolvable via
`Microsoft.Build.Locator` — in practice, the same .NET SDK you use to
`dotnet build` this repo. `Program.cs` calls `MSBuildLocator.RegisterDefaults()`
at startup; if no SDK is found, that registration fails and is logged, but
the rest of the app still starts (this service isn't a hard dependency of
anything else). If you call `/semantic/load` without a working MSBuild
registration, you get back `{"succeeded": false, "error": "..."}` with the
real exception message — never a fabricated success.

This is what makes the difference from `RoslynSyntaxIndexer` real: base
types/interfaces here are what the compiler actually resolved after
loading real project references and restored packages, not a syntax-only
guess.
