using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using Autodesk.Revit.UI;

/// Author: Orlando R Hernandez

/// <summary>
/// Provides a centralized utility for displaying TaskDialogs in Revit with customizable icons, content,
/// command links, and automatic exception logging. This version uses a fluent builder pattern for improved readability.
/// 
/// Features:
/// - Fluent builder for easy and readable dialog construction.
/// - Displays informational, warning, error, or shield dialogs.
/// - Supports a flexible number of command links with configurable actions.
/// - Captures detailed exception logs with stack trace and environment info.
/// - Offers a test method to preview all dialog variants.
/// - Can clean up old log files to prevent disk clutter.
/// </summary>
public static class RevitTaskDialog
{
    /// <summary>
    /// Specifies the icon type to display in a TaskDialog.
    /// </summary>
    public enum DialogIconType
    {
        None, Error, Warning, Information, Shield
    }

    /// <summary>
    /// Displays an exception in a TaskDialog and saves a detailed log file.
    /// </summary>
    /// <param name="title">The title of the error dialog.</param>
    /// <param name="ex">The exception to display and log.</param>
    /// <param name="iconType">The icon to display (default is Error).</param>
    public static void ShowExceptionDialog(string title, Exception ex, DialogIconType iconType = DialogIconType.Error)
    {
        string logContent = GenerateExceptionLog(ex);
        string logFilePath = CreateTempLogFile(logContent);

        string instruction = ex.Message;
        if (ex.InnerException != null)
            instruction += $"\nCaused by: {ex.InnerException.GetType().FullName} - {ex.InnerException.Message}";

        // Use the new builder to construct and show the dialog
        new RevitTaskDialogBuilder(title, instruction)
            .WithContent($"A detailed error log has been saved to:\n{logFilePath}\n\nDo you want to open the log file now?")
            .WithIcon(iconType)
            .AddCommandLink("Open log file", () =>
            {
                try
                {
                    //Process.Start(new ProcessStartInfo { FileName = logFilePath, UseShellExecute = true });
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = logFilePath,
                        UseShellExecute = true,
                        Verb = "open", // ensures it's opened with the associated default app
                        WindowStyle = ProcessWindowStyle.Normal // optional: forces it to appear if hidden
                    });

                }
                catch (Exception openEx)
                {
                    // Fallback dialog using the builder
                    new RevitTaskDialogBuilder("Error Opening Log File", openEx.Message)
                        .WithIcon(DialogIconType.Error)
                        .Show();
                }
            })
            .AddCommandLink("Close", null) // No action needed
            .Show();
    }

    /// <summary>
    /// Shows all dialog types for demonstration and testing using the builder pattern.
    /// </summary>
    public static void TestAllDialogVariants()
    {
#if DEBUG || DEBUG_R20 || DEBUG_R21 || DEBUG_R22 || DEBUG_R23 || DEBUG_R24 || DEBUG_R25
        new RevitTaskDialogBuilder("Basic Dialog", "This is a basic task dialog.").Show();

        new RevitTaskDialogBuilder("Info Dialog", "Completed Successfully")
            .WithContent("Everything went fine.")
            .WithIcon(DialogIconType.Information)
            .Show();

        new RevitTaskDialogBuilder("Action Dialog", "Would you like to proceed?")
            .WithContent("This may affect existing files.")
            .WithIcon(DialogIconType.Warning)
            .AddCommandLink("Yes, continue", () => new RevitTaskDialogBuilder("Confirmed", "You chose to continue.").WithIcon(DialogIconType.Information).Show())
            .AddCommandLink("No, cancel", () => new RevitTaskDialogBuilder("Cancelled", "Operation aborted.").WithIcon(DialogIconType.Information).Show())
            .Show();

        try
        {
            throw new InvalidOperationException("Test exception.", new ArgumentNullException("SampleParam"));
        }
        catch (Exception ex)
        {
            ShowExceptionDialog("Exception Example", ex);
        }
#endif
    }

    // #############################################################################################
    // ## Helper Methods
    // #############################################################################################

    /// <summary>
    /// Converts a DialogIconType to a Revit TaskDialogIcon. Public to be accessible by the builder.
    /// </summary>
    public static TaskDialogIcon GetTaskDialogIcon(DialogIconType iconType) => iconType switch
    {
        DialogIconType.Error => TaskDialogIcon.TaskDialogIconError,
        DialogIconType.Warning => TaskDialogIcon.TaskDialogIconWarning,
        DialogIconType.Information => TaskDialogIcon.TaskDialogIconInformation,
        DialogIconType.Shield => TaskDialogIcon.TaskDialogIconShield,
        _ => TaskDialogIcon.TaskDialogIconNone,
    };

    /// <summary>
    /// Deletes log files older than the specified number of days.
    /// </summary>
    public static void ClearOldLogs(int daysOld = 15)
    {
        string folderPath = Path.Combine(Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\Temp"), "RevitAddinLogs");
        if (!Directory.Exists(folderPath)) return;

        var oldFiles = Directory.GetFiles(folderPath)
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime < DateTime.Now.AddDays(-daysOld));

        foreach (var file in oldFiles)
        {
            try { file.Delete(); } catch { /* Ignore deletion failures */ }
        }
    }

    private static string GenerateExceptionLog(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Environment:");
        sb.AppendLine($"  Machine: {Environment.MachineName}");
        sb.AppendLine($"  User: {Environment.UserName}");
        sb.AppendLine($"  OS: {Environment.OSVersion}");
        sb.AppendLine($"  CLR Version: {Environment.Version}");
        sb.AppendLine($"  Culture: {CultureInfo.CurrentCulture.Name}\n");

        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = FileVersionInfo.GetVersionInfo(asm.Location);
            sb.AppendLine("Add-in Info:");
            sb.AppendLine($"  Assembly: {asm.GetName().Name}");
            sb.AppendLine($"  Version: {asm.GetName().Version}");
            sb.AppendLine($"  File Version: {info.FileVersion}\n");
        }
        catch
        {
            sb.AppendLine("Unable to retrieve assembly info.\n");
        }

        AppendDetailedExceptionTrace(sb, ex);
        return sb.ToString();
    }

    private static void AppendDetailedExceptionTrace(StringBuilder stringBuilder, Exception? ex, int depth = 0)
    {
        if (ex == null) return;

        string indent = new string(' ', depth * 2);
        stringBuilder.AppendLine($"{indent}Exception: {ex.GetType().FullName}");
        stringBuilder.AppendLine($"{indent}Message: {ex.Message}");
        stringBuilder.AppendLine($"{indent}Source: {ex.Source}");
        stringBuilder.AppendLine($"{indent}HResult: 0x{ex.HResult:X8}");

        if (ex.Data?.Count > 0)
        {
            stringBuilder.AppendLine($"{indent}Data:");
            foreach (var key in ex.Data.Keys)
            {
                stringBuilder.AppendLine($"{indent}  {key}: {ex.Data[key]}");
            }
        }

        stringBuilder.AppendLine($"{indent}Stack Trace:");
        var trace = new StackTrace(ex, true);
        foreach (var frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            stringBuilder.AppendLine($"{indent}  at {method.DeclaringType?.FullName}.{method.Name}");
            string? fileName = frame.GetFileName();
            if (!string.IsNullOrEmpty(fileName))
            {
                stringBuilder.AppendLine($"{indent}     in {fileName}:line {frame.GetFileLineNumber()}");
            }
        }

        if (ex.InnerException != null)
        {
            stringBuilder.AppendLine($"\n{indent}---> Inner Exception:");
            AppendDetailedExceptionTrace(stringBuilder, ex.InnerException, depth + 1);
        }
    }

    private static string CreateTempLogFile(string content)
    {
        string tempRoot = Environment.ExpandEnvironmentVariables("%LOCALAPPDATA%\\Temp");
        string folderPath = Path.Combine(tempRoot, "RevitAddinLogs");

        Directory.CreateDirectory(folderPath);

        string filePath = Path.Combine(folderPath, $"RevitException_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        try
        {
            File.WriteAllText(filePath, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            new RevitTaskDialogBuilder("Log File Error", $"Could not write log file:\n{ex.Message}")
                .WithIcon(DialogIconType.Error)
                .Show();
        }

        // clear old logs after creating a new one
        ClearOldLogs(15); // Default is 15 days

        return filePath;
    }
}

/// <summary>
/// A fluent builder for creating and displaying a Revit TaskDialog.
/// </summary>
public class RevitTaskDialogBuilder
{
    private readonly string _title;
    private readonly string _mainInstruction;
    private string _mainContent = string.Empty;
    private RevitTaskDialog.DialogIconType _iconType = RevitTaskDialog.DialogIconType.None;
    private bool _allowCancellation = true;
    private readonly List<(TaskDialogCommandLinkId Id, string Text, Action? Action)> _commandLinks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RevitTaskDialogBuilder"/>.
    /// </summary>
    /// <param name="title">The title of the dialog window.</param>
    /// <param name="mainInstruction">The main instruction or header message.</param>
    public RevitTaskDialogBuilder(string title, string mainInstruction)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _mainInstruction = mainInstruction ?? throw new ArgumentNullException(nameof(mainInstruction));
    }

    /// <summary> Sets the main content (the smaller text) of the dialog. </summary>
    public RevitTaskDialogBuilder WithContent(string content)
    {
        _mainContent = content ?? string.Empty;
        return this;
    }

    /// <summary> Sets the icon for the dialog. </summary>
    public RevitTaskDialogBuilder WithIcon(RevitTaskDialog.DialogIconType iconType)
    {
        _iconType = iconType;
        return this;
    }

    /// <summary> Adds a command link button with an associated action. </summary>
    public RevitTaskDialogBuilder AddCommandLink(string text, Action? action)
    {
        // Revit's TaskDialog supports up to 4 command links.
        if (_commandLinks.Count < 4 && !string.IsNullOrWhiteSpace(text))
        {
            var linkId = (TaskDialogCommandLinkId)(_commandLinks.Count + 1); // e.g., CommandLink1, CommandLink2 ...
            _commandLinks.Add((linkId, text, action));
        }
        return this;
    }

    /// <summary> Sets whether the dialog can be closed via the 'X' button or Escape key. </summary>
    public RevitTaskDialogBuilder AllowCancellation(bool allow)
    {
        _allowCancellation = allow;
        return this;
    }

    /// <summary> Builds and displays the configured TaskDialog. </summary>
    public void Show()
    {
        var dialog = new TaskDialog(_title)
        {
            TitleAutoPrefix = false,
            MainInstruction = _mainInstruction,
            MainContent = _mainContent,
            AllowCancellation = _allowCancellation,
            MainIcon = RevitTaskDialog.GetTaskDialogIcon(_iconType)
        };

        foreach (var (id, text, _) in _commandLinks)
        {
            dialog.AddCommandLink(id, text);
        }

        // If there are no command links, show a default OK button.
        if (_commandLinks.Count == 0)
        {
            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
        }

        TaskDialogResult result = dialog.Show();

        // Check if a command link was clicked and execute its action.
        var resultIndex = (int)result - (int)TaskDialogResult.CommandLink1;
        if (resultIndex >= 0 && resultIndex < _commandLinks.Count)
        {
            _commandLinks[resultIndex].Action?.Invoke();
        }
    }
}
