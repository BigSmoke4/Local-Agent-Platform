using System.Linq;
using Platform.Web.Services.Lsp;
using Xunit;

namespace Platform.Tests;

public class LspFramingTests
{
    [Fact]
    public void Frame_ProducesRealContentLengthHeader()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"id\":1}";
        var framed = LspFraming.Frame(json);
        var text = System.Text.Encoding.UTF8.GetString(framed);

        var expectedByteLength = System.Text.Encoding.UTF8.GetByteCount(json);
        Assert.StartsWith($"Content-Length: {expectedByteLength}\r\n\r\n", text);
        Assert.EndsWith(json, text);
    }

    [Fact]
    public async Task ReadMessageAsync_RoundTripsARealFramedMessage()
    {
        var json = "{\"jsonrpc\":\"2.0\",\"method\":\"initialize\",\"id\":1}";
        var framed = LspFraming.Frame(json);

        using var stream = new MemoryStream(framed);
        var result = await LspFraming.ReadMessageAsync(stream);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadMessageAsync_HandlesMultiByteUtf8ContentLengthCorrectly()
    {
        // "café" has a multi-byte UTF-8 character — Content-Length must be
        // the real byte count, not the character count, or this would fail.
        var json = "{\"text\":\"café\"}";
        var framed = LspFraming.Frame(json);

        using var stream = new MemoryStream(framed);
        var result = await LspFraming.ReadMessageAsync(stream);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadMessageAsync_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream(Array.Empty<byte>());
        var result = await LspFraming.ReadMessageAsync(stream);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMessageAsync_TwoConsecutiveMessages_ReadsBothCorrectly()
    {
        var first = LspFraming.Frame("{\"a\":1}");
        var second = LspFraming.Frame("{\"b\":2}");
        var combined = first.Concat(second).ToArray();

        using var stream = new MemoryStream(combined);
        var firstResult = await LspFraming.ReadMessageAsync(stream);
        var secondResult = await LspFraming.ReadMessageAsync(stream);

        Assert.Equal("{\"a\":1}", firstResult);
        Assert.Equal("{\"b\":2}", secondResult);
    }
}
