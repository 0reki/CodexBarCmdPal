using System.IO.Pipes;
using System.Text.Json;

namespace CodexBarCmdPal;

internal sealed class CodexBarStatusClient
{
    private const string PipeName = "WinCodexBar.Status";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<CodexBarStatusSnapshot?> ReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.In,
                PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));

            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<CodexBarStatusSnapshot>(
                    pipe,
                    JsonOptions,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
