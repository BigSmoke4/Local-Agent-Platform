using System.Text;

namespace Platform.Web.Services.Lsp;

/// <summary>
/// Real implementation of the Language Server Protocol's wire framing —
/// the actual "Content-Length: N\r\n\r\n{json}" format specified at
/// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#baseProtocol.
/// This is a real, documented, public protocol (unlike Cursor's internals
/// or JetBrains' proprietary plugin SDK) — VS Code, Neovim, Sublime,
/// Emacs, and many other editors can all speak LSP through a generic
/// client, so implementing this correctly gives genuine multi-editor reach
/// without needing any single vendor's SDK to build or verify against.
/// </summary>
public static class LspFraming
{
    /// <summary>Frames a JSON-RPC payload with the real LSP Content-Length header.</summary>
    public static byte[] Frame(string jsonPayload)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(jsonPayload);
        var header = $"Content-Length: {bodyBytes.Length}\r\n\r\n";
        var headerBytes = Encoding.UTF8.GetBytes(header);

        var result = new byte[headerBytes.Length + bodyBytes.Length];
        Buffer.BlockCopy(headerBytes, 0, result, 0, headerBytes.Length);
        Buffer.BlockCopy(bodyBytes, 0, result, headerBytes.Length, bodyBytes.Length);
        return result;
    }

    /// <summary>
    /// Reads one real LSP-framed message from the stream: parses the
    /// Content-Length header, then reads exactly that many body bytes.
    /// Returns null at end of stream. Throws on a malformed header rather
    /// than guessing a length.
    /// </summary>
    public static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        int? contentLength = null;
        var headerLine = new StringBuilder();

        while (true)
        {
            var b = stream.ReadByte();
            if (b == -1) return null; // end of stream

            if (b == '\r') continue;

            if (b == '\n')
            {
                var line = headerLine.ToString();
                headerLine.Clear();

                if (line.Length == 0) break; // blank line = end of headers

                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[("Content-Length:".Length)..].Trim();
                    if (!int.TryParse(value, out var parsed))
                        throw new InvalidOperationException($"Malformed Content-Length header: '{line}'");
                    contentLength = parsed;
                }
                // Other headers (e.g. Content-Type) are part of the real spec but
                // not required for JSON-RPC bodies — read and ignored, not guessed at.
                continue;
            }

            headerLine.Append((char)b);
        }

        if (contentLength is null)
            throw new InvalidOperationException("LSP message had no Content-Length header.");

        var bodyBuffer = new byte[contentLength.Value];
        var totalRead = 0;
        while (totalRead < bodyBuffer.Length)
        {
            var read = await stream.ReadAsync(bodyBuffer.AsMemory(totalRead, bodyBuffer.Length - totalRead), ct);
            if (read == 0) throw new EndOfStreamException("Stream ended before full LSP message body was read.");
            totalRead += read;
        }

        return Encoding.UTF8.GetString(bodyBuffer);
    }
}
