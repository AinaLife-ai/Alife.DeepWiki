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
    public string ProtocolVersion { get; set; } = "2024-11-05";

    [DisplayName("请求超时(秒)")]
    [Description("HTTP请求超时时间")]
    public double Timeout { get; set; } = 60;

    [DisplayName("最大重试次数")]
    [Description("瞬时故障最大重试次数")]
    public int MaxRetries { get; set; } = 3;

    [DisplayName("缓存TTL(秒)")]
    [Description("回答缓存有效期，0为不缓存")]
    public int CacheTtl { get; set; } = 300;

    [DisplayName("GitHub Token")]
    [Description("用于搜索仓库的GitHub Token(可选)")]
    public string GithubToken { get; set; } = "";

    [DisplayName("最大搜索结果数")]
    [Description("仓库搜索最大返回条数")]
    public int MaxSearchResults { get; set; } = 5;

    [DisplayName("命令词")]
    [Description("触发命令词，逗号分隔")]
    public string CommandWords { get; set; } = "/dw";

    [DisplayName("启用命令")]
    [Description("是否启用/dw命令拦截")]
    public bool EnableCommand { get; set; } = true;

    [DisplayName("默认仓库预设")]
    [Description("默认绑定的仓库，逗号分隔")]
    public string DefaultRepoPresets { get; set; } = "";

    [DisplayName("重置保留预设")]
    [Description("clear后是否保持预设仓库绑定")]
    public bool ResetKeepsPresetRepo { get; set; } = true;

    [DisplayName("富文本模式")]
    [Description("off=原样 sanitize=去Markdown stylize=全角美化")]
    public string QqRichTextMode { get; set; } = "sanitize";

    [DisplayName("自动转发")]
    [Description("超长内容是否用合并转发")]
    public bool EnableAutoForward { get; set; } = false;

    [DisplayName("转发阈值")]
    [Description("超过该长度触发合并转发")]
    public int ForwardThreshold { get; set; } = 3500;

    [DisplayName("强制转发")]
    [Description("所有内容都走合并转发")]
    public bool ForceForwardAll { get; set; } = false;
}

[Module("DeepWiki",
    "DeepWiki MCP客户端：查询GitHub仓库Wiki文档、向仓库提问、搜索仓库",
    defaultCategory: "Alife 官方/知识检索")]
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
    private readonly HashSet<string> _commandWords = new();
    private readonly List<string> _presetRepos = new();

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
        // 保护代码块/行内代码/URL
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
        var cached = GetCachedSearch(keyword);
        if (cached != null) return cached;

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
                        JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => null
                    };
                }
                results.Add(dict);
            }
            SetCachedSearch(keyword, results);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "搜索GitHub仓库失败");
        }
        return results;
    }

    private string FormatRepoList(List<Dictionary<string, object?>> repos)
    {
        if (repos.Count == 0) return "没有找到匹配的仓库";
        var lines = repos.Select((r, i) =>
        {
            var fullName = r.GetValueOrDefault("full_name")?.ToString() ?? "unknown";
            var desc = r.GetValueOrDefault("description")?.ToString() ?? "";
            var stars = Convert.ToInt64(r.GetValueOrDefault("stargazers_count") ?? 0);
            return $"{i + 1}. {fullName} ⭐{stars} {(string.IsNullOrEmpty(desc) ? "" : $"- {desc}")}";
        });
        return string.Join("\n", lines);
    }

    private async Task<string> AskRepoAsync(string repo, string question, CancellationToken ct = default)
    {
        var key = CacheKey(repo, question);
        var cached = GetCachedAnswer(key);
        if (cached != null) return cached;

        _client ??= new DeepWikiClient(Configuration.McpUrl, Configuration.ProtocolVersion, Configuration.Timeout, Configuration.MaxRetries);
        var answer = await _client.AskQuestionAsync(repo, question, ct);
        SetCachedAnswer(key, answer);
        return answer;
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("向指定GitHub仓库的DeepWiki提问，获取基于Wiki文档的答案")]
    public async Task DwAsk(
        [Description("仓库名，格式 owner/repo")] string repo,
        [Description("问题")] string question)
    {
        try
        {
            var answer = await AskRepoAsync(repo, question);
            interactor.Poke(Sanitize(answer));
        }
        catch (Exception e)
        {
            interactor.Poke($"提问失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("搜索GitHub仓库")]
    public async Task DwSearch(
        [Description("搜索关键词")] string keyword)
    {
        try
        {
            var repos = await SearchRepositoriesAsync(keyword);
            interactor.Poke(FormatRepoList(repos));
        }
        catch (Exception e)
        {
            interactor.Poke($"搜索失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取GitHub仓库的DeepWiki结构")]
    public async Task DwStructure(
        [Description("仓库名，格式 owner/repo")] string repo)
    {
        try
        {
            _client ??= new DeepWikiClient(Configuration.McpUrl, Configuration.ProtocolVersion, Configuration.Timeout, Configuration.MaxRetries);
            var result = await _client.ReadWikiStructureAsync(repo);
            interactor.Poke(Sanitize(result));
        }
        catch (Exception e)
        {
            interactor.Poke($"读取失败：{e.Message}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("读取GitHub仓库DeepWiki指定路径的内容")]
    public async Task DwContents(
        [Description("仓库名，格式 owner/repo")] string repo,
        [Description("Wiki路径")] string path)
    {
        try
        {
            _client ??= new DeepWikiClient(Configuration.McpUrl, Configuration.ProtocolVersion, Configuration.Timeout, Configuration.MaxRetries);
            var result = await _client.ReadWikiContentsAsync(repo, path);
            interactor.Poke(Sanitize(result));
        }
        catch (Exception e)
        {
            interactor.Poke($"读取失败：{e.Message}");
        }
    }
}