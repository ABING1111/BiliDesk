using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;

namespace BiliDesk.Models;

// ★ 2026-11-07 由 Models.cs 拆分而来(纯移动、零逻辑改动)。本文件: 关注列表条目(含红点状态的变更通知)

/// <summary>关注列表项(关注的 UP 主)</summary>
public class FollowUser : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public long Mid { get; set; }
    public string Name { get; set; } = "";

    /// <summary>
    /// UP 头像地址。
    ///
    /// ★ 必须带通知(2026-11-06 用户报"更新的 UP 头像不显示/显示别人的头像"):
    ///   未读占位行的头像来自 portal 的 up_list, 是**之后**才补上的 ——
    ///   属性不带通知的话, 那一格不会被重新绑定, 于是要么没头像, 要么留着
    ///   虚拟化回收时上一个 UP 的头像。
    /// </summary>
    private string _face = "";
    public string Face
    {
        get => _face;
        set
        {
            if (_face == value) return;
            _face = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(Face)));
        }
    }

    public string Sign { get; set; } = "";
    public int VerifyType { get; set; } = -1;
    public string VerifyDesc { get; set; } = "";

    /// <summary>
    /// 这一行是"有未读、但还没被分页加载出来"的**占位行**(2026-11-06)。
    ///
    /// ★ 用途: 关注 2790 人时, 一个刚更新的 UP 可能还在第 2 页之后。左栏只做一个面板,
    ///   所以这类 UP 作为占位行插进 Users 最前面, 等分页真的取到这个人时用真实数据补全
    ///   (见 FollowViewModel.MergeLoadedUser), 避免同一行出现两次。
    /// ★ 占位行不参与"关注总数"统计, 否则「已加载 N / 2790」里的 N 会虚高。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPlaceholder { get; set; }

    private bool _isSelected;

    /// <summary>
    /// 动态页左栏的选中高亮(UI 用, 不参与任何接口)。
    /// 必须带通知: 选中是**从 ViewModel 侧**改的, 新旧两项的高亮都要跟着变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private int _newCount;

    /// <summary>
    /// 「未读动态」条数 —— 左栏未读提示的判定依据(0 = 没有新动态)。
    /// ★ 界面上有**两处**用它: 头像左侧的**红点**(只表达"有没有")与头像右下角的
    ///   **数字角标**(直接显示条数)。2026-11-05 把数字角标改了回来 —— 之前数字只放在
    ///   红点的悬浮提示里, "有 12 条"这件事鼠标不悬停就看不见。
    ///
    /// ★ 由**本地**判断, 不新增接口(见 DynamicSeenStore): 拿动态流里"该 UP 最新的一条"
    ///   与上次记录的比对, 不一样就是有新动态。
    /// ★ 必须带通知: 未读提示是刷新后由 ViewModel 侧统一刷的(见 FollowViewModel.UpdateUnreadBadges),
    ///   界面要跟着变。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int NewCount
    {
        get => _newCount;
        set
        {
            if (_newCount == value) return;
            _newCount = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(NewCount)));
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(HasNew)));
        }
    }

    public bool HasNew => NewCount > 0;

    private long _latestPubdate;

    /// <summary>
    /// 该 UP 最近一条动态的发布时间(Unix 秒)。**0 = 还没拿到**。
    ///
    /// ★ 用途: 动态页左栏按"最近有更新的 UP 排前面"排序(见 FollowViewModel.ReorderUsersByActivity)。
    ///   关注列表接口本身**不给**这个时间(只给 mid/名字/头像/签名), 所以只能从动态流里
    ///   反推 —— 动态流按时间倒序, 某个 UP 最先出现的那条就是 TA 最新的。
    /// ★ 精度: 动态接口的扁平路径只给 "2小时前" 这种文本, 时间戳是**反推**出来的
    ///   (见 VideoItem.PubDateApprox), 误差在"分钟~小时"量级 —— 对"谁更新得勤"这个
    ///   排序需求来说完全够用(同一天内的先后差别不影响观感)。
    /// ★ 0 的含义是"**还没观察到**这个 UP 有更新"(动态流里还没出现过它)。
    ///   此时它会被排在有更新时间的那些**之后**, 但它**彼此之间**保持原相对顺序 ——
    ///   详见 <see cref="BiliDesk.ViewModels.FollowViewModel"/> 里的排序说明。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long LatestPubdate
    {
        get => _latestPubdate;
        set
        {
            if (_latestPubdate == value) return;
            _latestPubdate = value;
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(LatestPubdate)));
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(HasActivity)));
            PropertyChanged?.Invoke(this,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(ActivityLabel)));
        }
    }

    /// <summary>这个 UP 有没有被观察到"最近有更新"(= 动态流里出现过且拿到了时间)</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasActivity => LatestPubdate > 0;

    /// <summary>
    /// 左栏行尾显示的"最近更新"文字(没有则空串)。
    ///
    /// ★ 用接口原文那套措辞("2小时前" / "昨天 18:45"), 与动态卡片上的发布时间同一套语言 ——
    ///   同一页里出现两种写法("3小时前" vs "10-14 18:45")会很别扭。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ActivityLabel
        => LatestPubdate > 0 ? VideoItem.FormatPubRelative(LatestPubdate) : "";
}

/// <summary>
/// 评论里的一个表情。
