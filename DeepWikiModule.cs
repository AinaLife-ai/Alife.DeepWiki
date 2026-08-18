using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Alife.Demo.Plugin.DeepWiki;

public class DeepWikiConfig
{
    [DisplayName("MCP服务地址")]
    [Description("DeepWiki MCP服务器URL")]
    public string McpUrl { get; set; } = "https://mcp.deepwiki.com/mcp";

    [DisplayName("协议版本")]
    [Description("MCP协议版本")]
    public string ProtocolVersion { get; set; } = "2025-11-25";

    [DisplayName("请求超时(秒)")]
    [Description("HTTP请求超时时间")]
    public double Timeout { get; set; } = 60;

    [DisplayName("最大重试次数")]
    [Description("瞬时故障最大重试次数")]
    public int MaxRetries { get; set; } = 3;

    [DisplayName("缓存TTL(秒)")]
    [Description("回答缓存有效期，0为不缓存")]
    public int CacheTtl { get; set; } = 3600;

    [DisplayName("GitHub Token")]
    [Description("用于搜索仓库的GitHub Token(可选)")]
    public string GithubToken { get; set; } = "";

    [DisplayName("最大搜索结果数")]
    [Description("仓库搜索最大返回条数")]
    public int MaxSearchResults { get; set; } = 5;

    [DisplayName("启用多路搜索")]
    [Description("多路融合搜索(name/description/topics/通用)")]
    public bool UseMultiPathSearch { get; set; } = true;

    [DisplayName("命令词")]
    [Description("触发命令词，逗号分隔")]
    public string CommandWords { get; set; } = "/dw,dw:,/deepwiki";

    [DisplayName("启用命令")]
    [Description("是否启用/dw命令拦截")]
    public bool EnableCommand { get; set; } = true;

    [DisplayName("默认问题")]
    [Description("选定仓库后自动提问的默认问题")]
    public string DefaultQuestion { get; set; } = "请总结这个仓库的核心功能、主要架构以及如何使用或贡献。";

    [DisplayName("按用户隔离上下文")]
    [Description("开启后每个用户独立上下文")]
    public bool IsolateContextByUser { get; set; } = false;

    [DisplayName("默认仓库预设")]
    [Description("格式：会话标识;owner/repo，分号分隔，多组用逗号分隔")]
    public string DefaultRepoPresets { get; set; } = "";

    [DisplayName("重置保留预设")]
    [Description("clear后是否保持预设仓库绑定")]
    public bool ResetKeepsPresetRepo { get; set; } = true;

    [DisplayName("LLM绑定预设仓库")]
    [Description("LLM自然语言调用时默认绑定预设/当前仓库")]
    public bool LlmBindPresetRepo { get; set; } = false;

    [DisplayName("清除命令词")]
    [Description("清除上下文命令词，逗号分隔")]
    public string ClearCommandWords { get; set; } = "clear,重置,清空,reset,清除,清除上下文,重置上下文";

    [DisplayName("状态命令词")]
    [Description("查看状态命令词，逗号分隔")]
    public string StatusCommandWords { get; set; } = "?,？,status,ctx,context,当前,当前仓库";

    [DisplayName("启用LLM工具")]
    [Description("是否注册search_deepwiki/ask_deepwiki等LLM工具")]
    public bool EnableLlmTool { get; set; } = true;

    [DisplayName("自动转发")]
    [Description("超长内容是否用合并转发")]
    public bool EnableAutoForward { get; set; } = true;

    [DisplayName("强制转发")]
    [Description("所有内容都走合并转发")]
    public bool ForceForwardAll { get; set; } = true;

    [DisplayName("长度阈值触发")]
    [Description("启用长度判断触发转发")]
    public bool UseLengthThreshold { get; set; } = false;

    [DisplayName("转发阈值")]
    [Description("超过该长度触发合并转发")]
    public int ForwardThreshold { get; set; } = 400;

    [DisplayName("发送介绍消息")]
    [Description("转发前是否发送介绍消息")]
    public bool SendIntroMessage { get; set; } = false;

    [DisplayName("卡片内附元数据")]
    [Description("转发卡片内附仓库/问题元数据")]
    public bool PrependMetadataInCard { get; set; } = true;

    [DisplayName("私聊转发回退")]
    [Description("私聊合并转发失败时尝试send_forward_msg")]
    public bool EnablePrivateForwardFallback { get; set; } = true;

    [DisplayName("单节点最大字符")]
    [Description("合并转发单节点最大字符数(400-3500)")]
    public int ForwardNodeMaxChars { get; set; } = 800;

    [DisplayName("转发API超时(秒)")]
    [Description("合并转发API超时时间(15-180)")]
    public double ForwardApiTimeout { get; set; } = 60;

    [DisplayName("转发失败降级")]
    [Description("合并转发失败后降级为普通消息")]
    public bool EnableForwardPlainFallback { get; set; } = false;

    [DisplayName("富文本模式")]
    [Description("off=原样 sanitize=去Markdown stylize=全角美化")]
    public string QqRichTextMode { get; set; } = "sanitize";

    [DisplayName("附加操作指南")]
    [Description("答案末尾附加操作指南")]
    public bool AppendOperationGuide { get; set; } = true;
}

[Module("DeepWiki",
    "DeepWiki MCP客户端：查询GitHub仓库Wiki文档、向仓库提问、搜索仓库",
    defaultCategory: "知识检索")]
public class DeepWikiModule(
    XmlFunctionCaller functionCaller,
    ILogger<DeepWikiModule> logger,
    Interactor<DeepWikiModule> interactor) :
    ChatBehaviour,
    IConfigurable<DeepWikiConfig>
{
    public DeepWikiConfig Configuration { get; set; } = null!;

    private DeepWikiClient? _client;
    private readonly Dictionary<string, (string Answer, DateTime Time)> _answerCache = new();
    private readonly Dictionary<string, (List<Dictionary<string, object?>> Repos, DateTime Time)> _searchCache = new();
    private readonly Dictionary<string, string> _lastRepo = new();
    private readonly Dictionary<string, List<Dictionary<string, object?>>> _lastCandidates = new();
    private readonly List<string> _commandWords = new();
    private readonly List<string> _clearWords = new();
    private readonly List<string> _statusWords = new();
    private readonly List<(string Pattern, string Repo)> _presetRepos = new();
    private const int PlainMsgMaxChars = 3500;

    private string PrimaryCmd => _commandWords.Contains("/dw") ? "/dw" : (_commandWords.FirstOrDefault() ?? "/dw");

    private string CacheKey(string repo, string question)
    {
        var raw = $"{repo}|{question}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLower();
    }

    private string? GetCachedAnswer(string key)
    {
        if (Configuration.CacheTtl <= 0) return null;
        if (_answerCache.TryGetValue(key, out var entry) && (DateTime.Now - entry.Time).TotalSeconds < Configuration.CacheTtl)
            return entry.Answer;
        return null;
    }

    private void SetCachedAnswer(string key, string answer)
    {
        if (Configuration.CacheTtl > 0)
            _answerCache[key] = (answer, DateTime.Now);
    }

    private List<Dictionary<string, object?>>? GetCachedSearch(string keyword)
    {
        if (_searchCache.TryGetValue(keyword, out var entry) && (DateTime.Now - entry.Time).TotalSeconds < 60)
            return entry.Repos;
        return null;
    }

    private void SetCachedSearch(string keyword, List<Dictionary<string, object?>> repos)
    {
        _searchCache[keyword] = (repos, DateTime.Now);
    }

    private string Sanitize(string text)
    {
        var mode = string.IsNullOrEmpty(Configuration.QqRichTextMode) ? "sanitize" : Configuration.QqRichTextMode;
        if (mode == "off") return text;
        if (mode == "stylize") return StylizeQqText(text);
        return SanitizeQqText(text);
    }

    private static string SanitizeQqText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var buckets = new List<string>();
        text = Regex.Replace(text, @"```[\s\S]*?```", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"`[^`\n]+`", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"https?://[^\s<>""']+", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });

        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1", RegexOptions.Singleline);
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)([^*\n]+?)(?<!\*)\*(?!\*)", "$1");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^>\s?", "", RegexOptions.Multiline);

        for (int i = 0; i < buckets.Count; i++)
            text = text.Replace($"\x00KEEP{i}\x00", buckets[i]);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static bool HasCjk(string s) => Regex.IsMatch(s, @"[\u4e00-\u9fff\u3400-\u4dbf\uf900-\ufaff]");

    private static string ToFullwidthAlnum(string s)
    {
        var outChars = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch >= 0x41 && ch <= 0x5A) outChars[i] = (char)(0xFF21 + (ch - 0x41));
            else if (ch >= 0x61 && ch <= 0x7A) outChars[i] = (char)(0xFF41 + (ch - 0x61));
            else if (ch >= 0x30 && ch <= 0x39) outChars[i] = (char)(0xFF10 + (ch - 0x30));
            else outChars[i] = ch;
        }
        return new string(outChars);
    }

    private static string StyleBold(string s)
    {
        s = s.Trim();
        if (string.IsNullOrEmpty(s)) return s;
        return HasCjk(s) ? $"【{ToFullwidthAlnum(s)}】" : ToFullwidthAlnum(s);
    }

    private static string StyleItalic(string s)
    {
        s = s.Trim();
        return string.IsNullOrEmpty(s) ? s : $"「{s}」";
    }

    private static string StylizeQqText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var buckets = new List<string>();
        text = Regex.Replace(text, @"```[\s\S]*?```", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"`[^`\n]+`", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"https?://[^\s<>""']+", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });
        text = Regex.Replace(text, @"\b[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+\b", m => { buckets.Add(m.Value); return $"\x00KEEP{buckets.Count - 1}\x00"; });

        text = Regex.Replace(text, @"^#{1,6}\s*(.+?)\s*$", m => StyleBold(m.Groups[1].Value), RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", m => StyleBold(m.Groups[1].Value), RegexOptions.Singleline);
        text = Regex.Replace(text, @"(?<!\*)\*(?!\*)([^*\n]+?)(?<!\*)\*(?!\*)", m => StyleItalic(m.Groups[1].Value));

        for (int i = 0; i < buckets.Count; i++)
            text = text.Replace($"\x00KEEP{i}\x00", buckets[i]);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private async Task<List<Dictionary<string, object?>>> SearchRepositoriesAsync(string keyword, CancellationToken ct = default)
    {
        var results = new List<Dictionary<string, object?>>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var url = $"https://api.github.com/search/repositories?q={Uri.EscapeDataString(keyword)}&per_page={Configuration.MaxSearchResults}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Alife-DeepWiki-Plugin");
            if (!string.IsNullOrEmpty(Configuration.GithubToken))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Configuration.GithubToken}");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return results;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return results;
            foreach (var item in items.EnumerateArray())
            {
                var dict = new Dictionary<string, object?>();
                foreach (var prop in item.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString(),
                        JsonValueKind.Number => prop.Value.GetInt64(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
                results.Add(dict);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub search failed for {Keyword}", keyword);
        }
        return results;
    }

    private async Task<List<Dictionary<string, object?>>> MultiPathSearchRepositoriesAsync(string keyword, CancellationToken ct = default)
    {
        var cached = GetCachedSearch(keyword);
        if (cached != null) return cached;

        var tasks = new List<Task<List<Dictionary<string, object?>>>>();
        if (Configuration.UseMultiPathSearch)
        {
            tasks.Add(SearchRepositoriesAsync($"{keyword} in:name", ct));
            tasks.Add(SearchRepositoriesAsync($"{keyword} in:description,topics", ct));
            tasks.Add(SearchRepositoriesAsync(keyword, ct));
        }
        else
        {
            tasks.Add(SearchRepositoriesAsync(keyword, ct));
        }

        var allResults = await Task.WhenAll(tasks);
        var merged = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var items in allResults)
        {
            foreach (var item in items)
            {
                var fullName = item.GetValueOrDefault("full_name") as string;
                if (string.IsNullOrEmpty(fullName) || merged.ContainsKey(fullName)) continue;

                int score = 0;
                var name = (item.GetValueOrDefault("name") as string ?? "").ToLower();
                var desc = (item.GetValueOrDefault("description") as string ?? "").ToLower();
                var kLower = keyword.ToLower();

                if (name.Contains(kLower)) score += 3;
                if (desc.Contains(kLower)) score += 2;

                long stars = item.GetValueOrDefault("stargazers_count") is long l ? l : 0;
                score += (int)Math.Min(stars / 100, 5);

                var pushed = item.GetValueOrDefault("pushed_at") as string ?? "";
                if (pushed.StartsWith("2026") || pushed.StartsWith("2025")) score += 1;

                merged[fullName] = new Dictionary<string, object?>
                {
                    ["full_name"] = fullName,
                    ["stars"] = stars,
                    ["description"] = item.GetValueOrDefault("description") ?? "",
                    ["score"] = score,
                    ["html_url"] = item.GetValueOrDefault("html_url") ?? ""
                };
            }
        }

        var sorted = merged.Values
            .OrderByDescending(x => (long)(x.GetValueOrDefault("score") ?? 0))
            .ThenByDescending(x => (long)(x.GetValueOrDefault("stars") ?? 0))
            .Take(Configuration.MaxSearchResults)
            .ToList();

        SetCachedSearch(keyword, sorted);
        return sorted;
    }

    private static string FormatRepoCandidates(List<Dictionary<string, object?>> repos, string cmdPrefix = "/dw", string extraHint = "")
    {
        if (repos == null || repos.Count == 0)
            return "未找到相关仓库。可换个关键词再试，或直接发送 owner/repo。";

        var p = (cmdPrefix ?? "/dw").Trim();
        if (string.IsNullOrEmpty(p)) p = "/dw";
        var lines = new List<string> { "搜索到以下候选仓库（按相关性排序）：", "" };
        for (int i = 0; i < repos.Count; i++)
        {
            var r = repos[i];
            var desc = r.GetValueOrDefault("description") as string ?? "";
            if (desc.Length > 100) desc = desc[..100] + "...";
            var full = r.GetValueOrDefault("full_name") as string ?? "";
            long stars = r.GetValueOrDefault("stars") is long sl ? sl : 0;
            lines.Add($"{i + 1}. {full}  ⭐{stars}");
            if (!string.IsNullOrEmpty(desc))
                lines.Add($"   {desc}");
            lines.Add($"   选用：{p} {i + 1}   或   {p} {full}");
            lines.Add("");
        }

        lines.Add("——怎么选——");
        lines.Add($"• {p} 1          → 从候选列表查询第 1 个仓库");
        lines.Add($"• {p} 2          → 查询第 2 个…");
        lines.Add($"• {p} owner/repo → 直接指定仓库");
        lines.Add($"• {p} <问题>     → 选定后继续追问（需已有上下文）");
        lines.Add($"• {p} ?          → 查看当前上下文仓库");
        lines.Add($"• {p} clear      → 清除上下文，重新查询仓库来提问");
        if (!string.IsNullOrEmpty(extraHint) && extraHint.Trim() is { Length: > 0 } hint && hint is not "{}" and not "[]")
            lines.Add(hint);
        return string.Join("\n", lines).TrimEnd();
    }

    private string GetCtxKey(string sessionId, string? userId = null)
    {
        if (Configuration.IsolateContextByUser)
        {
            if (!string.IsNullOrEmpty(userId))
                return $"user:{userId}";
            var lastPart = sessionId.Contains(':') ? sessionId.Split(':')[^1] : sessionId;
            return $"user:{lastPart}";
        }
        return sessionId;
    }

    private string? FindPresetRepo(string ctxKey)
    {
        if (string.IsNullOrEmpty(ctxKey)) return null;
        var parts = ctxKey.Split(':');
        var tail2 = parts.Length >= 2 ? string.Join(":", parts[^2..]) : ctxKey;
        var tail1 = parts[^1];
        foreach (var (pattern, repo) in _presetRepos)
        {
            var p = (pattern ?? "").Trim();
            if (string.IsNullOrEmpty(p)) continue;
            if (p == ctxKey) return repo;
            var pParts = p.Split(':');
            if (pParts.Length >= 3 && parts.Length >= 3 && pParts[^2..].SequenceEqual(parts[^2..]))
                return repo;
            if (p == tail2 || p == tail1) return repo;
            if (ctxKey.StartsWith(p) || p.StartsWith(ctxKey)) return repo;
        }
        return null;
    }

    private string? ResolvePresetRepo(string ctxKey, string? sessionKey = null)
    {
        var repo = FindPresetRepo(ctxKey);
        if (repo != null) return repo;
        if (!string.IsNullOrEmpty(sessionKey) && sessionKey != ctxKey)
            return FindPresetRepo(sessionKey);
        return null;
    }

    private string? ApplyDefaultPresetIfNeeded(string ctxKey, string? sessionKey = null)
    {
        var preset = ResolvePresetRepo(ctxKey, sessionKey);
        if (preset != null && !_lastRepo.ContainsKey(ctxKey))
        {
            _lastRepo[ctxKey] = preset;
            logger.LogInformation("DeepWiki: ctx {Ctx} 应用预设仓库 {Preset}", ctxKey, preset);
        }
        return preset;
    }

    private void ClearCtx(string ctxKey)
    {
        _lastRepo.Remove(ctxKey);
        _lastCandidates.Remove(ctxKey);
    }

    private bool IsClearIntent(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.ToLower().Trim();
        foreach (var word in _clearWords)
        {
            if (string.IsNullOrEmpty(word)) continue;
            var w = word.ToLower().Trim();
            if (string.IsNullOrEmpty(w)) continue;
            if (t == w || t.StartsWith(w + " ") || t.EndsWith(" " + w)) return true;
        }
        return t is "forget" or "忘掉" or "不要记住" or "别记了" or "清除上下文" or "重置上下文";
    }

    private bool IsStatusIntent(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var t = text.Trim().ToLower();
        foreach (var word in _statusWords)
        {
            if (string.IsNullOrEmpty(word)) continue;
            var w = word.ToLower().Trim();
            if (!string.IsNullOrEmpty(w) && t == w) return true;
        }
        return false;
    }

    private bool LooksLikeNewKeyword(string text)
    {
        var t = (text ?? "").Trim();
        if (string.IsNullOrEmpty(t) || t.Any(char.IsWhiteSpace)) return false;
        if (t.IndexOfAny(new[] { '？', '?', '。', '！', '!', '，', ',' }) >= 0) return false;
        if (t.Length > 48) return false;
        if (Regex.IsMatch(t, @"(怎么|如何|什么|为什么|哪些|是否|能否|怎样|哪里|多少)")) return false;
        if (t.Contains('/')) return false;
        return true;
    }

    private bool IsRepoQueryFailure(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return true;
        var low = answer.ToLower();
        string[] markers =
        {
            "repository not found", "requested repos", "visit https://deepwiki.com to index",
            "to index it", "not indexed", "查询失败", "mcp error", "deepwiki request failed",
            "empty response", "error processing question", "server error",
            "temporarily unavailable", "service unavailable", "bad gateway", "overloaded"
        };
        if (markers.Any(m => low.Contains(m))) return true;
        if (Regex.IsMatch(low, @"(?<!\d)(?:50[0-4]|429)(?!\d)")) return true;
        if (low.Contains("error") && (low.Contains("failed") || low.Contains("not found"))) return true;
        return false;
    }

    private (string? Cmd, string RawQuery) MatchCommand(string text)
    {
        if (string.IsNullOrEmpty(text)) return (null, "");
        foreach (var cmd in _commandWords)
        {
            if (string.IsNullOrEmpty(cmd)) continue;
            if (text == cmd) return (cmd, "");
            if (text.StartsWith(cmd + " ") || text.StartsWith(cmd + ":"))
                return (cmd, text[cmd.Length..].TrimStart(' ', ':'));
        }
        return (null, "");
    }

    private string GetOperationGuide()
    {
        var p = PrimaryCmd;
        var triggers = _commandWords.Where(w => !string.IsNullOrEmpty(w)).Take(3).ToList();
        var clears = _clearWords.Where(w => !string.IsNullOrEmpty(w)).Take(3).ToList();
        var statuses = _statusWords.Where(w => !string.IsNullOrEmpty(w)).Take(3).ToList();

        var lines = new List<string> { "——操作指南——", $"【{p} 操作指南】" };
        if (triggers.Count > 0)
            lines.Add($"• {string.Join(" / ", triggers)} <关键词或问题>   （触发查询）");
        if (statuses.Count > 0)
            lines.Add($"• {string.Join(" / ", statuses.Select(s => $"{p} {s}"))}   （查看当前上下文仓库）");
        if (clears.Count > 0)
            lines.Add($"• {string.Join(" / ", clears.Select(c => $"{p} {c}"))}   （清除上下文，重新查询仓库来提问）");
        lines.Add($"• {p} 1 / {p} 2   （从候选列表中查询对应序号的仓库）");
        lines.Add($"• {p} <问题>   （在已有上下文仓库上继续追问）");
        lines.Add($"• 切换项目：{p} clear 后，再 {p} <新关键词>；或在无上下文时直接 {p} <新关键词> 搜索");
        lines.Add($"• 也可直接 {p} owner/repo 指定仓库");
        return string.Join("\n", lines);
    }

    private string BuildAnswerWithMetadata(string repo, string question, string answer)
    {
        var content = answer ?? "";
        if (Configuration.PrependMetadataInCard)
        {
            var prefix = $"【DeepWiki 查询】\n仓库：{repo}\n问题：{question}\n\n";
            content = prefix + content;
        }
        if (Configuration.AppendOperationGuide)
        {
            var guide = GetOperationGuide();
            if (!string.IsNullOrEmpty(guide))
                content = content.TrimEnd() + "\n\n" + guide.Trim();
        }
        return content;
    }

    private static List<string> SplitTextChunks(string text, int maxChars)
    {
        text ??= "";
        if (text.Length <= maxChars) return new List<string> { text };

        var chunks = new List<string>();
        var blocks = Regex.Split(text, @"\n{2,}");
        var buf = "";
        foreach (var block in blocks)
        {
            var candidate = string.IsNullOrEmpty(buf) ? block : buf + "\n\n" + block;
            if (candidate.Length <= maxChars)
            {
                buf = candidate;
                continue;
            }
            if (!string.IsNullOrEmpty(buf))
            {
                chunks.Add(buf);
                buf = "";
            }
            if (block.Length <= maxChars)
            {
                buf = block;
                continue;
            }
            var lines = block.Split('\n');
            var lineBuf = "";
            foreach (var line in lines)
            {
                var piece = string.IsNullOrEmpty(lineBuf) ? line : lineBuf + "\n" + line;
                if (piece.Length <= maxChars)
                {
                    lineBuf = piece;
                    continue;
                }
                if (!string.IsNullOrEmpty(lineBuf))
                {
                    chunks.Add(lineBuf);
                    lineBuf = "";
                }
                if (line.Length <= maxChars)
                {
                    lineBuf = line;
                }
                else
                {
                    for (int i = 0; i < line.Length; i += maxChars)
                        chunks.Add(line.Substring(i, Math.Min(maxChars, line.Length - i)));
                    lineBuf = "";
                }
            }
            buf = string.IsNullOrEmpty(lineBuf) ? "" : lineBuf;
        }
        if (!string.IsNullOrEmpty(buf)) chunks.Add(buf);
        return chunks.Count > 0 ? chunks : new List<string> { text[..Math.Min(maxChars, text.Length)] };
    }

    private string PrepareForwardText(string text)
    {
        var mode = string.IsNullOrEmpty(Configuration.QqRichTextMode) ? "sanitize" : Configuration.QqRichTextMode;
        var s = mode switch
        {
            "stylize" => StylizeQqText(text ?? ""),
            "off" => text ?? "",
            _ => SanitizeQqText(text ?? "")
        };

        s = Regex.Replace(s, @"<cite\b[^>]*/?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</cite>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"View this search on DeepWiki:.*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"Wiki pages you might want to explore:[\s\S]*?(?=\n\n|\Z)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[\x00-\x08\x0b\x0c\x0e-\x1f]", "");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s;
    }

    private async Task<string> ExecuteDirectQueryAsync(string repo, string question, string ctxKey)
    {
        if (_client == null) return "DeepWiki 客户端未初始化";

        var key = CacheKey(repo, question);
        var cached = GetCachedAnswer(key);
        if (cached != null) return cached;

        var answer = await _client.AskQuestionAsync(repo, question);
        if (IsRepoQueryFailure(answer))
        {
            _lastRepo.Remove(ctxKey);
            var hasCands = _lastCandidates.ContainsKey(ctxKey);
            var tip = $"查询失败或仓库未被 DeepWiki 索引：\n{answer}\n\n" +
                      $"✅ 已取消当前仓库绑定（避免后续关键词被当成追问）。\n请重新选择：\n" +
                      $"• {PrimaryCmd} <关键词>     重新搜索\n" +
                      $"• {PrimaryCmd} owner/repo   直接指定其他仓库";
            if (hasCands)
                tip += $"\n• {PrimaryCmd} 1 / {PrimaryCmd} 2     从刚才的候选列表重选";
            return tip;
        }

        SetCachedAnswer(key, answer);
        return answer;
    }

    private async Task<string> HandleKeywordSearchAsync(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        var cached = GetCachedSearch(keyword);
        if (cached == null)
        {
            cached = await MultiPathSearchRepositoriesAsync(keyword);
            SetCachedSearch(keyword, cached);
        }
        return FormatRepoCandidates(cached, PrimaryCmd);
    }

    private async Task<string> ProcessDwCommandAsync(string sessionId, string? userId, string rawQuery)
    {
        var ctxKey = GetCtxKey(sessionId, userId);
        var preset = ApplyDefaultPresetIfNeeded(ctxKey, sessionId);
        var p = PrimaryCmd;
        rawQuery = (rawQuery ?? "").Trim();

        if (string.IsNullOrEmpty(rawQuery))
        {
            var last = _lastRepo.GetValueOrDefault(ctxKey);
            if (last != null)
            {
                var isPreset = preset != null && last == preset;
                var tag = isPreset ? "（预设仓库）" : "";
                var lines = new List<string>
                {
                    $"当前上下文仓库：{last}{tag}",
                    "",
                    $"• {p} <你的问题>   → 直接向该仓库提问",
                    $"• {p} ?            → 查看当前上下文",
                    $"• {p} clear        → 清除上下文" + (isPreset && Configuration.ResetKeepsPresetRepo ? "（预设仓库会保持绑定）" : ""),
                    $"• {p} owner/repo   → 改查其他仓库"
                };
                if (!isPreset)
                    lines.Add($"• {p} <关键词>     → 重新搜索仓库");
                return string.Join("\n", lines);
            }
            return $"用法示例：\n{p} xxynet/KiraAI\n{p} xxynet/KiraAI 这个项目怎么安装？\n{p} KiraAI\n{p} 1          （从候选列表中查询对应序号的仓库）\n{p} ?          （查看当前上下文仓库）\n{p} clear      （清除上下文，重新查询仓库来提问）";
        }

        if (IsClearIntent(rawQuery))
        {
            ClearCtx(ctxKey);
            if (Configuration.ResetKeepsPresetRepo && preset != null)
            {
                _lastRepo[ctxKey] = preset;
                return $"✅ 已清除当前上下文（对话记忆与候选列表）。\n本会话/用户预设了仓库，重置后仍保持绑定：{preset}\n可直接继续提问：{p} <你的问题>\n查看当前上下文：{p} ?";
            }
            return $"✅ 已清除当前上下文。\n可重新发送：{p} <关键词> 或 {p} owner/repo";
        }

        if (IsStatusIntent(rawQuery))
        {
            var last = _lastRepo.GetValueOrDefault(ctxKey);
            var cands = _lastCandidates.GetValueOrDefault(ctxKey) ?? new();
            if (last != null)
            {
                var msg = $"当前上下文仓库：{last}\n继续追问：{p} <你的问题>\n清除上下文：{p} clear\n换项目：{p} clear 后，再 {p} <新关键词>";
                if (cands.Count > 0)
                    msg += $"\n最近候选数：{cands.Count}（可用 {p} 1 选择）";
                return msg;
            }
            var noCtx = "当前还没有上下文仓库。\n" + $"请先：{p} <关键词> 或 {p} owner/repo";
            if (cands.Count > 0)
                noCtx += $"\n最近候选数：{cands.Count}（可用 {p} 1 选择）";
            return noCtx;
        }

        if (rawQuery.All(char.IsDigit))
        {
            var candidates = _lastCandidates.GetValueOrDefault(ctxKey) ?? new();
            var idx = int.Parse(rawQuery) - 1;
            if (idx >= 0 && idx < candidates.Count)
            {
                var repo = candidates[idx].GetValueOrDefault("full_name") as string ?? "";
                _lastRepo[ctxKey] = repo;
                return await ExecuteDirectQueryAsync(repo, Configuration.DefaultQuestion, ctxKey);
            }
            return $"序号无效或没有候选列表。\n请先发送：{p} <关键词>\n再发送：{p} 1";
        }

        var parts = rawQuery.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0];
        var question = parts.Length > 1 ? parts[1] : "";

        if (first.Contains('/') && Regex.IsMatch(first, @"^[\w.-]+/[\w.-]+$"))
        {
            _lastRepo[ctxKey] = first;
            return await ExecuteDirectQueryAsync(first, string.IsNullOrEmpty(question) ? Configuration.DefaultQuestion : question, ctxKey);
        }

        var lastRepo = _lastRepo.GetValueOrDefault(ctxKey);
        if (lastRepo != null)
        {
            var pinned = preset != null && Configuration.ResetKeepsPresetRepo && lastRepo == preset;
            if (string.IsNullOrEmpty(question) && !pinned && LooksLikeNewKeyword(rawQuery))
            {
                _lastRepo.Remove(ctxKey);
                return await HandleKeywordSearchAsync(rawQuery);
            }
            return await ExecuteDirectQueryAsync(lastRepo, string.IsNullOrEmpty(rawQuery) ? Configuration.DefaultQuestion : rawQuery, ctxKey);
        }

        return await HandleKeywordSearchAsync(string.IsNullOrEmpty(question) ? first : rawQuery);
    }

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        _commandWords.Clear();
        _commandWords.AddRange(Configuration.CommandWords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _commandWords.Sort((a, b) => b.Length.CompareTo(a.Length));

        _clearWords.Clear();
        _clearWords.AddRange(Configuration.ClearCommandWords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        _statusWords.Clear();
        _statusWords.AddRange(Configuration.StatusCommandWords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        _presetRepos.Clear();
        foreach (var line in Configuration.DefaultRepoPresets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(';');
            if (idx > 0)
            {
                var k = line[..idx].Trim();
                var v = line[(idx + 1)..].Trim();
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    _presetRepos.Add((k, v));
            }
        }

        _client = new DeepWikiClient(Configuration.McpUrl, Configuration.ProtocolVersion, Configuration.Timeout, Configuration.MaxRetries);
        logger.LogInformation("DeepWiki plugin initialized: enable_command={EnableCommand}, presets={PresetCount}, reset_keeps_preset_repo={ResetKeeps}, llm_bind_preset_repo={LlmBind}, isolate_by_user={Isolate}, force_forward_all={ForceForward}",
            Configuration.EnableCommand, _presetRepos.Count, Configuration.ResetKeepsPresetRepo, Configuration.LlmBindPresetRepo, Configuration.IsolateContextByUser, Configuration.ForceForwardAll);
        await base.OnStartAsync(ct);
    }

    protected override async Task OnDestroyAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = null;
        _answerCache.Clear();
        _searchCache.Clear();
        await base.OnDestroyAsync(ct);
    }

    [XmlFunction("dw_query", "DeepWiki查询：/dw <关键词或owner/repo> 搜索仓库或提问")]
    public async Task<string> DwQuery([XmlParameter("sessionId", "会话ID")] string sessionId, [XmlParameter("userId", "用户ID(可选)")] string? userId, [XmlParameter("query", "查询内容")] string query)
    {
        if (!Configuration.EnableCommand)
            return "命令模式已禁用";
        return await ProcessDwCommandAsync(sessionId, userId, query);
    }

    [XmlFunction("dw_search", "DeepWiki搜索仓库：返回候选列表")]
    public async Task<string> DwSearch([XmlParameter("keyword", "搜索关键词")] string keyword)
    {
        if (!Configuration.EnableLlmTool)
            return "LLM 工具调用已被禁用，请使用 /dw 命令直接查询。";
        return await HandleKeywordSearchAsync(keyword);
    }

    [XmlFunction("dw_ask", "DeepWiki提问：向指定仓库提问")]
    public async Task<string> DwAsk([XmlParameter("repo", "仓库标识 owner/repo")] string repo, [XmlParameter("question", "问题")] string question)
    {
        if (!Configuration.EnableLlmTool)
            return "LLM 工具调用已被禁用，请使用 /dw 命令直接查询。";
        if (_client == null)
            return "DeepWiki 客户端未初始化";
        if (!Regex.IsMatch(repo ?? "", @"^[\w.-]+/[\w.-]+$"))
            return "repo 参数必须是 owner/repo 格式，例如 xxynet/KiraAI";

        var key = CacheKey(repo, question);
        var cached = GetCachedAnswer(key);
        if (cached != null) return cached;

        var answer = await _client.AskQuestionAsync(repo, question);
        if (IsRepoQueryFailure(answer))
            return $"未找到相关信息或仓库未被 DeepWiki 索引：{answer}\n请换一个已索引的 owner/repo，或先 search_deepwiki 重新搜索。";

        SetCachedAnswer(key, answer);
        return answer;
    }

    [XmlFunction("dw_structure", "DeepWiki获取仓库文档结构")]
    public async Task<string> DwStructure([XmlParameter("repo", "仓库标识 owner/repo")] string repo)
    {
        if (!Configuration.EnableLlmTool)
            return "LLM 工具调用已被禁用，请使用 /dw 命令直接查询。";
        if (_client == null)
            return "DeepWiki 客户端未初始化";
        return await _client.ReadWikiStructureAsync(repo);
    }

    [XmlFunction("dw_contents", "DeepWiki读取仓库文档内容")]
    public async Task<string> DwContents([XmlParameter("repo", "仓库标识 owner/repo")] string repo, [XmlParameter("topic", "主题(可选)")] string? topic = "")
    {
        if (!Configuration.EnableLlmTool)
            return "LLM 工具调用已被禁用，请使用 /dw 命令直接查询。";
        if (_client == null)
            return "DeepWiki 客户端未初始化";

        var key = CacheKey(repo, $"content:{topic}");
        var cached = GetCachedAnswer(key);
        if (cached != null) return cached;

        var answer = await _client.ReadWikiContentsAsync(repo, topic ?? "");
        if (string.IsNullOrEmpty(answer) || answer.ToLower().Contains("error") || answer.ToLower().Contains("failed"))
            return $"读取内容失败：{answer}";

        SetCachedAnswer(key, answer);
        return answer;
    }
}

public class DeepWikiClient : IDisposable
{
    private readonly string _mcpUrl;
    private readonly string _protocolVersion;
    private readonly double _timeout;
    private readonly int _maxRetries;
    private readonly HttpClient _http;
    private static readonly HashSet<int> TransientHttpCodes = new() { 429, 500, 502, 503, 504 };
    private static readonly Regex TransientTextRe = new(
        @"(?<!\d)(?:50[0-4]|429)(?!\d)|temporarily unavailable|service unavailable|server error|bad gateway|overloaded|timed?\s*out|connection (?:reset|refused|closed)|error processing question",
        RegexOptions.IgnoreCase);

    public DeepWikiClient(string mcpUrl, string protocolVersion, double timeout, int maxRetries = 3)
    {
        _mcpUrl = mcpUrl;
        _protocolVersion = protocolVersion;
        _timeout = timeout;
        _maxRetries = maxRetries;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) };
    }

    private static bool IsTransientText(string text) => TransientTextRe.IsMatch(text ?? "");

    private async Task<string> CallMcpToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new Dictionary<string, object?>
            {
                ["name"] = toolName,
                ["arguments"] = arguments
            }
        };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        content.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
        content.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var lastErr = "unknown error";
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            try
            {
                using var resp = await _http.PostAsync(_mcpUrl, content, ct);
                if (TransientHttpCodes.Contains((int)resp.StatusCode))
                {
                    lastErr = $"HTTP {(int)resp.StatusCode}";
                    if (attempt >= _maxRetries) break;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrEmpty(text))
                {
                    lastErr = "Empty response from DeepWiki MCP";
                    if (attempt >= _maxRetries) break;
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
                        if (root.TryGetProperty("result", out var result) && result.TryGetProperty("content", out var contentElem))
                        {
                            if (contentElem.ValueKind == JsonValueKind.Array && contentElem.GetArrayLength() > 0)
                            {
                                var first = contentElem[0];
                                if (first.TryGetProperty("text", out var textElem))
                                    fullAnswer.Add(textElem.GetString() ?? "");
                            }
                            else if (contentElem.ValueKind == JsonValueKind.Object && contentElem.TryGetProperty("text", out var textElem2))
                            {
                                fullAnswer.Add(textElem2.GetString() ?? "");
                            }
                        }
                        else if (root.TryGetProperty("error", out var errorElem))
                        {
                            var msg = $"MCP error: {errorElem.GetProperty("message").GetString() ?? "Unknown"}";
                            if (IsTransientText(msg))
                            {
                                lastErr = msg;
                                if (attempt >= _maxRetries) return msg;
                                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                                goto retry;
                            }
                            return msg;
                        }
                    }
                    catch (JsonException) { }
                }

                if (fullAnswer.Count > 0)
                {
                    var answer = string.Join("\n", fullAnswer);
                    if (IsTransientText(answer))
                    {
                        lastErr = answer;
                        if (attempt >= _maxRetries) break;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                        continue;
                    }
                    return answer;
                }

                using var doc2 = JsonDocument.Parse(text);
                var root2 = doc2.RootElement;
                if (root2.TryGetProperty("result", out var result2) && result2.TryGetProperty("content", out var content2))
                {
                    if (content2.ValueKind == JsonValueKind.Array && content2.GetArrayLength() > 0)
                    {
                        var answer2 = content2[0].GetProperty("text").GetString() ?? "";
                        if (IsTransientText(answer2))
                        {
                            lastErr = answer2;
                            if (attempt >= _maxRetries) break;
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                            continue;
                        }
                        return answer2;
                    }
                    if (content2.ValueKind == JsonValueKind.Object)
                    {
                        var answer3 = content2.GetProperty("text").GetString() ?? "";
                        if (IsTransientText(answer3))
                        {
                            lastErr = answer3;
                            if (attempt >= _maxRetries) break;
                            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                            continue;
                        }
                        return answer3;
                    }
                }
                return $"Unexpected response: {text[..Math.Min(200, text.Length)]}";
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                lastErr = "timeout";
                if (attempt >= _maxRetries) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (HttpRequestException e)
            {
                lastErr = e.Message;
                if (attempt >= _maxRetries) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception e)
            {
                return $"DeepWiki request failed: {e.Message}";
            }
        retry: ;
        }
        return $"DeepWiki request failed: {lastErr}";
    }

    public Task<string> AskQuestionAsync(string repo, string question, CancellationToken ct = default)
        => CallMcpToolAsync("ask_question", new Dictionary<string, object?> { ["repoName"] = repo, ["question"] = question }, ct);

    public Task<string> ReadWikiStructureAsync(string repo, CancellationToken ct = default)
        => CallMcpToolAsync("read_wiki_structure", new Dictionary<string, object?> { ["repoName"] = repo }, ct);

    public Task<string> ReadWikiContentsAsync(string repo, string topic = "", CancellationToken ct = default)
    {
        var args = new Dictionary<string, object?> { ["repoName"] = repo };
        if (!string.IsNullOrEmpty(topic)) args["topic"] = topic;
        return CallMcpToolAsync("read_wiki_contents", args, ct);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
