using CthExtract.Core;
using Serilog;
using Log = CthExtract.Core.Log;
using Serilog.Core;
using Serilog.Events;

namespace CthExtract.Fortnite.Unreal;

/// <summary>Sends CUE4Parse's own warnings/errors (Serilog) to extract.log, capped so a noisy build cannot flood it.</summary>
internal sealed class LogBridge : ILogEventSink
{
    private readonly Log _log;
    private readonly int _max;
    private int _count;

    private LogBridge(Log log, int max) { _log = log; _max = max; }

    public int Suppressed => Math.Max(0, _count - _max);

    public void Emit(LogEvent e)
    {
        int n = Interlocked.Increment(ref _count);
        if (n > _max) return;
        var msg = e.RenderMessage();
        if (e.Exception != null) msg += $" ({e.Exception.GetType().Name}: {e.Exception.Message.Split('\n')[0]})";
        if (msg.Length > 600) msg = msg[..600] + "…";
        var line = $"[fortnite] CUE4Parse {e.Level}: {msg}";
        if (e.Level >= LogEventLevel.Error) _log.Warn(line); else _log.Info(line);
        if (n == _max) _log.Info($"[fortnite] CUE4Parse: further messages suppressed after {_max}");
    }

    /// <summary>Stop sending CUE4Parse messages anywhere (after the module is done with its log).</summary>
    public static void Silence() => CUE4Parse.CUE4ParseLog.UseLogger(new LoggerConfiguration().CreateLogger());

    /// <summary>Route CUE4Parse logging to <paramref name="log"/> (warnings and above).</summary>
    public static LogBridge Install(Log log, int max = 300)
    {
        var sink = new LogBridge(log, max);
        var logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Sink(sink).CreateLogger();
        CUE4Parse.CUE4ParseLog.UseLogger(logger);
        return sink;
    }
}
