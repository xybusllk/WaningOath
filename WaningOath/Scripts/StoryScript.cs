using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using FileAccess = Godot.FileAccess;

namespace WaningOath;

/// <summary>剧情脚本里的一拍。</summary>
public sealed class StoryBeat
{
    /// <summary>`say` 台词 · `act` 演出/旁白 · `note` 系统提示/档案 · `battle` 战斗节点。</summary>
    public string Kind { get; set; } = "act";

    /// <summary>说话人（`say` 才有）。</summary>
    public string? Speaker { get; set; }

    /// <summary>正文。`note` 的表格拍没有这个字段，改用 <see cref="Table"/>。</summary>
    public string? Text { get; set; }

    /// <summary>立绘 key：`protagonist` / `xingyao` / `jingxing` / `alice`，或直接是文件名。</summary>
    public string? Portrait { get; set; }

    /// <summary>阵营行；不写就没有（`00 §4` 的口径）。</summary>
    public string? Faction { get; set; }

    /// <summary>这一拍换场景（缺省沿用本节场景）。</summary>
    public string? Scene { get; set; }

    /// <summary>表格（档案 / 表单）：若干行，每行若干格。</summary>
    public List<string[]>? Table { get; set; }
}

/// <summary>一个小节（第一章的一节 / 幕间的一幕）。</summary>
public sealed class StorySection
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>本节默认场景 slug（= `Assets/Backgrounds/<slug>.png`）。</summary>
    public string? Scene { get; set; }

    public List<StoryBeat> Beats { get; set; } = [];
}

/// <summary>
/// 一份剧情脚本（`WaningOath/Assets/Story/&lt;id&gt;.story.json`）。
/// </summary>
/// <remarks>
/// 由 `tools/story-convert.mjs` 从 `剧情文本/*.md` 生成，**不要手改 JSON** ——
/// 改文本、重跑转换器即可。标记规范见 `剧情文本/第一章 旧账未清.md` 的「标记」一节。
/// <para>
/// 序章仍是客户端内置的 96 拍数组（它有自己的场景编号 S0~S9 与教学战跳转）；
/// 第一章与幕间走这条数据通路。
/// </para>
/// </remarks>
public sealed class StoryScript
{
    private const string Dir = "res://Assets/Story/";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public int BeatCount { get; set; }
    public List<StorySection> Sections { get; set; } = [];

    private static readonly Dictionary<string, StoryScript?> Cache = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>读一份脚本；读不到返回 <c>null</c>（调用方负责提示，不崩）。</summary>
    public static StoryScript? Load(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Cache.TryGetValue(id, out StoryScript? cached)) return cached;

        StoryScript? script = null;
        string path = Dir + id + ".story.json";
        try
        {
            if (FileAccess.FileExists(path))
            {
                string json = FileAccess.GetFileAsString(path);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    script = JsonSerializer.Deserialize<StoryScript>(json, Options);
                }
            }
        }
        catch (System.Exception e)
        {
            GD.Print($"[剧情] 读取 {id} 失败：{e.Message}");
        }

        if (script is not null && string.IsNullOrEmpty(script.Id)) script.Id = id;
        Cache[id] = script;
        return script;
    }
}
