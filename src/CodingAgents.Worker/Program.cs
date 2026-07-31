using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Microsoft.Agents.AI;
using CodingAgents.Shared;
using CodingAgents.Worker.Tools;
using System.Diagnostics;
using OpenAI;
using System.ClientModel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Collections.Concurrent;

var builder = Host.CreateApplicationBuilder(args);

// Configure as a Windows Service if running as one
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CodingAgentsWorker";
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private HubConnection? _connection;
    private readonly string _serverUrl;
    private readonly string _workspaceRoot;
    private readonly string _workerKey;

    // Cancellation sources for workflows currently running, keyed by workflow id, so a
    // user-requested Stop can cancel the specific in-flight pipeline.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningWorkflows = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _retryWaiters = new();

    // Rate limit monitoring fields (copied from original RateLimitService)
    private readonly string _claudeDir;
    private readonly string _logPath;
    private readonly string _tokensPath;
    private bool _wasBlocked = false;

    public Worker(ILogger<Worker> logger, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _logger = logger;
        // Fallback used only when "ServerUrl" is missing from appsettings.json.
        // REPLACE WITH: your own server URL if you want a different built-in default.
        _serverUrl = configuration["ServerUrl"] ?? "http://localhost:5111/";
        // Shared secret proving to the server that this really is the local agent worker.
        // Must match the server's "WorkerKey" setting.
        _workerKey = configuration["WorkerKey"] ?? "change-me-worker-key";

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // Root under which each chat session / workflow gets its own isolated working folder.
        // Configurable via the "WorkspaceRoot" setting.
        _workspaceRoot = configuration["WorkspaceRoot"]
            ?? Path.Combine(localAppData, "CodingAgents", "Workspaces");

        _claudeDir = Path.Combine(localAppData, @"Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude");
        _logPath = Path.Combine(_claudeDir, @"logs\main.log");
        _tokensPath = Path.Combine(_claudeDir, "buddy-tokens.json");
    }

    // Returns (creating if needed) the isolated working folder for a task, so each chat
    // session and each workflow operates in its own folder instead of one shared directory.
    private string GetTaskWorkspace(string kind, Guid id)
    {
        var dir = Path.Combine(_workspaceRoot, $"{kind}-{id}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CodingAgents Worker starting...");
        
        // 1. Initialize SignalR Connection
        var hubUrl = $"{_serverUrl.TrimEnd('/')}/chathub";
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        // Register event handlers
        _connection.On<Guid, string>("ExecuteChatAgent", (sessionId, content) =>
        {
            _ = Task.Run(() => RunChatAgentAsync(sessionId, content, stoppingToken), stoppingToken);
        });

        _connection.On<Guid, string, string>("ExecuteWorkflow", (workflowId, originalTask, workspacePath) =>
        {
            StartWorkflowRun(workflowId, originalTask, workspacePath, stoppingToken);
        });

        _connection.On<Guid, string, string>("ExecuteWorkflowFollowUp", (workflowId, message, workspacePath) =>
        {
            StartWorkflowRun(workflowId, message, workspacePath, stoppingToken);
        });

        _connection.On<Guid>("CancelWorkflow", (workflowId) =>
        {
            if (_runningWorkflows.TryGetValue(workflowId, out var cts))
            {
                _logger.LogInformation("[Workflow] Cancellation requested for {Id}", workflowId);
                try { cts.Cancel(); } catch { /* already disposed */ }
            }
            return Task.CompletedTask;
        });

        _connection.On<Guid, bool>("OnRetryDecision", (workflowId, retry) =>
        {
            if (_retryWaiters.TryGetValue(workflowId, out var tcs))
            {
                tcs.TrySetResult(retry);
            }
        });

        // A file the user attached in chat: drop it into the session's workspace folder so
        // the agent can read it by name (e.g. ReadFile / AttachImage).
        _connection.On<Guid, string, string>("ReceiveChatFile", async (sessionId, fileName, base64) =>
        {
            try
            {
                var dir = GetTaskWorkspace("session", sessionId);
                var safe = Path.GetFileName(fileName);
                if (string.IsNullOrWhiteSpace(safe)) safe = "upload.bin";
                await File.WriteAllBytesAsync(Path.Combine(dir, safe), Convert.FromBase64String(base64));
                _logger.LogInformation("[ChatFile] Saved uploaded file '{File}' to session workspace.", safe);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed saving uploaded chat file.");
            }
        });

        _connection.On<string, List<string>>("GetInstalledModels", async (baseUrl) =>
        {
            try
            {
                var targetUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434/" : baseUrl;
                var httpClient = new HttpClient { BaseAddress = new Uri(targetUrl) };
                var ollamaClient = new OllamaApiClient(httpClient);
                var models = await ollamaClient.ListLocalModelsAsync();
                return models.Select(m => m.Name).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get local model list");
                return new List<string> { "Error loading models: " + ex.Message };
            }
        });

        _connection.On<string>("GetActiveModel", () =>
        {
            return "Dynamic via ModelConfigurations";
        });

        _connection.On<string>("SetActiveModel", (modelName) =>
        {
            _logger.LogInformation("SetActiveModel is obsolete. Use ModelConfigurations in the UI instead.");
            return Task.CompletedTask;
        });

        _connection.Closed += async (error) =>
        {
            _logger.LogWarning("Connection closed. Error: {Error}. Starting infinite reconnection loop...", error?.Message);
            await StartInfiniteReconnectionLoop(stoppingToken);
        };

        _connection.Reconnected += async (connectionId) =>
        {
            _logger.LogInformation("SignalR automatically reconnected. Registering worker...");
            if (await _connection.InvokeAsync<bool>("RegisterWorker", _workerKey, stoppingToken))
            {
                _logger.LogInformation("Worker re-registered successfully with cloud hub.");
            }
            else
            {
                _logger.LogError("Worker registration REJECTED: the WorkerKey does not match the server's. Set the same 'WorkerKey' in both appsettings.json files.");
            }
        };

        // 2. Initial connection attempt
        await StartInfiniteReconnectionLoop(stoppingToken);

        // 3. Start local rate limit monitoring loop
        _logger.LogInformation("Rate limit checker started. Checking Claude limits every 5 minutes...");
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunRateLimitCheckAsync();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker rate limit check thread stopping.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in local rate limit monitoring loop.");
        }
    }

    private async Task StartInfiniteReconnectionLoop(CancellationToken stoppingToken)
    {
        if (_connection == null) return;

        while (!stoppingToken.IsCancellationRequested && _connection.State != HubConnectionState.Connected)
        {
            try
            {
                _logger.LogInformation("Connecting to cloud coordinator at {Url}...", _serverUrl);
                await _connection.StartAsync(stoppingToken);
                _logger.LogInformation("SignalR Connection established successfully! Registering worker...");
                
                if (!await _connection.InvokeAsync<bool>("RegisterWorker", _workerKey, stoppingToken))
                {
                    _logger.LogError("Worker registration REJECTED: the WorkerKey does not match the server's. Set the same 'WorkerKey' in both appsettings.json files. Retrying in 30 seconds...");
                    await _connection.StopAsync(stoppingToken);
                    await Task.Delay(30000, stoppingToken);
                    continue;
                }
                _logger.LogInformation("Worker registered successfully with cloud hub.");

                // Let the server know we are online to process queued tasks
                await _connection.InvokeAsync("ProcessQueue", stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError("Connection failed: {Message}. Retrying in 10 seconds...", ex.Message);
                await Task.Delay(10000, stoppingToken);
            }
        }
    }

    private async Task RunChatAgentAsync(Guid sessionId, string content, CancellationToken ct)
    {
        if (_connection == null) return;
        _logger.LogInformation("[ChatAgent] Received execution request for Session {Id}", sessionId);

        try
        {
            var settings = await _connection.InvokeAsync<SystemSettings>("GetSettings", ct);
            var modelConfigs = await _connection.InvokeAsync<List<ModelConfiguration>>("GetModelConfigurations", ct);
            IChatClient chatClient = await CreateChatClientAsync(settings.ChatModel, modelConfigs);

            var workspaceDir = GetTaskWorkspace("session", sessionId);
            var tools = new WorkspaceTools(workspaceDir);
            tools.OnProgress += async (type, info) =>
            {
                _logger.LogInformation("[ChatAgent] Progress [{Type}]: {Info}", type, info);
                try
                {
                    await _connection.SendAsync("ReportWorkerProgress", sessionId, type, info);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed sending progress report.");
                }
            };
            tools.OnImageSaved += async (fileName, fullPath) =>
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(fullPath);
                    var base64 = Convert.ToBase64String(bytes);
                    await _connection.SendAsync("ReportWorkerImage", sessionId, fileName, base64);
                    _logger.LogInformation("[ChatAgent] Uploaded image '{File}' ({Bytes} bytes) to server.", fileName, bytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed uploading image to server.");
                }
            };

            var instructions = @"You are a local developer agent. You have direct access to the user's project workspace using tools.
You are communicating with the user remotely via this chat interface. You can receive instructions from the user, inspect their codebase, edit files, and run build or test commands.

Available tools:
- ListFiles: Lists all files in the project workspace (excluding bin, obj, git directories). Call this to understand the project structure.
- SearchInFiles: Searches file contents for a regular expression and returns matching files and line numbers. Use this to locate code instead of reading every file.
- ReadFile: Reads the contents of a specific file. Use it to check code implementation.
- WriteFile: Creates or overwrites an entire file. Use this for new files.
- EditFile: Replaces an exact block of text in an existing file. Prefer this over WriteFile for small changes so you don't rewrite the whole file.
- ExecuteCommand: Runs commands like 'dotnet build' or 'dotnet test' in the workspace. Always run this to verify your changes compile and pass tests. Do not run long-lived processes (dev servers, watchers); they will time out.
- TakeScreenshot: Captures a screenshot of the computer screen and saves it as a PNG file in the workspace. It is automatically shown to the user in the chat.
- AttachImage: Shows an existing image file from the workspace to the user in the chat. Use this whenever the user asks to see a picture, chart, or any image you created or found.

This workspace is a dedicated folder for this conversation only; it starts empty unless you create files in it. Files the user attaches in the chat are saved here under their original name, so use ListFiles then ReadFile to open them. Relative paths resolve inside this folder, but you can read, write, or attach files anywhere on the machine by passing an absolute path (e.g. the user's Downloads folder). If you don't know the exact path, use ExecuteCommand (e.g. 'dir $env:USERPROFILE\Downloads') to find it.

Guidelines:
1. When the user asks you to perform a task, use the tools to examine the relevant files, make the edits, compile the code to check for errors, and verify the changes.
2. When you produce or are asked for an image (a screenshot, a generated chart, etc.), attach it with TakeScreenshot or AttachImage so the user can actually see it.
3. Report the final status, code changes, and test results back to the user clearly.
4. Be concise and write standard, clean C# code.";

            // The interactive chat agent additionally gets the screenshot + image-attach tools
            // (exposed as quick-command buttons in the UI); the headless workflow agents do not.
            var chatTools = FullDevTools(tools);
            chatTools.Add(AIFunctionFactory.Create(tools.TakeScreenshot));
            chatTools.Add(AIFunctionFactory.Create(tools.AttachImage));
            var agent = CreateAgent(chatClient, instructions, "WorkspaceAgent", chatTools);

            await _connection.SendAsync("ReportWorkerProgress", sessionId, "Status", "Agent executing local query...");

            // Load prior turns so the agent has conversation memory. The current user
            // message was already persisted by the hub, so it's the last entry.
            string prompt = content;
            try
            {
                var history = await _connection.InvokeAsync<List<PersistedMessage>>("GetMessages", sessionId, ct);
                var convo = history.Where(m => m.Role is "User" or "Assistant").ToList();
                if (convo.Count > 1)
                {
                    var prior = string.Join("\n\n", convo.Take(convo.Count - 1).Select(m => $"{m.Role}: {m.Content}"));
                    prompt = $"Conversation so far:\n{prior}\n\nCurrent user message:\n{content}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ChatAgent] Could not load conversation history; proceeding without it.");
            }

            var assistantContent = await RunAgentTextAsync(agent, prompt, ct);
            if (string.IsNullOrEmpty(assistantContent)) assistantContent = "No text response generated by the agent.";

            _logger.LogInformation("[ChatAgent] Execution completed successfully.");
            await _connection.SendAsync("ReportWorkerResponse", sessionId, assistantContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChatAgent] Error executing agent");
            try
            {
                await _connection.SendAsync("ReportWorkerProgress", sessionId, "Error", $"Agent Execution Error: {ex.Message}\n{ex.StackTrace}");
            }
            catch {}
        }
    }

    private async Task<IChatClient> CreateChatClientAsync(string modelSettingName, List<ModelConfiguration> configs)
    {
        var config = configs.FirstOrDefault(c => c.Name.Equals(modelSettingName, StringComparison.OrdinalIgnoreCase));

        string ollamaBaseUrl;
        string targetModel;

        if (config != null)
        {
            if (config.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return new OpenAIRestChatClient(config.ApiKey ?? "", config.BaseUrl ?? "", config.ModelName);
            }
            if (config.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return new AnthropicRestChatClient(config.ApiKey ?? "", config.BaseUrl ?? "", config.ModelName);
            }

            // Ollama provider
            ollamaBaseUrl = string.IsNullOrWhiteSpace(config.BaseUrl) ? "http://localhost:11434/" : config.BaseUrl;
            targetModel = config.ModelName;
        }
        else
        {
            // Legacy: a settings value like "Ollama:llama3.2:latest" with no matching
            // ModelConfiguration row. Treat it as an Ollama model name against the default host.
            ollamaBaseUrl = "http://localhost:11434/";
            targetModel = modelSettingName.StartsWith("Ollama:", StringComparison.OrdinalIgnoreCase)
                ? modelSettingName.Substring("Ollama:".Length)
                : modelSettingName;
        }

        if (string.IsNullOrWhiteSpace(targetModel))
        {
            throw new InvalidOperationException(
                $"No Ollama model name is configured for setting '{modelSettingName}'. Configure it in the model settings.");
        }

        var httpClient = new HttpClient { BaseAddress = new Uri(ollamaBaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        var ollamaClient = new OllamaApiClient(httpClient);

        // Verify the configured model is actually installed. Previously the code silently
        // substituted a hardcoded fallback model, which meant the database setting could be
        // ignored at runtime. Now we fail loudly so the configured model stays authoritative.
        List<string> localModelNames;
        try
        {
            var localModels = await ollamaClient.ListLocalModelsAsync();
            localModelNames = localModels.Select(m => m.Name).ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not query Ollama at {ollamaBaseUrl} to verify model '{targetModel}': {ex.Message}", ex);
        }

        if (!localModelNames.Contains(targetModel))
        {
            var available = localModelNames.Count > 0 ? string.Join(", ", localModelNames) : "none";
            throw new InvalidOperationException(
                $"Configured Ollama model '{targetModel}' is not installed at {ollamaBaseUrl}. " +
                $"Install it (e.g. 'ollama pull {targetModel}') or choose an installed model. Available: {available}.");
        }

        ollamaClient.SelectedModel = targetModel;
        return ollamaClient;
    }

    // ---- Agent construction helpers -------------------------------------------------

    // Read-only inspection tools (safe for planners and reviewers).
    private static List<AITool> ReadOnlyTools(WorkspaceTools t) => new()
    {
        AIFunctionFactory.Create(t.ListFiles),
        AIFunctionFactory.Create(t.ReadFile),
        AIFunctionFactory.Create(t.SearchInFiles),
    };

    // Read tools plus the ability to run build/test/git commands (for reviewers).
    private static List<AITool> InspectionTools(WorkspaceTools t)
    {
        var list = ReadOnlyTools(t);
        list.Add(AIFunctionFactory.Create(t.ExecuteCommand));
        return list;
    }

    // Full developer tool set including file mutation (for the chat + executor agents).
    private static List<AITool> FullDevTools(WorkspaceTools t)
    {
        var list = InspectionTools(t);
        list.Add(AIFunctionFactory.Create(t.WriteFile));
        list.Add(AIFunctionFactory.Create(t.EditFile));
        return list;
    }

    private static ChatClientAgent CreateAgent(IChatClient client, string instructions, string name, List<AITool>? tools)
    {
        if (tools is { Count: > 0 })
        {
            // Wrap so weak local models that emit tool calls as raw JSON text still work.
            client = new OllamaToolParsingChatClient(client);
            return new ChatClientAgent(client, instructions: instructions, name: name, tools: tools);
        }
        return new ChatClientAgent(client, instructions: instructions, name: name);
    }

    // Runs an agent with an overall wall-clock cap (linked to the caller's token) so a model
    // stuck repeatedly calling tools can't run forever, and returns its text output.
    private static async Task<string> RunAgentTextAsync(ChatClientAgent agent, string prompt, CancellationToken ct, int timeoutMinutes = 20)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        var response = await agent.RunAsync(prompt, cancellationToken: cts.Token);
        return response.Text ?? "";
    }

    // Captures a reviewer's structured verdict submitted via the SubmitVerdict tool.
    private sealed class VerdictBox
    {
        public bool? Approved;
        public string Summary = "";
    }

    // Runs a reviewer agent that can inspect the workspace and must submit a structured
    // verdict. Falls back to the legacy [APPROVED] sentinel if the model answers in prose.
    private async Task<(bool approved, string text)> RunReviewerAsync(
        IChatClient client, string instructions, string name,
        WorkspaceTools tools, string prompt, CancellationToken ct)
    {
        var verdict = new VerdictBox();

        var reviewTools = InspectionTools(tools);
        reviewTools.Add(AIFunctionFactory.Create(
            (bool approved, string summary) =>
            {
                verdict.Approved = approved;
                verdict.Summary = summary ?? "";
                return "Verdict recorded.";
            },
            "SubmitVerdict",
            "Call this exactly once, after you have inspected the actual changes, to record your final decision. Set approved=true only if the code is acceptable as-is; set approved=false if any changes are required. Put a brief explanation in summary."));

        var agent = CreateAgent(client, instructions, name, reviewTools);
        string text = await RunAgentTextAsync(agent, prompt, ct);
        if (!string.IsNullOrWhiteSpace(verdict.Summary))
        {
            text = string.IsNullOrWhiteSpace(text) ? verdict.Summary : $"{verdict.Summary}\n\n{text}";
        }

        // Prefer the structured verdict; only fall back to sentinel parsing (requiring an
        // explicit approval that isn't contradicted by an issues flag) if the tool wasn't called.
        bool approved = verdict.Approved
            ?? (text.Contains("[APPROVED]") && !text.Contains("[ISSUES_FOUND]"));

        return (approved, text);
    }

    // Wraps a workflow run in its own cancellation source (linked to shutdown) so a user
    // Stop request can cancel this specific pipeline, and cleans it up when finished.
    private void StartWorkflowRun(Guid workflowId, string task, string workspacePath, CancellationToken stoppingToken)
    {
        // Cancel any prior in-flight run for the same workflow so a follow-up or re-dispatch
        // doesn't spawn a duplicate concurrent pipeline.
        if (_runningWorkflows.TryGetValue(workflowId, out var prev))
        {
            try { prev.Cancel(); } catch { /* already disposed */ }
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _runningWorkflows[workflowId] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunWorkflowPipelineAsync(workflowId, task, workspacePath, cts.Token);
            }
            finally
            {
                if (_runningWorkflows.TryGetValue(workflowId, out var existing) && existing == cts)
                {
                    _runningWorkflows.TryRemove(workflowId, out _);
                }
                cts.Dispose();
            }
        }, stoppingToken);
    }

    private async Task RunWorkflowPipelineAsync(Guid workflowId, string originalTask, string workspacePath, CancellationToken ct)
    {
        if (_connection == null) return;
        _logger.LogInformation("[Workflow] Received execution request for Workflow {Id}", workflowId);

        // All agents in this workflow share one isolated working folder so the executor's
        // changes are visible to the reviewers. Honor a user-chosen folder if provided,
        // otherwise use the default per-workflow folder.
        string workspaceDir;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            workspaceDir = GetTaskWorkspace("workflow", workflowId);
        }
        else
        {
            workspaceDir = Path.GetFullPath(workspacePath);
            Directory.CreateDirectory(workspaceDir);
        }

        // Report the resolved working directory so the UI can show where the team is working.
        await _connection.SendAsync("ReportWorkflowWorkspace", workflowId, workspaceDir);
        await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", $"📁 Working directory: {workspaceDir}");

        // Builds a workspace tool set whose progress is streamed to the workflow log
        // under the given stage label.
        WorkspaceTools MakeTools(string stage)
        {
            var t = new WorkspaceTools(workspaceDir);
            t.OnProgress += async (type, info) =>
            {
                try { await _connection!.SendAsync("ReportWorkflowLog", workflowId, stage, $"[{type}] {info}"); }
                catch { /* logging best effort */ }
            };
            return t;
        }

        try
        {
            var settings = await _connection.InvokeAsync<SystemSettings>("GetSettings", ct);
            var modelConfigs = await _connection.InvokeAsync<List<ModelConfiguration>>("GetModelConfigurations", ct);

            // 1. Analyst Phase — now able to actually inspect the codebase.
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst", "System Analyst: Starting request analysis and design planning...");

            var analystInstructions = @"You are a System Analyst and Software Designer. Your job is to take a user's development request, analyze the ACTUAL project structure using your tools, design a complete implementation plan, and write a detailed, step-by-step technical instruction prompt for a software engineer to execute.
Use ListFiles, SearchInFiles, and ReadFile to inspect the real codebase before planning — do not guess at the structure.
Be highly precise, list the directories/files that need modifications, and explain the architectural design decisions clearly.";

            var analystTools = MakeTools("Analyst");
            var analystClient = await CreateChatClientAsync(settings.AnalystModel, modelConfigs);
            var analystAgent = CreateAgent(analystClient, analystInstructions, "SystemAnalyst", ReadOnlyTools(analystTools));
            string analystPlan = await RunAgentTextAsync(analystAgent, originalTask, ct);
            if (string.IsNullOrEmpty(analystPlan)) analystPlan = "No design plan generated.";

            await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, null, "Executing", null);
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst", $"System Analyst Completed Plan:\n{analystPlan}");

            // 2. Engineer Review Phase — can verify the plan against real code.
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", "Software Engineer: Reviewing analyst plan and optimizing prompt for best practices...");

            var engineerInstructions = @"You are a Senior Software Engineer. Your job is to review a System Analyst's implementation plan and instruction prompt.
Use ListFiles, SearchInFiles, and ReadFile to verify the plan against the real code.
Optimize the prompt to ensure industry-standard best practices in security, performance, naming conventions, and code safety.
Output a clean, refined, and highly precise task description designed for an automated CLI agent (like Claude Code or Antigravity) to execute directly in the workspace.";

            var engineerClient = await CreateChatClientAsync(settings.EngineerModel, modelConfigs);
            var engineerAgent = CreateAgent(engineerClient, engineerInstructions, "SoftwareEngineer", ReadOnlyTools(MakeTools("Engineer")));
            string engineerPlan = await RunAgentTextAsync(engineerAgent, $"Analyst Plan:\n{analystPlan}", ct);
            if (string.IsNullOrEmpty(engineerPlan)) engineerPlan = "No optimized prompt generated.";

            await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, engineerPlan, "Executing", null);
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"Software Engineer Completed Optimized Prompt:\n{engineerPlan}");

            // Loop State Initialization
            int iteration = 0;
            bool isApproved = false;
            string currentInstruction = engineerPlan;

            while (!isApproved)
            {
                if (iteration >= settings.MaxReviewIterations)
                {
                    await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, "AwaitingRetryConfirmation", settings.DefaultExecutor);
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "Workflow reached maximum iterations. Waiting for user confirmation to retry...");
                    
                    var tcs = new TaskCompletionSource<bool>();
                    _retryWaiters[workflowId] = tcs;
                    
                    using var reg = ct.Register(() => tcs.TrySetCanceled());
                    bool shouldRetry = await tcs.Task;
                    
                    _retryWaiters.TryRemove(workflowId, out _);
                    
                    if (!shouldRetry)
                    {
                        break;
                    }
                    
                    settings.MaxReviewIterations++; // allow one more iteration
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "User approved retry. Continuing execution...");
                }

                ct.ThrowIfCancellationRequested();
                iteration++;
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, $"Applying Fixes (Iteration {iteration})", settings.DefaultExecutor);

                bool isBlocked = IsClaudeBlocked();
                if (isBlocked && settings.DefaultExecutor == "ClaudeCode")
                {
                    await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, "Queued", settings.DefaultExecutor);
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "Queue", "⚠️ Rate limit active. Task has been queued and will run automatically when limits reset.");
                    return;
                }

                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Executor", $"Starting Execution (Iteration {iteration})...");

                // 3. Executor Runs the Instruction
                IReadOnlyCollection<string> changedFiles = Array.Empty<string>();
                if (settings.DefaultExecutor == "ClaudeCode")
                {
                    await RunClaudeCodeAsync(workflowId, currentInstruction, workspaceDir, ct);
                }
                else
                {
                    var execClient = await CreateChatClientAsync(settings.ExecutorModel, modelConfigs);
                    changedFiles = await RunAntigravityAsync(workflowId, currentInstruction, execClient, workspaceDir, ct);
                }
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Executor", $"✅ Execution completed for Iteration {iteration}. Starting Code Review...");

                // 4. Code Review Phase — reviewers can now inspect the real changes.
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, $"Reviewing (Iteration {iteration})", settings.DefaultExecutor);

                string changeContext = changedFiles.Count > 0
                    ? $"The executor reported changes to these files:\n{string.Join("\n", changedFiles)}"
                    : "The executor may have changed files in the workspace (exact list unavailable).";
                string reviewPrompt =
                    $"{changeContext}\n\n" +
                    "Inspect the ACTUAL current code using ListFiles, ReadFile, and SearchInFiles. You may run 'git diff' or 'dotnet build' via ExecuteCommand to verify. " +
                    $"Then review the changes for this task:\n{originalTask}\n\n" +
                    "When finished, call SubmitVerdict with your decision.";

                var reviewTools = MakeTools("Engineer");

                string dotNetInstructions = @"You are a Senior .NET and SQL Programmer reviewing the code changes in the workspace.
Use your tools to inspect the actual code. Then submit your decision via the SubmitVerdict tool (approved=true only if the code is clean, correct, and follows best practices).
If you cannot call the tool, output exactly [APPROVED] if acceptable, otherwise [ISSUES_FOUND] followed by a detailed list of required fixes.";
                var dnClient = await CreateChatClientAsync(settings.DotNetReviewerModel, modelConfigs);
                var (dnApproved, dnText) = await RunReviewerAsync(dnClient, dotNetInstructions, "DotNetReviewer", reviewTools, reviewPrompt, ct);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"DotNetReviewer ({(dnApproved ? "APPROVED" : "ISSUES")}):\n{dnText}");

                string archInstructions = @"You are a Senior Solution Architect reviewing the codebase for structural, architectural, and scalability issues.
Use your tools to inspect the actual code. Then submit your decision via the SubmitVerdict tool (approved=true only if the design is sound and robust).
If you cannot call the tool, output exactly [APPROVED] if acceptable, otherwise [ISSUES_FOUND] followed by a detailed list of required fixes.";
                var archClient = await CreateChatClientAsync(settings.ArchitectReviewerModel, modelConfigs);
                var (archApproved, archText) = await RunReviewerAsync(archClient, archInstructions, "ArchitectureReviewer", reviewTools, reviewPrompt, ct);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"ArchitectureReviewer ({(archApproved ? "APPROVED" : "ISSUES")}):\n{archText}");

                if (dnApproved && archApproved)
                {
                    isApproved = true;
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "🎉 Both reviewers approved the code! Workflow consensus reached.");
                    break;
                }

                // 5. Validation Phase
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, $"Validating (Iteration {iteration})", settings.DefaultExecutor);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", "Software Engineer is validating the reported issues...");

                string validationInstructions = @"You are the Lead Software Engineer. Reviewers have rejected the current code and reported issues.
Use ListFiles, ReadFile, and SearchInFiles to confirm whether the reported issues are real before deciding.
If their concerns are valid, output [VALID] followed by a concrete, step-by-step instruction script for the Executor to apply the fixes.
If their concerns are false, invalid, or impossible, output [REFUSED] followed by a detailed explanation of why you refuse to fix it.";

                var validationAgent = CreateAgent(engineerClient, validationInstructions, "SoftwareEngineer", ReadOnlyTools(MakeTools("Engineer")));
                string validationText = await RunAgentTextAsync(validationAgent, $"Issues reported by reviewers:\n.NET Reviewer:\n{dnText}\n\nArchitect:\n{archText}", ct);

                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"Validation Result:\n{validationText}");

                if (validationText.Contains("[REFUSED]"))
                {
                    // 6. Refusal Assessment
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "Engineer refused the issues. Asking Reviewers to assess the refusal...");
                    string refusalAssessment = $"The Software Engineer refused your issues for this reason:\n{validationText}\nDo you accept this refusal and drop the issue? Output [ACCEPTED_REFUSAL] or [REJECTED_REFUSAL].";

                    var dnRefusalAgent = CreateAgent(dnClient, dotNetInstructions, "DotNetReviewer", ReadOnlyTools(reviewTools));
                    string dnRefusalText = await RunAgentTextAsync(dnRefusalAgent, refusalAssessment, ct);
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"DotNetReviewer on Refusal: {dnRefusalText}");

                    var archRefusalAgent = CreateAgent(archClient, archInstructions, "ArchitectureReviewer", ReadOnlyTools(reviewTools));
                    string archRefusalText = await RunAgentTextAsync(archRefusalAgent, refusalAssessment, ct);
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"ArchitectureReviewer on Refusal: {archRefusalText}");

                    if (dnRefusalText.Contains("[ACCEPTED_REFUSAL]") || archRefusalText.Contains("[ACCEPTED_REFUSAL]"))
                    {
                        isApproved = true;
                        await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "🎉 Refusal was accepted by the reviewers. Issue dropped, workflow completed!");
                        break;
                    }
                    else
                    {
                        await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "Reviewers rejected the refusal. Forcing the Engineer to generate fix instructions...");
                        var forcedAgent = CreateAgent(engineerClient, "Output only a step-by-step instruction script to fix the original issues.", "SoftwareEngineer", null);
                        string forcedText = await RunAgentTextAsync(forcedAgent, $"Issues:\n{dnText}\n{archText}", ct);
                        currentInstruction = string.IsNullOrEmpty(forcedText) ? "Fix the issues." : forcedText;
                    }
                }
                else
                {
                    // VALID issues, the text itself is the new instruction
                    currentInstruction = validationText;
                }
            }

            if (!isApproved)
            {
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, "Stalemate", settings.DefaultExecutor);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", $"⚠️ Workflow reached maximum iterations ({settings.MaxReviewIterations}) without consensus.");
            }
            else
            {
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, "Completed", settings.DefaultExecutor);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "✅ Workflow finished successfully.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Workflow] {Id} cancelled.", workflowId);
            try
            {
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, null, null, "Cancelled", null);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "System", "🛑 Workflow cancelled by user.");
            }
            catch {}
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Workflow] Error running workflow pipeline");
            try
            {
                await _connection.SendAsync("ReportWorkflowUpdate", workflowId, null, null, "Failed", null);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Error", $"Execution Error: {ex.Message}");
            }
            catch {}
        }
    }

    private async Task RunClaudeCodeAsync(Guid workflowId, string prompt, string workspaceDir, CancellationToken ct)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string claudeExePath = Path.Combine(localAppData, @"Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code\2.1.181\claude.exe");

        if (!File.Exists(claudeExePath))
        {
            throw new FileNotFoundException($"Claude CLI executable not found at '{claudeExePath}'");
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = claudeExePath,
            Arguments = $"-y \"{prompt.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspaceDir
        };

        using var process = Process.Start(processInfo);
        if (process == null) throw new Exception("Failed to start Claude CLI process.");

        // Drain stderr concurrently while we stream stdout, otherwise a full
        // stderr pipe buffer can block the child and deadlock both sides.
        var errorTask = process.StandardError.ReadToEndAsync();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(ct)) != null)
        {
            await _connection!.SendAsync("ReportWorkflowLog", workflowId, "Executor", line);
        }

        string error = await errorTask;
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0 && !string.IsNullOrEmpty(error))
        {
            throw new Exception($"Claude CLI process exited with code {process.ExitCode}. Error: {error}");
        }
    }

    // Runs the local Ollama/Antigravity executor agent and returns the set of files it changed.
    private async Task<IReadOnlyCollection<string>> RunAntigravityAsync(Guid workflowId, string prompt, IChatClient chatClient, string workspaceDir, CancellationToken ct)
    {
        var tools = new WorkspaceTools(workspaceDir);
        tools.OnProgress += async (type, info) =>
        {
            try { await _connection!.SendAsync("ReportWorkflowLog", workflowId, "Executor", $"[{type}] {info}"); }
            catch { /* logging best effort */ }
        };

        var instructions = @"You are a local developer agent. You have direct access to the user's project workspace using tools.
You are running an optimized plan generated by your software engineering team.
Use SearchInFiles and ReadFile to understand the code, EditFile for small changes and WriteFile for new files, and ExecuteCommand ('dotnet build'/'dotnet test') to verify your work compiles and passes before finishing.";

        var agent = CreateAgent(chatClient, instructions, "WorkspaceAgent", FullDevTools(tools));

        string outputText = await RunAgentTextAsync(agent, prompt, ct);
        await _connection!.SendAsync("ReportWorkflowLog", workflowId, "Executor", string.IsNullOrEmpty(outputText) ? "No text output generated." : outputText);

        return tools.ChangedFiles;
    }

    private bool IsClaudeBlocked()
    {
        if (!File.Exists(_logPath)) return false;

        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? lastLimitLine = null;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("session limit") && line.Contains("[CCD CycleHealth]"))
                {
                    lastLimitLine = line;
                }
            }

            if (lastLimitLine != null)
            {
                string utcTimeStr = lastLimitLine.Substring(0, 19);
                // The log timestamp is UTC; parse it as such and compare in UTC so the
                // 5-hour window isn't skewed by the local timezone offset.
                if (DateTime.TryParse(utcTimeStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime utcTime))
                {
                    TimeSpan elapsed = DateTime.UtcNow - utcTime;
                    if (elapsed.TotalHours is >= 0 and <= 5)
                    {
                        return true;
                    }
                }
            }
        }
        catch {}
        return false;
    }

    private async Task RunRateLimitCheckAsync()
    {
        if (_connection == null || _connection.State != HubConnectionState.Connected) return;

        bool isBlockedNow = IsClaudeBlocked();

        if (isBlockedNow && !_wasBlocked)
        {
            _wasBlocked = true;
            _logger.LogWarning("RateLimit Transition: Claude Desktop is now BLOCKED!");
            // Relay warning to server settings alerts if we want, or logs
        }
        else if (!isBlockedNow && _wasBlocked)
        {
            _wasBlocked = false;
            _logger.LogInformation("RateLimit Transition: Claude Desktop is now AVAILABLE!");
            
            // Process queued tasks on the server
            await _connection.InvokeAsync("ProcessQueue");
        }
    }

}

public class OllamaToolParsingChatClient : DelegatingChatClient
{
    public OllamaToolParsingChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        // Only attempt to reinterpret raw JSON as a tool call when tools were actually
        // offered on this request. Otherwise a model legitimately returning JSON (that
        // happens to have a "name" property) would be hijacked into a bogus tool call.
        var toolNames = new HashSet<string>(
            (options?.Tools ?? Enumerable.Empty<AITool>()).OfType<AIFunction>().Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase);
        if (toolNames.Count == 0)
        {
            return response;
        }

        if (response.Messages.Count > 0)
        {
            var message = response.Messages[0];
            if ((message.Contents.Count == 0 || message.Text != null) &&
                !message.Contents.Any(c => c is FunctionCallContent))
            {
                var text = message.Text?.Trim();
                if (!string.IsNullOrEmpty(text) && text.StartsWith("{") && text.EndsWith("}"))
                {
                    try
                    {
                        var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("name", out var nameProp)
                            && nameProp.ValueKind == JsonValueKind.String
                            && nameProp.GetString() is { Length: > 0 } name
                            && toolNames.Contains(name))
                        {
                            var args = new Dictionary<string, object?>();
                            if (root.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in argsProp.EnumerateObject())
                                {
                                    object? val = prop.Value.ValueKind switch
                                    {
                                        JsonValueKind.String => prop.Value.GetString(),
                                        JsonValueKind.Number => prop.Value.GetDouble(),
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => prop.Value.GetRawText()
                                    };
                                    args[prop.Name] = val;
                                }
                            }

                            var toolCall = new FunctionCallContent(Guid.NewGuid().ToString(), name, args);
                            message.Contents.Clear();
                            message.Contents.Add(toolCall);
                        }
                    }
                    catch
                    {
                        // Fall back to normal text if parsing fails
                    }
                }
            }
        }

        return response;
    }
}

public class AnthropicRestChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public AnthropicRestChatClient(string apiKey, string baseUrl, string model)
    {
        _model = model;
        _httpClient = new HttpClient();
        _httpClient.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com/" : baseUrl);
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public ChatClientMetadata Metadata => new("Anthropic");

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();

        var systemMsg = string.Concat(messageList
            .Where(m => m.Role == ChatRole.System)
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(t => t.Text));

        var payload = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = options?.MaxOutputTokens ?? 4096,
            ["messages"] = BuildMessages(messageList)
        };
        if (!string.IsNullOrEmpty(systemMsg)) payload["system"] = systemMsg;
        if (options?.Temperature is float temperature) payload["temperature"] = temperature;

        var tools = BuildTools(options);
        if (tools != null) payload["tools"] = tools;

        using var httpResponse = await _httpClient.PostAsJsonAsync("v1/messages", payload, cancellationToken);
        var contentStr = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic request failed ({(int)httpResponse.StatusCode}): {contentStr}");
        }

        var json = JsonNode.Parse(contentStr)!;
        var contents = new List<AIContent>();

        if (json["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                var type = block?["type"]?.GetValue<string>();
                if (type == "text")
                {
                    var text = block?["text"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));
                }
                else if (type == "tool_use")
                {
                    var id = block?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                    var name = block?["name"]?.GetValue<string>() ?? "";
                    contents.Add(new FunctionCallContent(id, name, RestChatHelpers.NodeToArguments(block?["input"])));
                }
            }
        }

        var responseMessage = new ChatMessage(ChatRole.Assistant, contents);
        var response = new ChatResponse(responseMessage);
        response.FinishReason = json["stop_reason"]?.GetValue<string>() switch
        {
            "tool_use" => ChatFinishReason.ToolCalls,
            "max_tokens" => ChatFinishReason.Length,
            _ => ChatFinishReason.Stop
        };
        return response;
    }

    private static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages.Where(m => m.Role != ChatRole.System))
        {
            // Tool results are carried on ChatRole.Tool messages; Anthropic expects those
            // as tool_result blocks inside a "user" turn.
            var role = m.Role == ChatRole.Assistant ? "assistant" : "user";
            var blocks = new JsonArray();

            foreach (var content in m.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        blocks.Add(new JsonObject { ["type"] = "text", ["text"] = tc.Text });
                        break;
                    case FunctionCallContent fc:
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = fc.CallId,
                            ["name"] = fc.Name,
                            ["input"] = JsonSerializer.SerializeToNode(fc.Arguments ?? new Dictionary<string, object?>())
                        });
                        break;
                    case FunctionResultContent fr:
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = fr.CallId,
                            ["content"] = RestChatHelpers.ResultToString(fr.Result)
                        });
                        break;
                }
            }

            if (blocks.Count == 0) continue;
            arr.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }
        return arr;
    }

    private static JsonArray? BuildTools(ChatOptions? options)
    {
        if (options?.Tools == null) return null;
        var arr = new JsonArray();
        foreach (var tool in options.Tools.OfType<AIFunction>())
        {
            arr.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
            });
        }
        return arr.Count > 0 ? arr : null;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => _httpClient.Dispose();
}

public class OpenAIRestChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OpenAIRestChatClient(string apiKey, string baseUrl, string model)
    {
        _model = model;
        _httpClient = new HttpClient();

        string endpoint = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1/" : baseUrl;
        if (!endpoint.EndsWith("/")) endpoint += "/";
        _httpClient.BaseAddress = new Uri(endpoint);

        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public ChatClientMetadata Metadata => new("OpenAI");

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = BuildMessages(messages)
        };
        if (options?.MaxOutputTokens is int maxTokens) payload["max_tokens"] = maxTokens;
        if (options?.Temperature is float temperature) payload["temperature"] = temperature;

        var tools = BuildTools(options);
        if (tools != null)
        {
            payload["tools"] = tools;
            payload["tool_choice"] = "auto";
        }

        using var httpResponse = await _httpClient.PostAsJsonAsync("chat/completions", payload, cancellationToken);
        var contentStr = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OpenAI request failed ({(int)httpResponse.StatusCode}): {contentStr}");
        }

        var json = JsonNode.Parse(contentStr)!;
        var choice = json["choices"]?[0];
        var message = choice?["message"];
        var contents = new List<AIContent>();

        var text = message?["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));

        if (message?["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var tc in toolCalls)
            {
                var id = tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();
                var fn = tc?["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var argsJson = fn?["arguments"]?.GetValue<string>() ?? "{}";
                contents.Add(new FunctionCallContent(id, name, RestChatHelpers.JsonStringToArguments(argsJson)));
            }
        }

        var responseMessage = new ChatMessage(ChatRole.Assistant, contents);
        var response = new ChatResponse(responseMessage);
        response.FinishReason = choice?["finish_reason"]?.GetValue<string>() switch
        {
            "tool_calls" => ChatFinishReason.ToolCalls,
            "length" => ChatFinishReason.Length,
            _ => ChatFinishReason.Stop
        };
        return response;
    }

    private static JsonArray BuildMessages(IEnumerable<ChatMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            // Tool results become one dedicated {role:"tool"} message per result.
            var functionResults = m.Contents.OfType<FunctionResultContent>().ToList();
            if (functionResults.Count > 0)
            {
                foreach (var fr in functionResults)
                {
                    arr.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = fr.CallId,
                        ["content"] = RestChatHelpers.ResultToString(fr.Result)
                    });
                }
                continue;
            }

            var role = m.Role == ChatRole.System ? "system"
                     : m.Role == ChatRole.User ? "user"
                     : m.Role == ChatRole.Tool ? "tool"
                     : "assistant";

            var text = string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text));
            var obj = new JsonObject { ["role"] = role };

            var functionCalls = m.Contents.OfType<FunctionCallContent>().ToList();
            if (functionCalls.Count > 0)
            {
                obj["content"] = string.IsNullOrEmpty(text) ? null : text;
                var tcArr = new JsonArray();
                foreach (var fc in functionCalls)
                {
                    tcArr.Add(new JsonObject
                    {
                        ["id"] = fc.CallId,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = fc.Name,
                            ["arguments"] = JsonSerializer.Serialize(fc.Arguments ?? new Dictionary<string, object?>())
                        }
                    });
                }
                obj["tool_calls"] = tcArr;
            }
            else
            {
                obj["content"] = text;
            }

            arr.Add(obj);
        }
        return arr;
    }

    private static JsonArray? BuildTools(ChatOptions? options)
    {
        if (options?.Tools == null) return null;
        var arr = new JsonArray();
        foreach (var tool in options.Tools.OfType<AIFunction>())
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
                }
            });
        }
        return arr.Count > 0 ? arr : null;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => _httpClient.Dispose();
}

internal static class RestChatHelpers
{
    public static string ResultToString(object? result) => result switch
    {
        null => "",
        string s => s,
        _ => JsonSerializer.Serialize(result)
    };

    public static Dictionary<string, object?> JsonStringToArguments(string argsJson)
    {
        try
        {
            return NodeToArguments(JsonNode.Parse(argsJson));
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    public static Dictionary<string, object?> NodeToArguments(JsonNode? node)
    {
        var dict = new Dictionary<string, object?>();
        if (node is JsonObject obj)
        {
            foreach (var kv in obj)
            {
                dict[kv.Key] = ConvertNode(kv.Value);
            }
        }
        return dict;
    }

    private static object? ConvertNode(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var b)) return b;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<string>(out var s)) return s;
        }
        // Nested objects/arrays are passed through as their JSON text; the function
        // invoker re-binds them against the tool's parameter schema.
        return node?.ToJsonString();
    }
}
