// -----------------------------------------------------------------------
// <copyright file="ConsoleLogger.cs" company="Altemiq">
// Copyright (c) Altemiq. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Altemiq.NuGet.Promote;

using global::NuGet.Common;

/// <summary>
/// The console logger.
/// </summary>
internal sealed class ConsoleLogger : LoggerBase
{
    /// <summary>
    /// The instance.
    /// </summary>
    public static readonly ILogger Instance = new ConsoleLogger();

    private static readonly Lock WriterLock = new();

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0042:Do not use blocking calls in an async method", Justification = "This follows the base implementation")]
    public override Task LogAsync(ILogMessage message)
    {
        this.Log(message);
        return Task.FromResult(result: true);
    }

    /// <inheritdoc/>
    public override void Log(ILogMessage? message)
    {
        if (message is null || !this.DisplayMessage(message.Level))
        {
            return;
        }

        switch (message.Level)
        {
            case LogLevel.Debug:
                WriteColor(Console.Out, ConsoleColor.Gray, message.Message);
                break;
            case LogLevel.Warning:
                WriteWarning(message.FormatWithCode());
                break;
            case LogLevel.Error:
                // Use standard error format for Packaging Errors
                var messageWithCode = message.FormatWithCode();
                WriteError(message.Code is >= NuGetLogCode.NU5000 and <= NuGetLogCode.NU5500
                    ? $"Error {messageWithCode}"
                    : messageWithCode);
                break;
            default:
                // Verbose, Information
                WriteLine(message.Message);
                break;
        }
    }

    private static void WriteLine(string value)
    {
        lock (WriterLock)
        {
            Console.Out.WriteLine(value);
        }
    }

    private static void WriteError(string? value) => WriteError(value, []);

    private static void WriteError(string? format, params object?[] args) => WriteColor(Console.Error, ConsoleColor.Red, format, args);

    private static void WriteWarning(string? value) => WriteWarning(prependWarningText: true, value: value, args: []);

    private static void WriteWarning(bool prependWarningText, string? value, params object?[] args)
    {
        var message = prependWarningText
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "WARNING: {0}", value)
            : value;

        WriteColor(Console.Out, ConsoleColor.Yellow, message, args);
    }

    private static void WriteColor(TextWriter writer, ConsoleColor color, string? value, params object?[] args)
    {
        if (value is null)
        {
            return;
        }

        lock (WriterLock)
        {
            var currentColor = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = color;
                if (args is { Length: > 0 })
                {
                    writer.WriteLine(value, args);
                }
                else
                {
                    // If it doesn't look like something that needs to be formatted, don't format it.
                    writer.WriteLine(value);
                }
            }
            finally
            {
                Console.ForegroundColor = currentColor;
            }
        }
    }
}