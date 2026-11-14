using System.Diagnostics;
using DotnetDebugger.Protocol;
using StackFrame = DotnetDebugger.Protocol.StackFrame;

namespace DotnetDebugger.Tests;

/// <summary>
/// A win-x86 build of the adapter (<c>./build/publish.ps1 -Rid win-x86 -SelfContained</c>) debugs 32-bit programs: the
/// debugging library has the bitness of the adapter, so the refusal in <see cref="ThirtyTwoBitTests"/> does not apply to
/// it. Not part of the releases; these tests run when DOTNET_DEBUGGER_X86_ADAPTER points at such a build. The debuggee
/// is TestAppFx built for x86 with a Windows PDB, the way old .NET Framework projects often are.
/// </summary>
public class X86AdapterTests
{
    private static string Adapter => Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_X86_ADAPTER")!;

    private static string Fx32FullExe =>
        Path.Combine(TestPaths.Root, "tests", "TestAppFx32Full", "bin", TestPaths.Configuration, TestPaths.FxTargetFramework, "TestAppFx32Full.exe");

    [X86AdapterFact]
    public void LaunchesA32BitProgramAndReadsItsWindowsPdb()
    {
        using var client = new DapClient(adapter: Adapter);
        client.Initialize();
        client.Request("launch", new { program = Fx32FullExe, args = new[] { "basic" }, justMyCode = true });
        Breakpoint bp = Assert.Single(client.SetBreakpoints("fx_locals"));
        client.Request("configurationDone");
        if (!bp.Verified)
            client.WaitForEvent("breakpoint", e => e.GetBody<BreakpointEventBody>()!.Breakpoint is { Verified: true } b && b.Id == bp.Id);

        StoppedEventBody stop = client.WaitForStop("breakpoint");
        StackFrame top = client.StackTrace(stop.ThreadId!.Value)[0];
        Assert.Equal("TestAppFx.Program.Basic()", top.Name);
        Assert.Equal(TestPaths.LineOf("fx_locals"), top.Line);

        // names come from the local scopes of the Windows PDB
        Dictionary<string, Variable> locals = client.Locals(top.Id);
        Assert.Equal("42", locals["number"].Value);
        Assert.Equal("{Person:Ann}", locals["person"].Value);
        Assert.Equal("42", client.Evaluate("number", top.Id).Result);

        client.Request("continue", new { threadId = stop.ThreadId });
        Assert.Equal(3, client.WaitForEvent("exited").GetBody<ExitedEventBody>()!.ExitCode);
    }

    [X86AdapterFact]
    public void AttachesToA32BitProcessAndDetaches()
    {
        var startInfo = new ProcessStartInfo(Fx32FullExe) { UseShellExecute = false, RedirectStandardOutput = true };
        startInfo.ArgumentList.Add("wait");
        using Process debuggee = Process.Start(startInfo)!;
        try
        {
            Assert.Equal("mode: wait", debuggee.StandardOutput.ReadLine());

            using (var client = new DapClient(adapter: Adapter))
            {
                client.Initialize();
                client.Request("attach", new { processId = debuggee.Id });
                client.Request("configurationDone");

                // modules are reported asynchronously after attach; retry until the PDB is known
                Breakpoint bp;
                int attempts = 0;
                while (!(bp = Assert.Single(client.SetBreakpoints("fx_waitLoop"))).Verified && attempts++ < 100)
                    System.Threading.Thread.Sleep(50);
                Assert.True(bp.Verified);

                StoppedEventBody stop = client.WaitForStop("breakpoint");
                Assert.Equal("TestAppFx.Program.Wait()", client.StackTrace(stop.ThreadId!.Value)[0].Name);

                client.Request("disconnect", new { terminateDebuggee = false });
            }

            Assert.False(debuggee.WaitForExit(500), "Detaching must leave the debuggee running.");
        }
        finally
        {
            if (!debuggee.HasExited)
                debuggee.Kill();
        }
    }
}

/// <summary>Runs on Windows when DOTNET_DEBUGGER_X86_ADAPTER names a win-x86 build of the adapter.</summary>
public sealed class X86AdapterFactAttribute : FactAttribute
{
    public X86AdapterFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = ".NET Framework debugging is Windows only.";
        else if (Environment.GetEnvironmentVariable("DOTNET_DEBUGGER_X86_ADAPTER") is not { Length: > 0 } path || !File.Exists(path))
            Skip = "Set DOTNET_DEBUGGER_X86_ADAPTER to a win-x86 build of the adapter (./build/publish.ps1 -Rid win-x86 -SelfContained).";
    }
}
