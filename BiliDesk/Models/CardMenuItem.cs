using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 分区面板菜单项

/// <summary>
/// 供页面往「视频卡片」右键菜单里追加的条目定义。
///
/// 为什么不用现成的 MenuItem:
/// 直接把 MenuItem 实例放进集合再让 DataTemplate 去渲染, 会丢失"命令参数应该是当前卡片的数据"
/// 这一层语义(MenuItem 自己的 DataContext 是它自身)。用一个纯数据的中转类型,
/// 模板里就能明确地把卡片的 DataContext 作为 CommandParameter 注入。
/// </summary>
public class CardMenuItem
{
    public string Header { get; set; } = "";
    public System.Windows.Input.ICommand? Command { get; set; }

    /// <summary>
    /// 菜单项左侧的图标字形(Segoe Fluent Icons 码位, 形如 "\uE896")。
    /// 留空则图标列收成 0 宽, 不会留下空档。
    /// </summary>
    public string Glyph { get; set; } = "";
}

// ============================================================================
// 番剧(PGC / 追番)
//
// 与 UGC 视频是**两套完全不同的资源体系**, 不能塞进 VideoItem:
//   视频按 bv 号寻址, 封面 16:9; 番剧按 season_id / ep_id 寻址, 封面是竖版海报 3:4,
//   一次拿回的是"一整季 + 它的分集列表"。所以这里单开一套模型。
//
// 接口面(2026-10-11 实测, 均带登录 Cookie):
//   我的追番   x/space/bangumi/follow/list?vmid=&pn=&ps=&type=   type:1 追番 2 追剧
//   番剧推荐   pgc/season/index/result?season_type=1&type=0&page=&ps=
//   剧集详情   pgc/view/web/season?season_id=
// ============================================================================

