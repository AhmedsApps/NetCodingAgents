using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodingAgents.Worker.Tools;

public class WorkspaceTools
{
    private readonly string _workspaceDir;

    // Files created or modified through this tool instance during the run, so callers
    // (e.g. the review phase) can be told exactly what changed.
    private readonly HashSet<string> _changedFiles = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> ChangedFiles => _changedFiles.ToArray();

    // Guard rails to keep tool output from flooding the model's context window.
    private const int MaxCommandOutputChars = 20000;
    private const int MaxSearchMatches = 200;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };

    // Prefer PowerShell 7 ("pwsh") when installed because it supports && and ||; Windows
    // PowerShell 5.1 does not, and agents routinely emit those bash-style operators.
    private static readonly Lazy<(string exe, bool supportsChaining)> Shell = new(() =>
    {
        foreach (var candidate in new[] { "pwsh.exe", "pwsh" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-NoProfile -Command \"exit 0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (probe != null) { probe.WaitForExit(5000); if (probe.ExitCode == 0) return (candidate, true); }
            }
            catch { /* not installed */ }
        }
        return ("powershell.exe", false);
    });

    public event Action<string, string>? OnProgress;

    // Raised when the agent produces an image file (e.g. a screenshot): (fileName, fullPath).
    // The worker forwards these to the server so they can be shown inline in the chat.
    public event Action<string, string>? OnImageSaved;

    public WorkspaceTools(string? workspaceDir = null)
    {
        if (string.IsNullOrEmpty(workspaceDir))
        {
            // Traverse up to find the root folder containing CodingAgents.slnx
            string current = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(current) && !File.Exists(Path.Combine(current, "CodingAgents.slnx")))
            {
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            _workspaceDir = current;
        }
        else
        {
            _workspaceDir = Path.GetFullPath(workspaceDir);
        }
    }

    // Resolves a workspace-relative path and rejects anything that escapes the root.
    // Resolves a path for the file tools. An absolute path is honored as-is so the agent can
    // reach anywhere on the machine (e.g. the user's Downloads folder); a relative path
    // resolves against this task's workspace folder. Path.Combine returns the second argument
    // unchanged when it is already rooted, so this handles both cases.
    private bool TryResolvePath(string path, out string fullPath, out string error)
    {
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_workspaceDir, path));
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            fullPath = string.Empty;
            error = $"Error: invalid path '{path}': {ex.Message}";
            return false;
        }
    }

    // Segment-aware, cross-platform ignore filter (the old substring check was Windows-only).
    private static bool IsIgnored(string relativePath)
    {
        var segments = relativePath.Split('/', '\\');
        return segments.Any(s => s is "bin" or "obj" or ".git" or ".vs" or "node_modules" or "packages");
    }

    [Description("Lists all files in the workspace directory (excluding bin, obj, and .git folders).")]
    public string ListFiles()
    {
        OnProgress?.Invoke("ToolCall", "Listing files in workspace...");
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Files in workspace ({_workspaceDir}):");
            foreach (var file in Directory.GetFiles(_workspaceDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_workspaceDir, file);

                // Ignore build artifacts, packages, git database, and ide settings
                if (IsIgnored(rel))
                    continue;

                sb.AppendLine($"- {rel}");
            }
            string result = sb.ToString();
            OnProgress?.Invoke("ToolOutput", $"Found {result.Split('\n').Length - 2} files.");
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Error listing files: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    [Description("Reads the contents of a file. Accepts a workspace-relative path or an absolute path anywhere on the machine.")]
    public string ReadFile([Description("Path to the file: relative to the workspace, or an absolute path anywhere on the machine.")] string relativePath)
    {
        OnProgress?.Invoke("ToolCall", $"Reading file '{relativePath}'...");
        try
        {
            if (!TryResolvePath(relativePath, out string fullPath, out string pathErr))
            {
                OnProgress?.Invoke("Error", pathErr);
                return pathErr;
            }

            if (!File.Exists(fullPath))
            {
                string err = $"Error: File '{relativePath}' does not exist.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            string content = File.ReadAllText(fullPath);
            OnProgress?.Invoke("ToolOutput", $"Successfully read '{relativePath}' ({content.Length} characters).");
            return content;
        }
        catch (Exception ex)
        {
            string err = $"Error reading file: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    [Description("Creates or overwrites a file in the workspace with new content.")]
    public string WriteFile(
        [Description("The path to the file relative to the workspace root.")] string relativePath, 
        [Description("The exact new content to write to the file.")] string content)
    {
        OnProgress?.Invoke("ToolCall", $"Writing file '{relativePath}'...");
        try
        {
            if (!TryResolvePath(relativePath, out string fullPath, out string pathErr))
            {
                OnProgress?.Invoke("Error", pathErr);
                return pathErr;
            }

            // Create parent directories if they don't exist
            string? dir = Path.GetDirectoryName(fullPath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(fullPath, content);
            _changedFiles.Add(relativePath);
            string result = $"Success: Wrote to '{relativePath}' successfully ({content.Length} characters).";
            OnProgress?.Invoke("ToolOutput", result);
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Error writing file: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    [Description("Runs a command in the workspace using Windows PowerShell and returns its output. IMPORTANT: this is PowerShell, not bash. Do not use '&&' or '||' - Windows PowerShell 5.1 rejects them. Run one command per call, or separate them with ';'. Use PowerShell equivalents (for example 'dir' or 'Get-ChildItem', not 'ls -la'). The command is killed if it exceeds the timeout, so do not start long-lived processes like dev servers or watchers.")]
    public string ExecuteCommand(
        [Description("The exact command string to run in the terminal.")] string command,
        [Description("Maximum seconds to allow the command to run before it is killed. Defaults to 120.")] int timeoutSeconds = 120)
    {
        OnProgress?.Invoke("ToolCall", $"Executing command: {command}...");
        try
        {
            if (timeoutSeconds <= 0) timeoutSeconds = 120;

            // Windows PowerShell 5.1 cannot parse && or ||; say so plainly instead of
            // returning a cryptic parser error.
            if (!Shell.Value.supportsChaining && Regex.IsMatch(command, @"(\|\||&&)"))
            {
                string err = "Error: this shell is Windows PowerShell 5.1, which does not support '&&' or '||'. " +
                             "Run the commands separately, or join them with ';'. Example: 'dotnet build; dotnet test'.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            var processInfo = new ProcessStartInfo
            {
                FileName = Shell.Value.exe,
                Arguments = $"-NoProfile -Command \"{command.Replace("\"", "\\\"")}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _workspaceDir
            };

            using var process = Process.Start(processInfo);
            if (process == null)
            {
                string err = "Error: Failed to start process.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            // Read both pipes concurrently; reading one to completion before the other
            // can deadlock if the child fills the second pipe's buffer while we block.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(timeoutSeconds * 1000);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                string timeoutErr = $"Error: Command timed out after {timeoutSeconds} seconds and was terminated. Avoid long-running or interactive commands.";
                OnProgress?.Invoke("Error", timeoutErr);
                return timeoutErr;
            }

            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();

            var sb = new StringBuilder();
            sb.AppendLine($"(Exit code: {process.ExitCode})");
            if (!string.IsNullOrWhiteSpace(output))
            {
                sb.AppendLine("=== Output ===");
                sb.AppendLine(output);
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                sb.AppendLine("=== Error ===");
                sb.AppendLine(error);
            }
            string result = Truncate(sb.ToString(), MaxCommandOutputChars);
            OnProgress?.Invoke("CommandOutput", result);
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Error executing command: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text.Substring(0, maxChars) +
               $"\n\n... [output truncated; {text.Length - maxChars} more characters omitted] ...";
    }

    [Description("Searches the text content of files in the workspace for a regular expression and returns matching files with line numbers. Use this to locate code instead of reading every file. Runs locally; it does not use any external 'grep' tool.")]
    public string SearchInFiles(
        [Description("A .NET regular expression to search for (case-insensitive).")] string pattern,
        [Description("Optional file extension filter, e.g. '.cs' or 'cs'. Leave empty to search all files.")] string fileExtension = "")
    {
        OnProgress?.Invoke("ToolCall", $"Searching workspace for /{pattern}/...");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            const string err = "Error: search pattern must not be empty.";
            OnProgress?.Invoke("Error", err);
            return err;
        }

        Regex regex;
        try
        {
            // Timeout guards against catastrophic backtracking on a hostile pattern.
            regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            string err = $"Error: invalid regular expression: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }

        string ext = fileExtension.Trim();
        if (ext.Length > 0 && !ext.StartsWith(".")) ext = "." + ext;

        try
        {
            var sb = new StringBuilder();
            int matchCount = 0;
            bool truncated = false;

            foreach (var file in Directory.EnumerateFiles(_workspaceDir, "*.*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(_workspaceDir, file);
                if (IsIgnored(rel)) continue;
                if (ext.Length > 0 && !file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;

                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; } // skip binary/locked/unreadable files

                for (int i = 0; i < lines.Length; i++)
                {
                    bool isMatch;
                    try { isMatch = regex.IsMatch(lines[i]); }
                    catch (RegexMatchTimeoutException) { continue; }

                    if (!isMatch) continue;

                    sb.AppendLine($"{rel}:{i + 1}: {lines[i].Trim()}");
                    if (++matchCount >= MaxSearchMatches)
                    {
                        truncated = true;
                        break;
                    }
                }
                if (truncated) break;
            }

            if (matchCount == 0)
            {
                OnProgress?.Invoke("ToolOutput", "No matches found.");
                return "No matches found.";
            }

            if (truncated) sb.AppendLine($"... [stopped after {MaxSearchMatches} matches; refine your pattern] ...");
            OnProgress?.Invoke("ToolOutput", $"Found {matchCount} match(es).");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            string err = $"Error searching files: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    [Description("Makes a targeted edit to an existing file by replacing an exact block of text. Prefer this over WriteFile for small changes so you do not have to reproduce the whole file.")]
    public string EditFile(
        [Description("The path to the file relative to the workspace root.")] string relativePath,
        [Description("The exact existing text to find. Must match the file content precisely, including indentation.")] string find,
        [Description("The text to replace the found text with.")] string replace,
        [Description("If true, replaces every occurrence. If false, the edit fails unless the found text appears exactly once.")] bool replaceAll = false)
    {
        OnProgress?.Invoke("ToolCall", $"Editing file '{relativePath}'...");
        try
        {
            if (!TryResolvePath(relativePath, out string fullPath, out string pathErr))
            {
                OnProgress?.Invoke("Error", pathErr);
                return pathErr;
            }

            if (!File.Exists(fullPath))
            {
                string err = $"Error: File '{relativePath}' does not exist. Use WriteFile to create it.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            if (string.IsNullOrEmpty(find))
            {
                const string err = "Error: 'find' text must not be empty.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            string content = File.ReadAllText(fullPath);
            int occurrences = CountOccurrences(content, find);

            if (occurrences == 0)
            {
                string err = "Error: the 'find' text was not found in the file. Read the file to confirm the exact text.";
                OnProgress?.Invoke("Error", err);
                return err;
            }
            if (occurrences > 1 && !replaceAll)
            {
                string err = $"Error: the 'find' text appears {occurrences} times. Provide more surrounding context to make it unique, or set replaceAll to true.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            string updated = replaceAll
                ? content.Replace(find, replace)
                : ReplaceFirst(content, find, replace);

            File.WriteAllText(fullPath, updated);
            _changedFiles.Add(relativePath);
            string result = $"Success: replaced {(replaceAll ? occurrences : 1)} occurrence(s) in '{relativePath}'.";
            OnProgress?.Invoke("ToolOutput", result);
            return result;
        }
        catch (Exception ex)
        {
            string err = $"Error editing file: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string text, string find, string replace)
    {
        int index = text.IndexOf(find, StringComparison.Ordinal);
        return index < 0 ? text : text.Substring(0, index) + replace + text.Substring(index + find.Length);
    }

    [Description("Attaches an existing image file to the conversation so the user can see it inline. Use this to show the user any picture you created or found (e.g. a generated chart, a screenshot, or an image in their Downloads folder).")]
    public string AttachImage([Description("Path to the image file: relative to the workspace, or an absolute path anywhere on the machine (e.g. C:\\Users\\<name>\\Downloads\\pic.png).")] string relativePath)
    {
        OnProgress?.Invoke("ToolCall", $"Attaching image '{relativePath}'...");
        try
        {
            if (!TryResolvePath(relativePath, out string fullPath, out string pathErr))
            {
                OnProgress?.Invoke("Error", pathErr);
                return pathErr;
            }

            if (!File.Exists(fullPath))
            {
                string err = $"Error: Image '{relativePath}' does not exist.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            var ext = Path.GetExtension(fullPath);
            if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                string err = $"Error: '{relativePath}' is not a supported image type ({string.Join(", ", ImageExtensions)}).";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            OnImageSaved?.Invoke(Path.GetFileName(fullPath), fullPath);
            string msg = $"Success: attached '{relativePath}' to the conversation.";
            OnProgress?.Invoke("ToolOutput", msg);
            return msg;
        }
        catch (Exception ex)
        {
            string err = $"Error attaching image: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    // Session 0 is the non-interactive services session with no desktop. Note that
    // Environment.UserInteractive is unreliable on modern .NET (returns true even for
    // services), so we query the session id directly.
    private static bool IsRunningInSessionZero()
    {
        try
        {
            return ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId) && sessionId == 0;
        }
        catch
        {
            return false;
        }
    }

    [Description("Captures a screenshot of the computer screen and saves it as a PNG file in the workspace.")]
    public string TakeScreenshot([Description("The filename to save the screenshot as (e.g. 'screen.png').")] string fileName)
    {
        OnProgress?.Invoke("ToolCall", $"Taking screenshot, saving to '{fileName}'...");
        try
        {
            // A blank capture means there is no interactive desktop. The most common cause is
            // the worker running as a Windows Service in session 0.
            if (IsRunningInSessionZero())
            {
                string err = "Error: Cannot capture a screenshot because the agent is running in session 0 (as a Windows Service), which has no desktop. Run the worker as a normal app in your logged-in, unlocked session to enable screenshots.";
                OnProgress?.Invoke("Error", err);
                return err;
            }

            if (string.IsNullOrWhiteSpace(fileName)) fileName = "screenshot.png";
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";

            if (!TryResolvePath(fileName, out string fullPath, out string pathErr))
            {
                OnProgress?.Invoke("Error", pathErr);
                return pathErr;
            }

            // Remove any previous capture so a failed/blank run can't leave a stale image behind.
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { /* ignore */ }

            var savePath = fullPath.Replace("'", "''");
            // Run from a temp .ps1 file (no command-line quote escaping). Before capturing,
            // bind the thread to the active INPUT desktop (OpenInputDesktop/SetThreadDesktop)
            // so a process sitting in the session but on the wrong desktop (e.g. launched by a
            // service or a "run whether logged on" scheduled task) can still read the screen.
            // Make the process DPI-aware, capture the full virtual screen, and reject a uniform
            // (blank) frame instead of saving it.
            var script = $@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class Cap{{[DllImport(""user32.dll"")]public static extern bool SetProcessDPIAware();[DllImport(""user32.dll"",SetLastError=true)]public static extern IntPtr OpenInputDesktop(uint f,bool i,uint a);[DllImport(""user32.dll"",SetLastError=true)]public static extern bool SetThreadDesktop(IntPtr h);[DllImport(""user32.dll"")]public static extern bool CloseDesktop(IntPtr h);}}'
[Cap]::SetProcessDPIAware() | Out-Null
$deskBound = $false
$hDesk = [Cap]::OpenInputDesktop(0, $false, 0x0081)
if ($hDesk -ne [IntPtr]::Zero) {{ $deskBound = [Cap]::SetThreadDesktop($hDesk) }}
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
if ($b.Width -le 0 -or $b.Height -le 0) {{ Write-Error 'No screen bounds available (no interactive desktop).'; exit 1 }}
$bitmap = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($b.X, $b.Y, 0, 0, $bitmap.Size)
if ($hDesk -ne [IntPtr]::Zero) {{ [Cap]::CloseDesktop($hDesk) | Out-Null }}
$first = $bitmap.GetPixel(0,0).ToArgb()
$blank = $true
$stepX = [Math]::Max(1,[int]($b.Width/24))
$stepY = [Math]::Max(1,[int]($b.Height/24))
for ($x=0; $x -lt $b.Width -and $blank; $x+=$stepX) {{
  for ($y=0; $y -lt $b.Height; $y+=$stepY) {{
    if ($bitmap.GetPixel($x,$y).ToArgb() -ne $first) {{ $blank=$false; break }}
  }}
}}
if ($blank) {{ $graphics.Dispose(); $bitmap.Dispose(); Write-Error ('BLANK_CAPTURE bounds=' + $b.Width + 'x' + $b.Height + ' flatColor=' + $first + ' inputDesktopBound=' + $deskBound); exit 2 }}
$bitmap.Save('{savePath}', [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
";

            var scriptPath = Path.Combine(Path.GetTempPath(), $"ca_screenshot_{Guid.NewGuid():N}.ps1");
            File.WriteAllText(scriptPath, script);

            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _workspaceDir
                };

                using var process = Process.Start(processInfo);
                if (process == null)
                {
                    string err = "Error: Failed to start process for taking screenshot.";
                    OnProgress?.Invoke("Error", err);
                    return err;
                }

                var errorTask = process.StandardError.ReadToEndAsync();
                process.WaitForExit();
                string errOutput = errorTask.GetAwaiter().GetResult();

                if (process.ExitCode == 2 || errOutput.Contains("BLANK_CAPTURE"))
                {
                    ProcessIdToSessionId(GetCurrentProcessId(), out uint sid);
                    string err = $"Error: the screenshot came out blank (a single flat color). The worker's Windows session (id={sid}) has no actively-rendered desktop, so there is nothing to capture. Common causes: the session is disconnected (you RDP'd in and disconnected instead of staying connected), the machine is headless/has no monitor, or the screen is locked. To capture screenshots the worker must run on a machine with a live, connected, unlocked desktop. Diagnostics: {errOutput.Trim()}";
                    OnProgress?.Invoke("Error", err);
                    return err;
                }

                if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
                {
                    OnImageSaved?.Invoke(Path.GetFileName(fullPath), fullPath);
                    string msg = $"Success: Screenshot saved to '{fileName}' and attached to the conversation.";
                    OnProgress?.Invoke("ToolOutput", msg);
                    return msg;
                }

                string failErr = $"Error taking screenshot: the capture produced no image. Details: {errOutput}";
                OnProgress?.Invoke("Error", failErr);
                return failErr;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { /* best effort cleanup */ }
            }
        }
        catch (Exception ex)
        {
            string err = $"Error taking screenshot: {ex.Message}";
            OnProgress?.Invoke("Error", err);
            return err;
        }
    }
}
