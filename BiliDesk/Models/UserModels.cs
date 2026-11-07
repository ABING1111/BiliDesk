using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 用户: 登录态(nav)与 UP 主空间信息

/// <summary>登录用户信息</summary>
public class UserState
{
    public bool IsLogin { get; set; }
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }

    public double Coins { get; set; }

    /// <summary>当前经验值(nav 的 level_info.current_exp; 「我的」页显示"经验 x/y")</summary>
    public long ExperienceCurrent { get; set; }

    /// <summary>升级所需经验(nav 的 level_info.next_exp; Lv6 时为 0)</summary>
    public long ExperienceNext { get; set; }
}

/// <summary>UP 主空间信息(多接口聚合的结果)</summary>
public class SpaceInfo
{
    public string Name { get; set; } = "";
    public string Face { get; set; } = "";
    public string Sign { get; set; } = "";
    public long Mid { get; set; }
    public int Level { get; set; }
    public long Follower { get; set; }   // 粉丝数
    public long Following { get; set; }  // 关注数
    public long VideoCount { get; set; } // 投稿数
}

/// <summary>
/// 搜索 / 历史的"看哪一类"标签(2026-10-03 新增)。
///
/// 视频与直播间走的是**完全不同的接口和字段**, 但界面上是同一套卡片墙 ——
/// 所以用同一个枚举把"当前这一类"在页面/VM/接口之间传下去, 而不是各传一个 bool。
/// ★ <see cref="User"/> 只有搜索页会用到; 历史页永远只可能是 Video / Live。
/// </summary>
