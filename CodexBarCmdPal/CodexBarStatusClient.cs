using System.IO.Pipes;
namespace CodexBarCmdPal;

internal sealed class CodexBarStatusClient
{
    private const string PipeName = "WinCodexBar.Status";

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
            return await System.Text.Json.JsonSerializer.DeserializeAsync(
                    pipe,
                    CodexBarJsonContext.Default.CodexBarStatusSnapshot,
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ExtensionLog.Write($"ReadSnapshotAsync failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
