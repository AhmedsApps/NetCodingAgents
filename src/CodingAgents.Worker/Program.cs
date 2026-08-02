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
using CodingAgents.Worker.Agents;
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
    // Wall-clock cap for a single agent run. Long tasks on slow local models can exceed
    // the default, so it is configurable via "AgentTimeoutMinutes".
    private readonly int _agentTimeoutMinutes;
    // Character budget for the message list sent to a model on each call. Roughly 4 chars
    // per token, so the default is about 12k tokens. Configurable via "MaxContextChars".
    private readonly int _maxContextChars;
    // Ollama model used to embed messages for semantic recall. Set to "" to disable recall.
    private readonly string _embeddingModel;
    private readonly string _ollamaUrl;

    // Cancellation sources for workflows currently running, keyed by workflow id, so a
    // user-requested Stop can cancel the specific in-flight pipeline.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningWorkflows = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _retryWaiters = new();

    // Identifies which chat session / workflow the current agent run belongs to, so a soft
    // timeout knows where to send its "keep going?" prompt. AsyncLocal flows down the async
    // call chain and stays isolated between concurrent runs.
    private sealed record RunContext(Guid Id, bool IsWorkflow);
    private static readonly AsyncLocal<RunContext?> _runContext = new();

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
        _agentTimeoutMinutes = int.TryParse(configuration["AgentTimeoutMinutes"], out var atm) && atm > 0 ? atm : 20;
        _maxContextChars = int.TryParse(configuration["MaxContextChars"], out var mcc) && mcc > 4000 ? mcc : 48000;
        _embeddingModel = configuration["EmbeddingModel"] ?? "nomic-embed-text";
        _ollamaUrl = configuration["OllamaUrl"] ?? "http://localhost:11434/";

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

        _connection.On<Guid, string, string, string, string>("ExecuteWorkflow",
            (workflowId, originalTask, workspacePath, analystPlan, engineerPlan) =>
        {
            StartWorkflowRun(workflowId, originalTask, workspacePath, analystPlan, engineerPlan, stoppingToken);
        });

        _connection.On<Guid, string, string>("ExecuteWorkflowFollowUp", (workflowId, message, workspacePath) =>
        {
            // A follow-up is a new instruction, so the team re-plans from scratch.
            StartWorkflowRun(workflowId, message, workspacePath, string.Empty, string.Empty, stoppingToken);
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
        _runContext.Value = new RunContext(sessionId, false);

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

            string instructions = ChatAgent.Instructions;

            // The interactive chat agent gets the full tool set, plus screenshot/image
            // attaching and the durable memory tools the pipeline agents do not have.
            var chatTools = FullDevTools(tools);
            chatTools.Add(AIFunctionFactory.Create(tools.TakeScreenshot));
            chatTools.Add(AIFunctionFactory.Create(tools.AttachImage));

            chatTools.Add(AIFunctionFactory.Create(
                async (string topic, string content) =>
                {
                    try
                    {
                        await _connection.SendAsync("SaveMemoryFact", workspaceDir, topic, content);
                        return $"Remembered '{topic}'.";
                    }
                    catch (Exception ex) { return "Could not save that: " + ex.Message; }
                },
                "RememberFact",
                "Store a durable fact about this project (conventions, architecture, decisions, preferences) that should be remembered in future conversations. Saving the same topic again replaces it."));

            chatTools.Add(AIFunctionFactory.Create(
                async () =>
                {
                    try
                    {
                        var facts = await _connection.InvokeAsync<List<MemoryFactDto>>("GetMemoryFacts", workspaceDir);
                        return facts.Count == 0
                            ? "No facts stored yet."
                            : string.Join(Environment.NewLine, facts.Select(f => $"- {f.Topic}: {f.Content}"));
                    }
                    catch (Exception ex) { return "Could not read memory: " + ex.Message; }
                },
                "RecallFacts",
                "List durable facts previously stored about this project."));

            // Seed the agent with what it already knows about this project.
            try
            {
                var facts = await _connection.InvokeAsync<List<MemoryFactDto>>("GetMemoryFacts", workspaceDir, ct);
                if (facts.Count > 0)
                {
                    instructions += "\n\nWhat you already know about this project (from earlier sessions):\n"
                        + string.Join("\n", facts.Take(40).Select(f => $"- {f.Topic}: {f.Content}"));
                }
            }
            catch (Exception ex) { _logger.LogDebug("Could not load memory facts: {Message}", ex.Message); }

            var agent = CreateAgent(chatClient, instructions, ChatAgent.Name, chatTools);

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
                    // Include only the most recent turns that fit a share of the context
                    // budget. Without this the whole session history is replayed on every
                    // message and eventually overflows the model's window.
                    var prior = convo.Take(convo.Count - 1).ToList();
                    int budget = _maxContextChars / 2;
                    var selected = new List<PersistedMessage>();
                    for (int i = prior.Count - 1; i >= 0; i--)
                    {
                        int len = (prior[i].Content?.Length ?? 0) + prior[i].Role.Length + 4;
                        if (selected.Count > 0 && budget - len < 0) break;
                        budget -= len;
                        selected.Insert(0, prior[i]);
                    }

                    int omitted = prior.Count - selected.Count;
                    var joined = string.Join("\n\n", selected.Select(m => $"{m.Role}: {m.Content}"));

                    // Long-term memory: a rolling summary of what fell out of the window,
                    // plus any older messages semantically related to this request.
                    var sections = new List<string>();

                    var summary = await _connection.InvokeAsync<string>("GetSessionSummary", sessionId, ct);
                    if (!string.IsNullOrWhiteSpace(summary))
                        sections.Add($"Summary of earlier conversation:\n{summary}");

                    var recalled = await RecallRelevantAsync(sessionId, content, 4, ct);
                    if (!string.IsNullOrWhiteSpace(recalled))
                        sections.Add(recalled);

                    sections.Add($"Recent conversation:\n{joined}");
                    sections.Add($"Current user message:\n{content}");
                    prompt = string.Join("\n\n", sections);

                    // Fold anything newly dropped into the rolling summary so it is not lost.
                    if (omitted > 0)
                    {
                        var dropped = prior.Take(omitted).ToList();
                        await UpdateSessionSummaryAsync(sessionId, dropped, summary, chatClient, ct);
                    }
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

            // Index this exchange so it can be recalled semantically in future turns.
            await RememberMessageAsync(sessionId, "User", content, ct);
            await RememberMessageAsync(sessionId, "Assistant", assistantContent, ct);
        }
        catch (OperationCanceledException)
        {
            // Worker shutting down or run cancelled: expected, not a failure.
            _logger.LogInformation("[ChatAgent] Run cancelled for session {Id}.", sessionId);
            try { await _connection.SendAsync("ReportWorkerProgress", sessionId, "Status", "The agent run was cancelled."); } catch {}
        }
        catch (TimeoutException tex)
        {
            _logger.LogWarning("[ChatAgent] {Message}", tex.Message);
            try { await _connection.SendAsync("ReportWorkerProgress", sessionId, "Error", "TIMEOUT: " + tex.Message); } catch {}
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ChatAgent] Error executing agent");
            try
            {
                await _connection.SendAsync("ReportWorkerProgress", sessionId, "Error", DescribeError(ex));
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

    private ChatClientAgent CreateAgent(IChatClient client, string instructions, string name, List<AITool>? tools)
    {
        // Keep every request inside the context budget. This wraps the innermost client so
        // it also covers the agent's internal tool-calling loop, which is where large tool
        // outputs accumulate over a long run.
        client = new ContextTrimmingChatClient(client, _maxContextChars);

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
    /// <summary>
    /// Turns an exception into a clear, actionable message so the user can tell a network
    /// problem from a configuration problem from a model problem, instead of reading a raw
    /// exception string.
    /// </summary>
    private string DescribeError(Exception ex)
    {
        // Walk to the innermost cause; the useful detail is usually at the bottom.
        var root = ex;
        while (root.InnerException != null) root = root.InnerException;

        switch (ex)
        {
            case TimeoutException:
                return "TIMEOUT: " + ex.Message;

            case OperationCanceledException:
                return "CANCELLED: the run was stopped before it finished.";

            // Raised by CreateChatClientAsync for missing or unreachable models.
            case InvalidOperationException:
                return "CONFIGURATION: " + ex.Message;

            case HttpRequestException http:
            {
                var m = http.Message;
                var lower = m.ToLowerInvariant();
                if (m.Contains("401") || m.Contains("403") || lower.Contains("unauthorized"))
                    return "AUTHENTICATION: the AI provider rejected the API key. Check the key and base URL under Settings > Model Configurations.";
                if (m.Contains("429"))
                    return "RATE LIMIT: the AI provider is throttling requests. Wait a moment, or switch to a different model.";
                if (m.Contains("404"))
                    return "NOT FOUND: the AI provider returned 404 - the model name or base URL is likely wrong. Details: " + m;
                if (root is System.Net.Sockets.SocketException)
                    return "NETWORK: could not reach the AI model service. Check that Ollama (or your provider URL) is running and reachable. Details: " + root.Message;
                return "NETWORK: failed talking to the AI provider. Details: " + m;
            }

            case System.Net.Sockets.SocketException:
                return "NETWORK: the connection failed or was dropped. Details: " + ex.Message;

            case IOException:
                return "WORKER FILE ERROR: a file operation failed on the worker machine. Details: " + ex.Message;

            case UnauthorizedAccessException:
                return "WORKER PERMISSION: the worker is not allowed to access that path. Details: " + ex.Message;

            case Microsoft.AspNetCore.SignalR.HubException:
                return "SERVER: the server rejected the request. Details: " + ex.Message;
        }

        if (root is System.Net.Sockets.SocketException)
            return "NETWORK: the connection to the AI service or server was dropped. Details: " + root.Message;

        return "UNEXPECTED (" + ex.GetType().Name + "): " + ex.Message;
    }

    /// <summary>
    /// Asks the user whether a long-running agent should keep going. The agent is still
    /// running while we wait, so approving costs nothing. Returns false to stop.
    /// </summary>
    private async Task<bool> AskContinueAsync(int minutes, CancellationToken ct)
    {
        var ctx = _runContext.Value;
        if (_connection == null || ctx == null) return false;   // nobody to ask -> stop

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _retryWaiters[ctx.Id] = tcs;
        try
        {
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            var message =
                $"The agent has been working for {minutes} minutes and hasn't finished yet. " +
                "It is still running - continue waiting?";
            _logger.LogInformation("[Timeout] Asking user whether to continue {Id}.", ctx.Id);
            await _connection.SendAsync("RequestContinueDecision", ctx.Id, ctx.IsWorkflow, message, ct);
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _retryWaiters.TryRemove(ctx.Id, out _);
        }
    }

    // ---- Long-term memory ----------------------------------------------------------

    /// <summary>
    /// Embeds text with Ollama. Returns null when embeddings are disabled or unavailable,
    /// in which case the caller simply skips semantic recall.
    /// </summary>
    private async Task<float[]?> EmbedAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_embeddingModel) || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(_ollamaUrl), Timeout = TimeSpan.FromSeconds(60) };
            var payload = new { model = _embeddingModel, prompt = text.Length > 8000 ? text.Substring(0, 8000) : text };
            using var resp = await http.PostAsJsonAsync("api/embeddings", payload, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (json?["embedding"] is not JsonArray arr || arr.Count == 0) return null;
            var vec = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++) vec[i] = (float)(arr[i]?.GetValue<double>() ?? 0);
            return vec;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Embedding unavailable: {Message}", ex.Message);
            return null;
        }
    }

    private static float[] ParseVector(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<float>();
        var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var v = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
        return v;
    }

    private static string FormatVector(float[] v) =>
        string.Join(",", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Finds older messages semantically related to the current one.</summary>
    private async Task<string> RecallRelevantAsync(Guid sessionId, string query, int topK, CancellationToken ct)
    {
        if (_connection == null) return string.Empty;
        var queryVec = await EmbedAsync(query, ct);
        if (queryVec == null) return string.Empty;

        try
        {
            var stored = await _connection.InvokeAsync<List<MessageEmbeddingDto>>("GetMessageEmbeddings", sessionId, ct);
            if (stored.Count == 0) return string.Empty;

            var ranked = stored
                .Select(e => (e, score: Cosine(queryVec, ParseVector(e.Vector))))
                .Where(x => x.score > 0.5)                 // ignore weak matches
                .OrderByDescending(x => x.score)
                .Take(topK)
                .ToList();

            if (ranked.Count == 0) return string.Empty;
            var lines = ranked.Select(x => $"- ({x.e.Role}) {Shorten(x.e.Content, 500)}");
            return "Possibly relevant earlier messages:\n" + string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Semantic recall skipped: {Message}", ex.Message);
            return string.Empty;
        }
    }

    private static string Shorten(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text ?? "" : text.Substring(0, max) + "...";

    /// <summary>Stores the embedding of a message so it can be recalled later.</summary>
    private async Task RememberMessageAsync(Guid sessionId, string role, string content, CancellationToken ct)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(content)) return;
        var vec = await EmbedAsync(content, ct);
        if (vec == null) return;
        try
        {
            await _connection.SendAsync("SaveMessageEmbedding", sessionId, Guid.NewGuid(), role,
                Shorten(content, 4000), FormatVector(vec), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not store embedding: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Condenses the part of a conversation that has aged out of the context window into a
    /// rolling summary, so older detail is retained instead of simply dropping off.
    /// </summary>
    private async Task UpdateSessionSummaryAsync(Guid sessionId, List<PersistedMessage> dropped,
                                                 string previousSummary, IChatClient client, CancellationToken ct)
    {
        if (_connection == null || dropped.Count == 0) return;
        try
        {
            var transcript = string.Join("\n", dropped.Select(m => $"{m.Role}: {Shorten(m.Content, 1000)}"));
            var instructions = SummarizerAgent.Instructions;
            var agent = CreateAgent(client, instructions, "Summarizer", null);
            var prompt = $"Previous summary:\n{(string.IsNullOrWhiteSpace(previousSummary) ? "(none)" : previousSummary)}\n\nNew messages:\n{transcript}";
            var summary = await RunAgentTextAsync(agent, prompt, ct, timeoutMinutes: 5);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                await _connection.SendAsync("SaveSessionSummary", sessionId, Shorten(summary, 6000),
                    dropped.Last().Timestamp, ct);
                _logger.LogInformation("[Memory] Session summary updated ({Count} messages folded in).", dropped.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Summary update skipped: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// True when a command explicitly asks its agent to look at existing files or code.
    /// The Analyst and Engineer are given inspection tools only in that case; by default
    /// they work purely from the text they were handed.
    /// </summary>
    /// <summary>
    /// Removes fenced code blocks from analysis output. The Analyst is told not to write
    /// code, but models frequently do anyway; stripping it deterministically keeps
    /// implementation decisions with the Engineer instead of leaking upstream.
    /// </summary>
    private static string StripCodeBlocks(string text, out int removed)
    {
        removed = 0;
        if (string.IsNullOrEmpty(text) || !text.Contains("```")) return text;

        // A local is required because an out parameter cannot be captured in a lambda.
        int count = 0;
        var cleaned = Regex.Replace(text, @"```[\s\S]*?```", _ =>
        {
            count++;
            return "*(implementation detail removed - the Software Engineer decides how this is built)*";
        });

        // An unterminated final fence would otherwise leave a dangling block.
        int stray = cleaned.IndexOf("```", StringComparison.Ordinal);
        if (stray >= 0)
        {
            count++;
            cleaned = cleaned.Substring(0, stray).TrimEnd();
        }

        removed = count;
        return cleaned;
    }

    private static bool RequestsCodeInspection(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.ToLowerInvariant();

        string[] phrases =
        {
            "existing code", "existing codebase", "the codebase", "current code",
            "read the file", "read file", "check the file", "check the code",
            "look at the file", "look at the code", "inspect the", "review the code",
            "review the existing", "in the repository", "in the repo", "the solution",
            "the project files", "already implemented", "refactor", "existing project"
        };
        if (phrases.Any(t.Contains)) return true;

        // An explicit file name / path is also a request to go and look at it.
        return Regex.IsMatch(text, @"\b[\w\-/\.]+\.(cs|razor|json|js|ts|html|css|xml|sql|md|py|java|cshtml)\b",
                             RegexOptions.IgnoreCase);
    }

    private async Task<string> RunAgentTextAsync(ChatClientAgent agent, string prompt, CancellationToken ct, int? timeoutMinutes = null)
    {
        int minutes = timeoutMinutes ?? _agentTimeoutMinutes;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Soft timeout: start the run and watch a timer alongside it. Reaching the deadline
        // does NOT cancel the agent - the model call keeps going while we ask the user
        // whether to keep waiting, so answering "continue" loses no work.
        var runTask = agent.RunAsync(prompt, cancellationToken: cts.Token);

        while (true)
        {
            var timer = Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token);
            var finished = await Task.WhenAny(runTask, timer);

            if (finished == runTask)
            {
                var response = await runTask;   // propagate result or failure
                return response.Text ?? "";
            }

            ct.ThrowIfCancellationRequested();

            bool keepGoing = await AskContinueAsync(minutes, ct);
            if (!keepGoing)
            {
                cts.Cancel();
                try { await runTask; } catch { /* expected cancellation */ }
                throw new TimeoutException(
                    $"The agent ran past {minutes} minutes and was stopped at your request.");
            }
            // Approved: extend by another full interval and keep waiting.
        }
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
    private void StartWorkflowRun(Guid workflowId, string task, string workspacePath,
                                 string existingAnalystPlan, string existingEngineerPlan, CancellationToken stoppingToken)
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
                await RunWorkflowPipelineAsync(workflowId, task, workspacePath, existingAnalystPlan, existingEngineerPlan, cts.Token);
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

    private async Task RunWorkflowPipelineAsync(Guid workflowId, string originalTask, string workspacePath,
                                               string existingAnalystPlan, string existingEngineerPlan, CancellationToken ct)
    {
        if (_connection == null) return;
        _logger.LogInformation("[Workflow] Received execution request for Workflow {Id}", workflowId);
        _runContext.Value = new RunContext(workflowId, true);

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

        // A brand-new project has nothing to inspect. Detect that up front so the team is
        // told to design from scratch instead of being sent to analyse code that isn't there.
        bool isNewProject;
        try
        {
            isNewProject = !Directory.EnumerateFileSystemEntries(workspaceDir)
                .Any(e => !Path.GetFileName(e).StartsWith("."));
        }
        catch { isNewProject = true; }

        // Report the resolved working directory so the UI can show where the team is working.
        await _connection.SendAsync("ReportWorkflowWorkspace", workflowId, workspaceDir);
        if (isNewProject)
        {
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "System",
                "The working folder is empty, so this is treated as a new project: the team will design it from scratch rather than analyse existing code.");
        }
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
            // A resumed workflow reuses the plan already stored in the database instead of
            // paying for the whole analysis again.
            string analystPlan;
            if (!string.IsNullOrWhiteSpace(existingAnalystPlan) && existingAnalystPlan != "No design plan generated.")
            {
                analystPlan = existingAnalystPlan;
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst",
                    "Resuming: reusing the System Analyst's existing plan.");
            }
            else
            {
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst", "System Analyst: Starting request analysis and design planning...");

            // The Analyst works from the user's written requirements only. It is handed
            // inspection tools solely when the user explicitly asked for existing code to
            // be examined, so it cannot go hunting for files by default.
            bool analystMayInspect = RequestsCodeInspection(originalTask);

            var analystInstructions = SystemAnalystAgent.Instructions(analystMayInspect);

            var analystTools = MakeTools("Analyst");
            var analystClient = await CreateChatClientAsync(settings.AnalystModel, modelConfigs);
            // With nothing on disk, inspection tools only invite pointless calls.
            var analystAgent = CreateAgent(analystClient, analystInstructions, "SystemAnalyst",
                analystMayInspect ? ReadOnlyTools(analystTools) : null);
            analystPlan = await RunAgentTextAsync(analystAgent, originalTask, ct);
            if (string.IsNullOrEmpty(analystPlan)) analystPlan = "No design plan generated.";

            // Enforce the "no code" rule rather than trusting the model to honour it.
            analystPlan = StripCodeBlocks(analystPlan, out int strippedBlocks);
            if (strippedBlocks > 0)
            {
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst",
                    $"Removed {strippedBlocks} code block(s) from the analysis - the analyst must produce requirements, not implementation.");
            }

            await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, null, "Executing", null);
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Analyst", $"System Analyst Completed Plan:\n{analystPlan}");
            }

            // 2. Engineer Review Phase — can verify the plan against real code.
            string engineerPlan;
            var engineerClientShared = await CreateChatClientAsync(settings.EngineerModel, modelConfigs);
            if (!string.IsNullOrWhiteSpace(existingEngineerPlan) && existingEngineerPlan != "No optimized prompt generated.")
            {
                engineerPlan = existingEngineerPlan;
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer",
                    "Resuming: reusing the Software Engineer's existing optimized prompt.");
            }
            else
            {
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", "Software Engineer: Reviewing analyst plan and optimizing prompt for best practices...");

            // The Engineer works from the Analyst's technical requirements only - not from
            // the user's original prompt - and inspects code only if the Analyst asked for it.
            bool engineerMayInspect = RequestsCodeInspection(analystPlan);

            var engineerInstructions = SoftwareEngineerAgent.Instructions(engineerMayInspect);

            var engineerAgent = CreateAgent(engineerClientShared, engineerInstructions, "SoftwareEngineer",
                engineerMayInspect ? ReadOnlyTools(MakeTools("Engineer")) : null);
            engineerPlan = await RunAgentTextAsync(engineerAgent, $"Technical requirements from the System Analyst:\n{analystPlan}", ct);
            if (string.IsNullOrEmpty(engineerPlan)) engineerPlan = "No optimized prompt generated.";

            await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, engineerPlan, "Executing", null);
            await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"Software Engineer Completed Optimized Prompt:\n{engineerPlan}");
            }

            // Loop State Initialization
            int iteration = 0;
            bool isApproved = false;
            string currentInstruction = engineerPlan;

            while (!isApproved)
            {
                if (iteration >= settings.MaxReviewIterations)
                {
                    await _connection.SendAsync("ReportWorkflowUpdate", workflowId, analystPlan, currentInstruction, "AwaitingRetryConfirmation", settings.DefaultExecutor);
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System",
                        "Reviewers still have comments after the maximum iterations. Waiting for your decision: retry, or accept the current version as-is.");

                    // RunContinuationsAsynchronously is essential. Without it the remainder of
                    // this pipeline resumes inline on the SignalR dispatch thread when the
                    // answer arrives, blocking the connection's message pump so its own
                    // SendAsync calls never complete - the workflow appears to hang.
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _retryWaiters[workflowId] = tcs;

                    bool shouldRetry;
                    try
                    {
                        using var reg = ct.Register(() => tcs.TrySetCanceled());
                        shouldRetry = await tcs.Task;
                    }
                    finally
                    {
                        _retryWaiters.TryRemove(workflowId, out _);
                    }

                    if (!shouldRetry)
                    {
                        // Accept the latest version regardless of outstanding review comments.
                        isApproved = true;
                        await _connection.SendAsync("ReportWorkflowLog", workflowId, "System",
                            "User accepted the current version. Remaining review comments were waived.");
                        break;
                    }

                    // Grant a fresh batch of iterations rather than a single extra pass.
                    settings.MaxReviewIterations += 3;
                    await _connection.SendAsync("ReportWorkflowLog", workflowId, "System",
                        $"User chose to retry. Granting 3 more iterations (up to {settings.MaxReviewIterations}).");
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
                    ? (isNewProject
                        ? $"This is a NEW project built from scratch. The executor created these files:\n{string.Join("\n", changedFiles)}"
                        : $"The executor reported changes to these files:\n{string.Join("\n", changedFiles)}")
                    : "The executor may have changed files in the workspace (exact list unavailable).";
                string reviewPrompt =
                    $"{changeContext}\n\n" +
                    "Inspect the ACTUAL current code using ListFiles, ReadFile, and SearchInFiles. You may run 'git diff' or 'dotnet build' via ExecuteCommand to verify. " +
                    $"Then review the changes for this task:\n{originalTask}\n\n" +
                    "When finished, call SubmitVerdict with your decision.";

                var reviewTools = MakeTools("Engineer");

                string dotNetInstructions = ReviewerAgents.DotNetInstructions;

                var dnClient = await CreateChatClientAsync(settings.DotNetReviewerModel, modelConfigs);
                var (dnApproved, dnText) = await RunReviewerAsync(dnClient, dotNetInstructions, "DotNetReviewer", reviewTools, reviewPrompt, ct);
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Engineer", $"DotNetReviewer ({(dnApproved ? "APPROVED" : "ISSUES")}):\n{dnText}");

                string archInstructions = ReviewerAgents.ArchitectInstructions;

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

                string validationInstructions = SoftwareEngineerAgent.ValidationInstructions;

                var validationAgent = CreateAgent(engineerClientShared, validationInstructions, "SoftwareEngineer", ReadOnlyTools(MakeTools("Engineer")));
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
                        var forcedAgent = CreateAgent(engineerClientShared, SoftwareEngineerAgent.ForcedFixInstructions, SoftwareEngineerAgent.Name, null);
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
                await _connection.SendAsync("ReportWorkflowLog", workflowId, "Error", DescribeError(ex));
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

        var instructions = ExecutorAgent.Instructions;

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

/// <summary>
/// Keeps the message list sent to a model inside a character budget so long agent runs do
/// not overflow the context window. Oversized individual messages (large file reads, build
/// logs) are truncated first; if the conversation is still too big, the middle is dropped
/// while the system prompt, the original task and the most recent exchanges are kept.
/// </summary>
/// <remarks>
/// Truncation mutates the message contents in place, so a shrunk history stays shrunk for
/// the rest of the run instead of being re-trimmed on every call.
/// </remarks>
public sealed class ContextTrimmingChatClient : DelegatingChatClient
{
    private readonly int _maxTotalChars;
    private readonly int _maxMessageChars;
    private readonly int _keepRecent;

    public ContextTrimmingChatClient(IChatClient innerClient, int maxTotalChars = 48000,
                                     int maxMessageChars = 8000, int keepRecent = 8)
        : base(innerClient)
    {
        _maxTotalChars = maxTotalChars;
        _maxMessageChars = maxMessageChars;
        _keepRecent = keepRecent;
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(Trim(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(Trim(messages), options, cancellationToken);

    private static int Size(ChatMessage m)
    {
        int n = 0;
        foreach (var c in m.Contents)
        {
            switch (c)
            {
                case TextContent t: n += t.Text?.Length ?? 0; break;
                case FunctionCallContent f: n += 64 + (f.Name?.Length ?? 0); break;
                case FunctionResultContent r: n += (r.Result as string)?.Length ?? 64; break;
                default: n += 32; break;
            }
        }
        return n;
    }

    // Only free text and tool output are shortened. Function-call arguments are never
    // touched, because cutting them would corrupt their JSON.
    private static void TruncateMessage(ChatMessage m, int max)
    {
        for (int i = 0; i < m.Contents.Count; i++)
        {
            if (m.Contents[i] is TextContent t && t.Text is { } s && s.Length > max)
            {
                m.Contents[i] = new TextContent(s.Substring(0, max) +
                    $"\n... [{s.Length - max} characters trimmed to fit the context window] ...");
            }
            else if (m.Contents[i] is FunctionResultContent r && r.Result is string rs && rs.Length > max)
            {
                r.Result = rs.Substring(0, max) +
                    $"\n... [{rs.Length - max} characters trimmed to fit the context window] ...";
            }
        }
    }

    private List<ChatMessage> Trim(IEnumerable<ChatMessage> source)
    {
        var messages = source.ToList();

        foreach (var m in messages) TruncateMessage(m, _maxMessageChars);
        if (messages.Sum(Size) <= _maxTotalChars) return messages;

        var systems = messages.Where(m => m.Role == ChatRole.System).ToList();
        var rest = messages.Where(m => m.Role != ChatRole.System).ToList();

        int keep = Math.Min(_keepRecent, rest.Count);
        var tail = rest.Skip(rest.Count - keep).ToList();

        // Preserve the original task, which is usually the first user message.
        var head = new List<ChatMessage>();
        var firstUser = rest.FirstOrDefault(m => m.Role == ChatRole.User);
        if (firstUser != null && !tail.Contains(firstUser)) head.Add(firstUser);

        int dropped = rest.Count - tail.Count - head.Count;

        var result = new List<ChatMessage>();
        result.AddRange(systems);
        result.AddRange(head);
        if (dropped > 0)
        {
            result.Add(new ChatMessage(ChatRole.User,
                $"[Context trimmed: {dropped} earlier messages were omitted to stay within the model's limit. " +
                "The original task and the most recent exchanges are preserved.]"));
        }
        result.AddRange(tail);

        // A tool result whose originating call was dropped is invalid to most providers,
        // so remove any orphans (and messages left empty as a result).
        var callIds = new HashSet<string>(
            result.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(f => f.CallId));
        foreach (var m in result)
        {
            for (int i = m.Contents.Count - 1; i >= 0; i--)
            {
                if (m.Contents[i] is FunctionResultContent fr && !callIds.Contains(fr.CallId))
                    m.Contents.RemoveAt(i);
            }
        }
        result.RemoveAll(m => m.Contents.Count == 0);
        return result;
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
