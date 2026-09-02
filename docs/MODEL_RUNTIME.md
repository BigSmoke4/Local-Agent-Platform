# Model Runtime

## Abstraction

`IModelProvider` (`Services/IModelProvider.cs`) is the only interface
controllers/services depend on:

```csharp
Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken ct);
IAsyncEnumerable<string> StreamAsync(GenerationRequest request, CancellationToken ct);
Task<ModelHealth> CheckHealthAsync(CancellationToken ct);
Task<IReadOnlyList<string>> ListAvailableModelsAsync(CancellationToken ct);
```

## Concrete adapter: Ollama

`OllamaModelProvider` implements `IModelProvider` over Ollama's real HTTP
API (`/api/generate`, `/api/tags`). Registered via
`AddHttpClient<IModelProvider, OllamaModelProvider>` in `Program.cs`,
pointed at `ModelRuntime:OllamaBaseUrl` (default
`http://localhost:11434/`).

Token counts and tokens/sec come from Ollama's real response fields
(`prompt_eval_count`, `eval_count`, `eval_duration`) — not estimated or
fabricated.

## Swapping runtimes

To add e.g. an `llama.cpp` server adapter: implement `IModelProvider`
against its HTTP/gRPC surface, then change the `AddHttpClient<IModelProvider,
YourAdapter>` line in `Program.cs`. No controller or service code
references `OllamaModelProvider` directly — they all take `IModelProvider`.

## Model registry vs. model runtime

`ModelDescriptor` (EF Core entity, `POST /api/models`) is just metadata —
name, context window, quantization label, which runtime id to use. It does
not itself load or manage the model process; that's entirely Ollama's job
(`ollama pull`, `ollama serve`). This platform does not manage model
downloads or GPU/quantization selection — it assumes Ollama is already
running with the model pulled.

## Not implemented

- Direct `llama.cpp`/ONNX Runtime adapters (interface supports them; no
  concrete implementation exists)
- Model load/unload lifecycle management (`/api/models/{id}/load` from the
  spec's suggested endpoint list is not implemented — Ollama manages its
  own model loading)
- Streaming is implemented in `OllamaModelProvider.StreamAsync` but not yet
  wired into any controller endpoint (no `POST /api/agent/run/stream`)
