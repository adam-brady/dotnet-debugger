using System.Diagnostics;
using DotnetDebugger.Protocol;

namespace DotnetDebugger.Tests;

/// <summary>
/// What the DAP client of Neovim (nvim-dap) relies on. The client cannot be changed: as soon as "disconnect" is answered
/// it closes the session and kills the adapter process (Session:disconnect → close → handle:kill), without waiting for
/// "terminated" or for the adapter to exit. Whatever the adapter still has to do for the debuggee must be done before
/// the response.
/// </summary>
public class NeovimClientTests
{
    [Fact]
    public void DetachLeavesTheDebuggeeRunningWhenTheAdapterIsKilledRightAfterTheResponse()
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add(TestPaths.TestAppDll);
        startInfo.ArgumentList.Add("wait");
        AttachDetachAndKillTheAdapter(startInfo, "loop");
    }

    [WindowsFact]
    public void DetachLeavesTheFrameworkDebuggeeRunningWhenTheAdapterIsKilledRightAfterTheResponse()
    {
        var startInfo = new ProcessStartInfo(TestPaths.TestAppFxExe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        AttachDetachAndKillTheAdapter(startInfo, "fx_waitLoop");
    }

    private static void AttachDetachAndKillTheAdapter(ProcessStartInfo startInfo, string marker)
    {
        using Process debuggee = Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());

            using (var client = new DapClient())
            {
                client.Initialize();
                client.Request("attach", new { processId = debuggee.Id });
                client.Request("configurationDone");

                // modules are reported asynchronously after attach; retry until the PDB is known
                Breakpoint bp;
                int attempts = 0;
                while (!(bp = Assert.Single(client.SetBreakpoints(marker))).Verified && attempts++ < 100)
                    System.Threading.Thread.Sleep(50);
                Assert.True(bp.Verified);
                StoppedEventBody stop = client.WaitForStop("breakpoint");

                // nvim-dap detaches from a running program as often as from a stopped one
                client.Request("continue", new { threadId = stop.ThreadId });
                client.SetBreakpoints(Array.Empty<string>());

                client.Request("disconnect", new { terminateDebuggee = false });
                client.KillAdapter();
            }

            Assert.False(debuggee.WaitForExit(2000), "Detaching must leave the debuggee running, even when the client kills the adapter right after the response.");
        }
        finally
        {
            if (!debuggee.HasExited)
                debuggee.Kill();
        }
    }
}
