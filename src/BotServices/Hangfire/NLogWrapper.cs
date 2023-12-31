using Hangfire.Logging;
using NLog;
using LogLevel = Hangfire.Logging.LogLevel;

namespace Meshmakers.Octo.Backend.BotServices.Hangfire;

internal class NLogWrapper : ILog
{
    private readonly Logger _targetLogger;

    public NLogWrapper(Logger targetLogger)
    {
        _targetLogger = targetLogger ?? throw new ArgumentNullException(nameof(targetLogger));
    }

    public bool Log(LogLevel logLevel, Func<string>? messageFunc, Exception? exception = null)
    {
        var targetLogLevel = ToTargetLogLevel(logLevel);

        // When messageFunc is null, Hangfire.Logging
        // just determines is logging enabled.
        if (messageFunc == null)
        {
            return _targetLogger.IsEnabled(targetLogLevel);
        }

        _targetLogger.Log(targetLogLevel, exception, () => messageFunc());
        return true;
    }

    private static NLog.LogLevel ToTargetLogLevel(LogLevel logLevel)
    {
        switch (logLevel)
        {
            case LogLevel.Trace:
                return NLog.LogLevel.Trace;
            case LogLevel.Debug:
                return NLog.LogLevel.Debug;
            case LogLevel.Info:
                return NLog.LogLevel.Info;
            case LogLevel.Warn:
                return NLog.LogLevel.Warn;
            case LogLevel.Error:
                return NLog.LogLevel.Error;
            case LogLevel.Fatal:
                return NLog.LogLevel.Fatal;
        }

        return NLog.LogLevel.Off;
    }
}
