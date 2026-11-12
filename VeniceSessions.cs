using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudePowertoys;

/// <summary>
/// Starts Claude Code in a terminal with its model requests going to Venice AI instead of the Anthropic
/// subscription. Venice only speaks the OpenAI chat format, so a local LiteLLM proxy on 127.0.0.1
/// translates Claude Code's Anthropic Messages requests. The proxy holds the Venice key (the user's
/// VENICE_API_KEY environment variable); Claude Code gets the proxy's own key through a settings file
/// passed with --settings, so Claude Desktop and plain `claude` stay on the subscription.
/// </summary>
sealed class VeniceSessions : IDisposable
{
    const string VeniceApi = "https://api.venice.ai/api/v1";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys", "venice");
    static readonly string ProxyConfigPath = Path.Combine(Dir, "litellm.yaml");
    static readonly string ClaudeSettingsPath = Path.Combine(Dir, "claude-settings.json");
    static readonly string ProxyLogPath = Path.Combine(Dir, "proxy.log");

    readonly Settings _settings;
    readonly object _gate = new();
    Process? _proxy;

    public VeniceSessions(Settings settings) => _settings = settings;

    string BaseUrl => $"http://127.0.0.1:{_settings.VeniceProxyPort}";

    /// <summary>Opens a terminal tab running Claude Code on Venice in <paramref name="folder"/> (the user profile when null).</summary>
    public void Start(string? folder)
    {
        var veniceKey = Environment.GetEnvironmentVariable("VENICE_API_KEY", EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(veniceKey))
        {
            MessageBox.Show(
                "Set your Venice API key as the user environment variable VENICE_API_KEY, for example in a terminal:\n\n" +
                "setx VENICE_API_KEY your-key\n\nThen pick the session again.",
                "Claude Powertoys: Venice", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        folder ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Log.Write($"New Venice session in {folder}");
        WriteClaudeSettings();
        // The terminal opens right away and waits for the proxy, which takes several seconds to start.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { EnsureProxy(veniceKey); }
            catch (Exception ex) { Log.Write($"Venice proxy: {ex.Message}"); }
        });
        OpenTerminal(folder);
    }

    /// <summary>Stops the proxy this app started; the next session starts a fresh one (for example after a new key).</summary>
    public void StopProxy()
    {
        lock (_gate)
        {
            if (_proxy is { HasExited: false }) _proxy.Kill(entireProcessTree: true);
            _proxy?.Dispose();
            _proxy = null;
        }
        Log.Write("Venice proxy stopped");
    }

    public bool ProxyRunning
    {
        get { lock (_gate) return _proxy is { HasExited: false }; }
    }

    void EnsureProxy(string veniceKey)
    {
        lock (_gate)
        {
            if (_proxy is { HasExited: false } || IsUp()) return;
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ProxyConfigPath, ProxyConfig(VeniceModels()));
            var psi = new ProcessStartInfo(_settings.LiteLlmPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Dir,
            };
            foreach (var arg in new[] { "--config", ProxyConfigPath, "--host", "127.0.0.1", "--port", _settings.VeniceProxyPort.ToString() })
                psi.ArgumentList.Add(arg);
            psi.Environment["VENICE_API_KEY"] = veniceKey;
            psi.Environment["LITELLM_MASTER_KEY"] = ProxyKey();
            // LiteLLM's start-up banner crashes on a non-UTF-8 console encoding.
            psi.Environment["PYTHONUTF8"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var log = new StreamWriter(ProxyLogPath, append: false) { AutoFlush = true };
            var proxy = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proxy.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            proxy.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
            proxy.Exited += (_, _) =>
            {
                proxy.WaitForExit(); // lets the last output lines arrive before the log closes
                Log.Write($"Venice proxy exited ({proxy.ExitCode}); log: {ProxyLogPath}");
                lock (log) log.Dispose();
            };
            proxy.Start();
            proxy.BeginOutputReadLine();
            proxy.BeginErrorReadLine();
            _proxy = proxy;
            Log.Write($"Venice proxy started on {BaseUrl} (pid {proxy.Id})");
        }
    }

    bool IsUp()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync(BaseUrl + "/health/liveliness").Result.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>
    /// Venice's uncensored text models that take tools, which Claude Code needs; the list is public.
    /// Only these are served, so a session can't reach Venice's Claude or other models.
    /// </summary>
    static List<string> VeniceModels()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var doc = JsonDocument.Parse(http.GetStringAsync(VeniceApi + "/models?type=text").Result);
        var models = new List<string>();
        foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var tools = m.TryGetProperty("model_spec", out var spec)
                && spec.TryGetProperty("capabilities", out var caps)
                && caps.TryGetProperty("supportsFunctionCalling", out var f) && f.GetBoolean();
            var id = m.GetProperty("id").GetString()!;
            if (tools && id.Contains("uncensored", StringComparison.OrdinalIgnoreCase)) models.Add(id);
        }
        return models;
    }

    static string ProxyConfig(List<string> models)
    {
        var sb = new StringBuilder("model_list:\n");
        foreach (var id in models)
        {
            sb.Append($"  - model_name: \"{id}\"\n");
            sb.Append("    litellm_params:\n");
            sb.Append($"      model: \"openai/{id}\"\n");
            sb.Append($"      api_base: \"{VeniceApi}\"\n");
            sb.Append("      api_key: os.environ/VENICE_API_KEY\n");
            // Venice adds its own system prompt unless told not to; Claude Code brings its own.
            sb.Append("      extra_body:\n        venice_parameters:\n          include_venice_system_prompt: false\n");
        }
        sb.Append("litellm_settings:\n  drop_params: true\n");
        sb.Append("general_settings:\n  master_key: os.environ/LITELLM_MASTER_KEY\n");
        return sb.ToString();
    }

    /// <summary>The key Claude Code uses for the local proxy, so other programs on the machine can't spend the Venice key through it.</summary>
    string ProxyKey()
    {
        if (string.IsNullOrEmpty(_settings.VeniceProxyKey))
        {
            _settings.VeniceProxyKey = "sk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _settings.Save();
        }
        return _settings.VeniceProxyKey;
    }

    void WriteClaudeSettings()
    {
        var env = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = BaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = ProxyKey(),
            // A user-level Anthropic key would also be sent, in x-api-key, and clash with the proxy key.
            ["ANTHROPIC_API_KEY"] = "",
            // Lists the proxy's Venice models in /model.
            ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1",
            // Subagents and background work (titles, summaries) ask for these tiers; without them they
            // would ask for Claude models, which the proxy doesn't serve.
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = _settings.VeniceModel,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = _settings.VeniceModel,
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = _settings.VeniceModel,
            // Pre-release request fields that the OpenAI translation can't carry.
            ["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"] = "1",
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ClaudeSettingsPath, JsonSerializer.Serialize(new { env }, new JsonSerializerOptions { WriteIndented = true }));
    }

    void OpenTerminal(string folder)
    {
        var script = $$"""
            $Host.UI.RawUI.WindowTitle = 'Claude Code (Venice)'
            Write-Host 'Waiting for the Venice proxy' -NoNewline
            for ($i = 0; $i -lt 90; $i++) {
              try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 '{{BaseUrl}}/health/liveliness' | Out-Null; break }
              catch { Write-Host '.' -NoNewline; Start-Sleep 1 }
            }
            Write-Host ''
            if ($i -eq 90) { Write-Host "The proxy didn't start. Its log: {{ProxyLogPath}}" -ForegroundColor Red }
            # When Powertoys was started from a Claude Code session, the tab inherits that session's
            # variables (its own base URL, sign-in and child-session marker), which beat the settings file.
            Get-ChildItem Env: | Where-Object Name -match '^(CLAUDE|ANTHROPIC)' | Remove-Item
            claude --settings '{{ClaudeSettingsPath}}' --model '{{_settings.VeniceModel}}'
            """;
        var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
        foreach (var arg in new[] { "-d", folder, "--title", "Claude Code (Venice)", "powershell.exe", "-NoLogo", "-NoExit", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);
        Process.Start(psi)?.Dispose();
    }

    public void Dispose()
    {
        if (ProxyRunning) StopProxy();
    }
}
