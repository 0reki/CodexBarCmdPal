// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Shmuelie.WinRTServer;
using Shmuelie.WinRTServer.CsWinRT;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CodexBarCmdPal;

public class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        ExtensionLog.Write($"Main launched: {string.Join(" ", args)}");
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            ExtensionLog.Write("Creating COM server");
            global::Shmuelie.WinRTServer.ComServer server = new();
            ExtensionLog.Write("COM server created");

            ManualResetEvent extensionDisposedEvent = new(false);
            
            // We are instantiating an extension instance once above, and returning it every time the callback in RegisterExtension below is called.
            // This makes sure that only one instance of SampleExtension is alive, which is returned every time the host asks for the IExtension object.
            // If you want to instantiate a new instance each time the host asks, create the new instance inside the delegate.
            CodexToysExtension extensionInstance = new(extensionDisposedEvent);
            server.RegisterClass<CodexToysExtension, IExtension>(() => extensionInstance);
            ExtensionLog.Write("Starting COM server");
            server.Start();
            ExtensionLog.Write("COM server started");
            
            // This will make the main thread wait until the event is signalled by the extension class.
            // Since we have single instance of the extension object, we exit as soon as it is disposed.
            extensionDisposedEvent.WaitOne();
            server.Stop();
            server.UnsafeDispose();
        }
        else
        {
            Console.WriteLine("Not being launched as a Extension... exiting.");
        }
    }
}
