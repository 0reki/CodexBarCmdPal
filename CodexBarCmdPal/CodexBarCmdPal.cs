using Microsoft.CommandPalette.Extensions;
using System.Runtime.InteropServices;

namespace CodexBarCmdPal;

[ComVisible(true)]
[Guid("00f37f09-1369-4fe4-82a7-8c41796ca5fc")]
[ClassInterface(ClassInterfaceType.None)]
public sealed partial class CodexBarCmdPal : IExtension, IDisposable
{
    private readonly ManualResetEvent? _extensionDisposedEvent;
    private readonly CodexBarCommandsProvider _provider = new();

    public CodexBarCmdPal()
    {
        ExtensionLog.Write("CodexBarCmdPal constructed");
    }

    public CodexBarCmdPal(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
        ExtensionLog.Write("CodexBarCmdPal constructed with dispose event");
    }

    public object? GetProvider(ProviderType providerType)
    {
        ExtensionLog.Write($"GetProvider requested: {providerType}");
        return providerType == ProviderType.Commands ? _provider : null;
    }

    public void Dispose()
    {
        ExtensionLog.Write("CodexBarCmdPal disposed");
        _provider.Dispose();
        _extensionDisposedEvent?.Set();
    }
}
