using System.Diagnostics;
using System.Text;

namespace Viddl.Downloads.Processes;

/// <summary>Describes an external program to run.</summary>
public sealed class ProcessRequest
{
    public required string FileName { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string? WorkingDirectory { get; init; }
    public Action<string>? StandardOutput { get; init; }
    public Action<string>? StandardError { get; init; }

    /// <summary>
    /// On cancellation: true = send "q" on standard input so the program can finish its file (ffmpeg),
    /// then kill it if it hasn't exited after <see cref="GracefulStopTimeout"/>; false = kill immediately.
    /// </summary>
    public bool GracefulStop { get; init; }

    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Runs external programs. Replaceable for testing.</summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs the program and returns its exit code. When cancelled: throws <see cref="OperationCanceledException"/>
    /// unless <see cref="ProcessRequest.GracefulStop"/> is set, in which case the exit code is returned.
    /// </summary>
    Task<int> RunAsync(ProcessRequest request, CancellationToken ct);
}

/// <summary>Runs programs with <see cref="Process"/>, hidden, capturing their output line by line.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (request.WorkingDirectory != null) psi.WorkingDirectory = request.WorkingDirectory;
        foreach (var a in request.Arguments) psi.ArgumentList.Add(a);
        foreach (var (k, v) in request.Environment) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) request.StandardOutput?.Invoke(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) request.StandardError?.Invoke(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool cancelled = false;
        using (ct.Register(() =>
        {
            cancelled = true;
            if (request.GracefulStop)
            {
                TrySendQuit(proc);
                _ = Task.Delay(request.GracefulStopTimeout).ContinueWith(_ => Kill(proc), TaskScheduler.Default);
            }
            else
            {
                Kill(proc);
            }
        }))
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (cancelled && !request.GracefulStop) throw new OperationCanceledException(ct);
        return proc.ExitCode;
    }

    /// <summary>Asks the program to quit by typing "q" (ffmpeg's stop key). False if its input is gone.</summary>
    internal static bool TrySendQuit(Process proc)
    {
        try
        {
            proc.StandardInput.Write('q');
            proc.StandardInput.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false; // already exited, or input not redirected
        }
    }

    /// <summary>Kills the program and its children; does nothing if it has already exited.</summary>
    internal static void Kill(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Not started or already gone.
        }
    }
}
