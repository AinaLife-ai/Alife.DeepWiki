using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Demo.Plugin.DeepWiki;

/// <summary>DeepWiki MCP客户端，支持ask_question/read_wiki_structure/read_wiki_contents</summary>
public class DeepWikiClient
{
    private static readonly HashSet<int> TransientHttpCodes = new() { 429, 500, 502, 503, 504 };

    private readonly HttpClient _http;
    private readonly string _mcpUrl;
    private readonly string _protocolVersion;
    private readonly int _maxRetries;

    public DeepWikiClient(string mcpUrl, string protocolVersion = "2024-11-05", double timeout = 60, int maxRetries = 3)
    {
        _mcpUrl = mcpUrl;
        _protocolVersion = protocolVersion;
        _maxRetries = maxRetries;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
    }

    private static bool IsTransientText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"(?<!\d)(?:50[0-4]|429)(?!\d)|temporarily unavailable|service unavailable|server error|bad gateway|overloaded|timed?\s*out|connection (?:reset|refused|closed)|error processing question",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>调用MCP工具，对瞬时故障自动指数退避重试</summary>
    public async Task<string> CallMcpToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?>
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            }
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        string lastErr = "unknown error";
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, _mcpUrl);
                req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
                req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                req.Content = content;

                using var resp = await _http.SendAsync(req, ct);
                if (TransientHttpCodes.Contains((int)resp.StatusCode))
                {
                    lastErr = $"HTTP {(int)resp.StatusCode}";
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }

                var text = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrEmpty(text))
                {
                    lastErr = "Empty response from DeepWiki MCP";
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }

                var fullAnswer = new List<string>();
                foreach (var line in text.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("data: ")) continue;
                    var jsonStr = trimmed[6..];
                    try
                    {
                        using var doc = JsonDocument.Parse(jsonStr);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("content", out var contentEl))
                        {
                            if (contentEl.ValueKind == JsonValueKind.Array && contentEl.GetArrayLength() > 0)
                            {
                                var first = contentEl[0];
                                if (first.TryGetProperty("text", out var textEl))
                                    fullAnswer.Add(textEl.GetString() ?? "");
                            }
                            else if (contentEl.ValueKind == JsonValueKind.Object && contentEl.TryGetProperty("text", out var textEl2))
                            {
                                fullAnswer.Add(textEl2.GetString() ?? "");
                            }
                        }
                        else if (root.TryGetProperty("error", out var errEl))
                        {
                            var msg = errEl.TryGetProperty("message", out var msgEl)
                                ? $"MCP error: {msgEl.GetString()}"
                                : "MCP error: Unknown";
                            if (IsTransientText(msg))
                            {
                                lastErr = msg;
                                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                                goto retry;
                            }
                            return msg;
                        }
                    }
                    catch (JsonException)
                    {
                        // 跳过非JSON行
                    }
                }

                if (fullAnswer.Count > 0)
                    return string.Join("\n", fullAnswer);
                lastErr = "No content in DeepWiki MCP response";
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (TaskCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                lastErr = e.Message;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        retry:
            ;
        }
        return $"DeepWiki request failed after {_maxRetries + 1} attempts: {lastErr}";
    }

    /// <summary>向仓库提问</summary>
    public Task<string> AskQuestionAsync(string repo, string question, CancellationToken ct = default)
        => CallMcpToolAsync("ask_question", new() { ["repo"] = repo, ["question"] = question }, ct);

    /// <summary>读取Wiki结构</summary>
    public Task<string> ReadWikiStructureAsync(string repo, CancellationToken ct = default)
        => CallMcpToolAsync("read_wiki_structure", new() { ["repo"] = repo }, ct);

    /// <summary>读取Wiki内容</summary>
    public Task<string> ReadWikiContentsAsync(string repo, string path, CancellationToken ct = default)
        => CallMcpToolAsync("read_wiki_contents", new() { ["repo"] = repo, ["path"] = path }, ct);
}