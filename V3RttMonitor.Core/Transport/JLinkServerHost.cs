using System.Diagnostics;

namespace V3RttMonitor.Core.Transport;

public sealed class JLinkServerHost : IAsyncDisposable
{
    private Process? _process;
    public event Action<string>? LogReceived;
    public bool IsRunning => _process is { HasExited: false };
    public bool OwnsProcess => _process is not null;

    public void Start(RttSessionSettings settings)
    {
        if (IsRunning) return;
        var exe = Path.Combine(settings.JLinkDirectory, "JLinkGDBServerCL.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("未找到JLinkGDBServerCL.exe。", exe);
        var info = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        Add("-device", settings.Device);
        Add("-if", settings.Interface);
        Add("-speed", settings.SpeedKhz.ToString());
        Add("-port", settings.GdbPort.ToString());
        Add("-RTTTelnetPort", settings.Port.ToString());
        Add("-LocalhostOnly", "1");
        info.ArgumentList.Add("-noreset");
        info.ArgumentList.Add("-nosinglerun");
        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => Forward(e.Data);
        _process.ErrorDataReceived += (_, e) => Forward(e.Data);
        if (!_process.Start()) throw new InvalidOperationException("J-Link GDB Server启动失败。");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Forward($"已启动J-Link GDB Server，PID={_process.Id}");

        void Add(string name, string value) { info.ArgumentList.Add(name); info.ArgumentList.Add(value); }
    }

    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        var processId = TryGetProcessId(process);
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("quit").ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }
        finally
        {
            process.Dispose();
            Forward(processId > 0
                ? $"J-Link GDB Server已释放，PID={processId}"
                : "J-Link GDB Server已释放");
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    private void Forward(string? text) { if (!string.IsNullOrWhiteSpace(text)) LogReceived?.Invoke(text.Trim()); }
    private static int TryGetProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return -1; }
    }
}
