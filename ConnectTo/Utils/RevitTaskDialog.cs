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
/// Provides a centralized utility for displaying Revit TaskDialogs using a fluent builder pattern,
/// with support for exception reporting, logging, and user actions.
/// </summary>
public static class RevitTaskDialog
{
    /// <summary>
    /// Specifies the icon type to display in a TaskDialog.
    /// Options: None, Error, Warning, Information, Shield
    /// </summary>
    public enum DialogIconType
    {
        None,
        Error,
        Warning,
        Information,
        Shield
    }

    /// <summary>
    /// Displays a TaskDialog for an exception, logs details to a file, and offers to open the log.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="exception">Exception to display.</param>
    /// <param name="iconType">Icon type for the dialog.</param>
    public static void ShowExceptionDialog(string title, Exception exception, DialogIconType iconType = DialogIconType.Error)
    {
        string logContent = GenerateExceptionLog(exception);
        string logFilePath = CreateTempLogFile(logContent);

        string instructionText = exception.Message;
        if (exception.InnerException != null)
        {
            instructionText += $"\nCaused by: {exception.InnerException.GetType().FullName} - {exception.InnerException.Message}";
        }

        var dialog = new TaskDialog(title)
        {
            TitleAutoPrefix = false,
            MainInstruction = instructionText,
            MainContent = $"A detailed error log has been saved to:\n{logFilePath}\n\nDo you want to open the log file now?",
            AllowCancellation = true,
            MainIcon = GetTaskDialogIcon(iconType)
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open log file");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Close");

        TaskDialogResult result = dialog.Show();

        if (result == TaskDialogResult.CommandLink1)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = logFilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception openEx)
            {
                TaskDialog.Show("Error Opening Log File", openEx.Message);
            }
        }
    }

    /// <summary>
    /// Shows all dialog variants for testing purposes (only in DEBUG builds).
    /// TestAllDialogVariants is intended for development and debugging scenarios,
    /// </summary>
    public static void TestAllDialogVariants()
    {
#if DEBUG || DEBUG_R20 || DEBUG_R21 || DEBUG_R22 || DEBUG_R23 || DEBUG_R24 || DEBUG_R25 || DEBUG_R26
        new RevitTaskDialogBuilder("Basic Dialog", "This is a basic task dialog.").Show();

        new RevitTaskDialogBuilder("Info Dialog", "Operation completed successfully.")
            .WithContent("Everything went as expected.")
            .WithIcon("Information")
            .Show();

        new RevitTaskDialogBuilder("Warning Dialog", "Potential issue detected.")
            .WithContent("Check your input values before proceeding.")
            .WithIcon("Warning")
            .Show();

        new RevitTaskDialogBuilder("Error Dialog", "Critical error encountered.")
            .WithContent("The process cannot continue.")
            .WithIcon("Error")
            .Show();

        new RevitTaskDialogBuilder("Security Notice", "Administrative privileges required.")
            .WithContent("Please rerun the application as an administrator.")
            .WithIcon("Shield")
            .Show();

        new RevitTaskDialogBuilder("Save Changes", "Would you like to save your changes?")
            .WithContent("Unsaved changes will be lost.")
            .WithIcon("Warning")
            .AddCommandLink("Save", () =>
                new RevitTaskDialogBuilder("Saved", "Changes have been saved.")
                    .WithIcon("Information")
                    .Show())
            .AddCommandLink("Don't Save", () =>
                new RevitTaskDialogBuilder("Not Saved", "Changes were discarded.")
                    .WithIcon("Warning")
                    .Show())
            .Show();

        try
        {
            throw new InvalidOperationException("Simulated exception", new ArgumentNullException("parameterName"));
        }
        catch (Exception ex)
        {
            ShowExceptionDialog("Exception Example", ex);
        }
#endif
    }

    /// <summary>
    /// Gets the corresponding <see cref="TaskDialogIcon"/> for a given <see cref="DialogIconType"/>.
    /// </summary>
    /// <param name="iconType">The icon type.</param>
    /// <returns>The TaskDialogIcon value.</returns>
    public static TaskDialogIcon GetTaskDialogIcon(DialogIconType iconType) => iconType switch
    {
        DialogIconType.Error => TaskDialogIcon.TaskDialogIconError,
        DialogIconType.Warning => TaskDialogIcon.TaskDialogIconWarning,
        DialogIconType.Information => TaskDialogIcon.TaskDialogIconInformation,
        DialogIconType.Shield => TaskDialogIcon.TaskDialogIconShield,
        _ => TaskDialogIcon.TaskDialogIconNone,
    };

    /// <summary>
    /// Parses a string to a <see cref="DialogIconType"/> value.
    /// </summary>
    /// <param name="iconString">The icon type as string.</param>
    /// <returns>The parsed DialogIconType, or None if invalid.</returns>
    public static DialogIconType ParseIconType(string? iconString)
    {
        if (Enum.TryParse(iconString, true, out DialogIconType parsedIcon))
        {
            return parsedIcon;
        }
        return DialogIconType.None;
    }

    /// <summary>
    /// Generates a detailed exception log string.
    /// </summary>
    /// <param name="exception">The exception to log.</param>
    /// <returns>Exception log as string.</returns>
    private static string GenerateExceptionLog(Exception exception)
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
            sb.AppendLine("Assembly Info:");
            sb.AppendLine($"  Name: {asm.GetName().Name}");
            sb.AppendLine($"  Version: {asm.GetName().Version}");
            sb.AppendLine($"  File Version: {info.FileVersion}\n");
        }
        catch
        {
            sb.AppendLine("Unable to retrieve assembly information.\n");
        }

        AppendDetailedExceptionTrace(sb, exception);
        return sb.ToString();
    }

    /// <summary>
    /// Appends detailed exception trace information to a StringBuilder.
    /// </summary>
    /// <param name="sb">The StringBuilder to append to.</param>
    /// <param name="exception">The exception to trace.</param>
    /// <param name="depth">The depth of the exception (for indentation).</param>
    private static void AppendDetailedExceptionTrace(StringBuilder sb, Exception? exception, int depth = 0)
    {
        if (exception == null) return;

        string indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}Exception: {exception.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {exception.Message}");
        sb.AppendLine($"{indent}Source: {exception.Source}");
        sb.AppendLine($"{indent}HResult: 0x{exception.HResult:X8}");

        if (exception.Data?.Count > 0)
        {
            sb.AppendLine($"{indent}Data:");
            foreach (var key in exception.Data.Keys)
                sb.AppendLine($"{indent}  {key}: {exception.Data[key]}");
        }

        sb.AppendLine($"{indent}Stack Trace:");
        var stackTrace = new StackTrace(exception, true);
        foreach (var frame in stackTrace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            if (method == null) continue;

            sb.AppendLine($"{indent}  at {method.DeclaringType?.FullName}.{method.Name}");
            string? fileName = frame.GetFileName();
            if (!string.IsNullOrEmpty(fileName))
                sb.AppendLine($"{indent}     in {fileName}:line {frame.GetFileLineNumber()}");
        }

        if (exception.InnerException != null)
        {
            sb.AppendLine($"\n{indent}---> Inner Exception:");
            AppendDetailedExceptionTrace(sb, exception.InnerException, depth + 1);
        }
    }

    /// <summary>
    /// Creates a temporary log file with the given content.
    /// </summary>
    /// <param name="content">The content to write to the log file.</param>
    /// <returns>The full path to the created log file.</returns>
    private static string CreateTempLogFile(string content)
    {
        string tempDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "RevitAddinLogs");
        Directory.CreateDirectory(tempDirectory);

        string fileName = $"RevitException_{DateTime.Now:yyyyMMdd_HHmmss}.log";
        string fullPath = Path.Combine(tempDirectory, fileName);

        try
        {
            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            new RevitTaskDialogBuilder("Log File Error", $"Could not write log file:\n{ex.Message}")
                .WithIcon(DialogIconType.Error)
                .Show();
        }

        ClearOldLogs(15);
        return fullPath;
    }

    /// <summary>
    /// Deletes log files older than the specified number of days.
    /// </summary>
    /// <param name="daysOld">Number of days before a log file is considered old.</param>
    public static void ClearOldLogs(int daysOld = 15)
    {
        string folderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "RevitAddinLogs");
        if (!Directory.Exists(folderPath)) return;

        var oldFiles = Directory.GetFiles(folderPath)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTime < DateTime.Now.AddDays(-daysOld));

        foreach (var file in oldFiles)
        {
            try { file.Delete(); } catch { /* Silent fail */ }
        }
    }
}

/// <summary>
/// Fluent builder for creating and displaying Revit TaskDialogs.
/// </summary>
public class RevitTaskDialogBuilder
{
    private readonly string _dialogTitle;
    private readonly string _mainInstruction;
    private string _mainContent = string.Empty;
    private RevitTaskDialog.DialogIconType _iconType = RevitTaskDialog.DialogIconType.None;
    private bool _allowCancellation = true;
    private readonly List<(TaskDialogCommandLinkId Id, string Text, Action? Action)> _commandLinks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="RevitTaskDialogBuilder"/> class.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="mainInstruction">Main instruction text.</param>
    /// <exception cref="ArgumentNullException">Thrown if title or mainInstruction is null.</exception>
    public RevitTaskDialogBuilder(string title, string mainInstruction)
    {
        _dialogTitle = title ?? throw new ArgumentNullException(nameof(title));
        _mainInstruction = mainInstruction ?? throw new ArgumentNullException(nameof(mainInstruction));
    }

    /// <summary>
    /// Sets the main content text of the dialog.
    /// </summary>
    /// <param name="content">Content text.</param>
    /// <returns>The builder instance.</returns>
    public RevitTaskDialogBuilder WithContent(string content)
    {
        _mainContent = content ?? string.Empty;
        return this;
    }

    /// <summary>
    /// Sets the icon type for the dialog.
    /// </summary>
    /// <param name="iconType">Icon type.</param>
    /// <returns>The builder instance.</returns>
    public RevitTaskDialogBuilder WithIcon(RevitTaskDialog.DialogIconType iconType)
    {
        _iconType = iconType;
        return this;
    }

    /// <summary>
    /// Sets the icon type for the dialog by name.
    /// </summary>
    /// <param name="iconName">Icon type as string.</param>
    /// <returns>The builder instance.</returns>
    public RevitTaskDialogBuilder WithIcon(string iconName)
    {
        _iconType = RevitTaskDialog.ParseIconType(iconName);
        return this;
    }

    /// <summary>
    /// Adds a command link button to the dialog.
    /// </summary>
    /// <param name="buttonText">Button text.</param>
    /// <param name="onClick">Action to invoke when button is clicked.</param>
    /// <returns>The builder instance.</returns>
    public RevitTaskDialogBuilder AddCommandLink(string buttonText, Action? onClick)
    {
        if (_commandLinks.Count < 4 && !string.IsNullOrWhiteSpace(buttonText))
        {
            var commandId = (TaskDialogCommandLinkId)(_commandLinks.Count + 1);
            _commandLinks.Add((commandId, buttonText, onClick));
        }
        return this;
    }

    /// <summary>
    /// Sets whether the dialog can be cancelled.
    /// </summary>
    /// <param name="allow">True to allow cancellation; otherwise, false.</param>
    /// <returns>The builder instance.</returns>
    public RevitTaskDialogBuilder AllowCancellation(bool allow)
    {
        _allowCancellation = allow;
        return this;
    }

    /// <summary>
    /// Shows the dialog and invokes any associated command link actions.
    /// </summary>
    public void Show()
    {
        var dialog = new TaskDialog(_dialogTitle)
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

        if (_commandLinks.Count == 0)
        {
            dialog.CommonButtons = TaskDialogCommonButtons.Ok;
        }

        TaskDialogResult result = dialog.Show();

        int selectedIndex = (int)result - (int)TaskDialogResult.CommandLink1;
        if (selectedIndex >= 0 && selectedIndex < _commandLinks.Count)
        {
            _commandLinks[selectedIndex].Action?.Invoke();
        }
    }
}
