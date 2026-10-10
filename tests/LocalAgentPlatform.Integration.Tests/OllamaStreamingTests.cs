using System.Net;
using System.Text;
using LocalAgentPlatform.Modules.Models.Infrastructure.Ollama;
using LocalAgentPlatform.Shared.Kernel.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalAgentPlatform.Integration.Tests;

public sealed class OllamaStreamingTests
{
    [Fact]
    public async Task Rejects_a_stream_that_ends_without_a_completion_frame()
    {
        var handler = new FixedResponseHandler("{\"response\":\"partial text\",\"done\":false}\n");
        using var http = new HttpClient(handler);
        var provider = new OllamaModelProvider(
            http,
            Options.Create(new OllamaOptions { BaseUrl = "http://ollama.test", RequestTimeoutSeconds = 10 }),
            NullLogger<OllamaModelProvider>.Instance);
        var request = new ModelGenerationRequest("test-model", "prompt");

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in provider.GenerateStreamAsync(request)) { }
        });
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly string _body;
        public FixedResponseHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/x-ndjson")
            };
            return Task.FromResult(response);
        }
    }
}
