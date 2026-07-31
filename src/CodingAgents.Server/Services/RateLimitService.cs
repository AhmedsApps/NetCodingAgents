using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodingAgents.Server.Services;

public class RateLimitService : BackgroundService
{
    private readonly string _claudeDir;
    private readonly string _logPath;
    private readonly string _tokensPath;
    private readonly EmailService? _emailService;
    private readonly WhatsAppService? _whatsappService;
    private readonly WorkflowManager _workflowManager;
    private readonly ILogger<RateLimitService> _logger;
    private bool _wasBlocked = false;

    public RateLimitService(
        IOptions<AppSettings> settings,
        EmailService? emailService,
        WhatsAppService? whatsappService,
        WorkflowManager workflowManager,
        ILogger<RateLimitService> logger)
    {
        _logger = logger;
        _emailService = settings.Value.EnableEmail ? emailService : null;
        _whatsappService = settings.Value.EnableWhatsApp ? whatsappService : null;
        _workflowManager = workflowManager;

        // Resolve the sandboxed Claude Desktop folder in Local AppData
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _claudeDir = Path.Combine(localAppData, @"Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude");
        _logPath = Path.Combine(_claudeDir, @"logs\main.log");
        _tokensPath = Path.Combine(_claudeDir, "buddy-tokens.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RateLimitService started. Checking Claude limits every 5 minutes...");

        // Initial check on startup
        RunCheck();

        // Process queue on startup if available
        _ = _workflowManager.ProcessQueueAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                RunCheck();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("RateLimitService stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RateLimitService loop.");
        }
    }

    public void RunCheck()
    {
        _logger.LogInformation("Running Claude Desktop rate limit checks...");

        // 1. Check Token Usage
        CheckTokenUsage();

        // 2. Check Session Limit Warnings
        CheckSessionLimit();
    }

    private void CheckTokenUsage()
    {
        if (!File.Exists(_tokensPath))
        {
            _logger.LogWarning("Token Usage: 'buddy-tokens.json' not found (no tokens used yet today).");
            return;
        }

        try
        {
            string jsonText = File.ReadAllText(_tokensPath);
            using JsonDocument doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.TryGetProperty("tokens-today", out JsonElement tokensToday))
            {
                string date = tokensToday.GetProperty("date").GetString() ?? "";
                int tokens = tokensToday.GetProperty("tokens").GetInt32();
                _logger.LogInformation("Token Usage: {Tokens:N0} tokens used today ({Date}).", tokens, date);
            }
            else
            {
                _logger.LogWarning("Token Usage: Unable to parse token data from file.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading buddy-tokens.json");
        }
    }

    private void CheckSessionLimit()
    {
        if (!File.Exists(_logPath))
        {
            _logger.LogWarning("Log File: 'main.log' not found at '{Path}'.", _logPath);
            return;
        }

        try
        {
            // Open with ReadWrite sharing to avoid conflicts with Claude Desktop locking the log file
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);

            string? lastLimitLine = null;
            string? line;

            // Scan the file for session limit warnings from CCD CycleHealth
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("session limit") && line.Contains("[CCD CycleHealth]"))
                {
                    lastLimitLine = line;
                }
            }

            bool isBlockedNow = false;
            string? limitMsg = null;
            DateTime localLogTime = DateTime.MinValue;

            if (lastLimitLine != null)
            {
                // Parse the log line (e.g. "2026-06-25 00:34:35")
                string utcTimeStr = lastLimitLine.Substring(0, 19);

                int msgIndex = lastLimitLine.IndexOf("api_error (success):");
                if (msgIndex != -1)
                {
                    limitMsg = lastLimitLine.Substring(msgIndex + "api_error (success):".Length).Trim();
                }
                else
                {
                    int cycleIndex = lastLimitLine.IndexOf("[CCD CycleHealth]");
                    if (cycleIndex != -1)
                    {
                        limitMsg = lastLimitLine.Substring(cycleIndex + "[CCD CycleHealth]".Length).Trim();
                    }
                }

                if (limitMsg != null)
                {
                    limitMsg = Regex.Replace(limitMsg, @"[^\u0020-\u007E]", "—");
                }

                if (DateTime.TryParse(utcTimeStr, out DateTime utcTime))
                {
                    localLogTime = utcTime.ToLocalTime();
                    TimeSpan elapsed = DateTime.Now - localLogTime;

                    // Session limits typically last up to 5 hours. We display the active warning if it occurred within the last 5 hours.
                    if (elapsed.TotalHours <= 5)
                    {
                        isBlockedNow = true;
                    }
                }
            }

            // Detect state transitions for single alerts
            if (isBlockedNow && !_wasBlocked)
            {
                _wasBlocked = true;
                _logger.LogWarning("RateLimit Transition: Claude Desktop is now BLOCKED!");

                string alertText = $"⚠️ WARNING: Claude Desktop rate limit reached.\n" +
                                   $"Status: {limitMsg}\n" +
                                   $"Logged at: {localLogTime:HH:mm:ss} (Local Time)";

                if (_whatsappService != null)
                {
                    _ = _whatsappService.SendNotificationAsync(alertText);
                }

                if (_emailService != null)
                {
                    string emailBody = $"{alertText}\n\n" +
                                       $"You can reply directly to this email to continue coding. Your local C# Agent is online " +
                                       $"and has access to read, write, and execute commands in your workspace directory!";
                    _ = _emailService.SendNotificationAsync("[Alert] ⚠️ Claude Desktop Rate Limit Reached", emailBody);
                }
            }
            else if (!isBlockedNow && _wasBlocked)
            {
                _wasBlocked = false;
                _logger.LogInformation("RateLimit Transition: Claude Desktop is now AVAILABLE!");

                string alertText = $"🎉 SUCCESS: Your Claude Desktop session rate limit has reset! You can now resume coding in the GUI.";

                if (_whatsappService != null)
                {
                    _ = _whatsappService.SendNotificationAsync(alertText);
                }

                if (_emailService != null)
                {
                    _ = _emailService.SendNotificationAsync("[Status] 🎉 Claude Desktop Rate Limit Reset", alertText);
                }

                // Process queued tasks now that limits recovered
                _ = _workflowManager.ProcessQueueAsync();
            }

            // Log current status
            if (isBlockedNow)
            {
                _logger.LogWarning("STATUS: Claude Desktop session limit reached. Msg: {Msg}, Time: {Time}", limitMsg, localLogTime);
            }
            else
            {
                _logger.LogInformation("STATUS: Claude Desktop is available.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing main.log");
        }
    }
}
