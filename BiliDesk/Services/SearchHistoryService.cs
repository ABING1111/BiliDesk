using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Services;

/// <summary>
/// 搜索历史(search_history.json, 最多留 20 条)。
///
/// 只存关键词本身 —— 搜索结果是一搜就变的, 存下来只会让用户点到过期内容。
/// 重复搜索同一关键词不新增, 而是把它挪到最前(不区分大小写去重)。
/// </summary>
public class SearchHistoryService
{
    public static SearchHistoryService Instance { get; } = new();

    private const int MaxItems = 20;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "search_history.json");

    // 最多 20 条, 缩进没有意义, 反倒让每次搜索都多写一倍的字节
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>最近搜过的关键词, 新的在前</summary>
    public ObservableCollection<string> Items { get; } = new();

    /// <summary>内容变化(增删/清空)。界面据此刷新"有没有历史"这类派生状态。</summary>
    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath), JsonOpts);
            if (list == null) return;
            foreach (var k in list.Where(x => !string.IsNullOrWhiteSpace(x)).Take(MaxItems))
                Items.Add(k);
        }
        catch
        {
            // 文件被改坏了就当没有历史, 不影响搜索本身
            Items.Clear();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items.ToArray(), JsonOpts));
        }
        catch
        {
            // 忽略: 历史写不进去不该影响搜索
        }
    }

    /// <summary>记一条搜索。空关键词忽略; 已存在的挪到最前而不是重复添加。</summary>
    public void Add(string? keyword)
    {
        var kw = (keyword ?? "").Trim();
        if (kw.Length == 0) return;

        var old = Items.FirstOrDefault(x => string.Equals(x, kw, StringComparison.OrdinalIgnoreCase));
        if (old != null) Items.Remove(old);

        Items.Insert(0, kw);
        while (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        Save();
        Changed?.Invoke();
    }

    public void Remove(string? keyword)
    {
        if (string.IsNullOrEmpty(keyword)) return;
        var hit = Items.FirstOrDefault(x => string.Equals(x, keyword, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return;
        Items.Remove(hit);
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Save();
        Changed?.Invoke();
    }
}
