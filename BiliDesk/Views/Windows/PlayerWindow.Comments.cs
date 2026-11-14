using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;
using BiliDesk.Views.Controls;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace BiliDesk.Views;

public partial class PlayerWindow : FluentWindow
{
    // ------------------------------------------------------------ Tab

    private void TabIntro_Checked(object sender, RoutedEventArgs e)
    {
        IntroPanel.Visibility = Visibility.Visible;
        // 切走评论 tab: 根评论列表整块隐藏, 它的 RichTextBox/FlowDocument 没必要常驻。
        // 放在 Visibility 变化之前, 这样卡片还有最后一次布局高度可用(释放要把高度冻结住)。
        ReleaseAllCommentContent();
        CommentsPanel.Visibility = Visibility.Collapsed;
        CloseCommentDetail();
        // 切走评论 tab: FAB 与展开的输入面板一起收掉(它们只属于评论区)
        UpdateCommentComposer();
        // 「回到顶部」只属于评论列表, 切走时一并熄灭
        UpdateCommentBackToTop(null);
    }

    private async void TabComments_Checked(object sender, RoutedEventArgs e)
    {
        IntroPanel.Visibility = Visibility.Collapsed;
        CommentsPanel.Visibility = Visibility.Visible;
        // 切到评论页时顺手校准一次输入框状态(FAB 显隐跟着 tab 走):
        // 用户可能是先登录/换号再回来发的
        UpdateCommentComposer();
        // 从简介切过来时外层 ScrollViewer 可能停在下滑位置: 按当前偏移重新判定一次,
        // 避免"已经滚过阈值、回到顶部却不亮"。
        UpdateCommentBackToTop(InfoScroll);
        await LoadCommentsAsync();
        // 上一轮离开评论 tab / 进入详情时可能把内容释放了(见 ReleaseAllCommentContent):
        // 切回来立刻按当前视口补回来, 否则用户看到的是整页空占位。
        RestoreVisibleCommentCardsNow(forceLayout: true);
        QueueCommentContentWindow();
        ScheduleCommentAutoLoad();
    }

    // ------------------------------------------------------------ 回到顶部(2026-11-09)

    /// <summary>
    /// 评论列表向下滚过这个距离才把「回到顶部」亮出来(与首页同值)。
    /// ★ 阈值别太小: 轻轻滚一下按钮就闪出来、回滚一点又消失, 看着像故障。
    /// </summary>
    private const double CommentBackToTopThreshold = 400;

    /// <summary>「回到顶部」当前是否亮着(避免每次滚动都重复起动画)</summary>
    private bool _commentBackToTopShown;

    /// <summary>
    /// 按当前滚动位置点亮/熄灭评论区右下角的「回到顶部」。
    ///
    /// ★ 只动 <c>Opacity</c> 与命中测试, **不用 Visibility**:
    ///   它和「返回评论列表」共用 FAB 上方 54px 那一格, 用 Collapsed 会让另一颗按钮
    ///   在显隐切换时上下跳(用户正要点就点空)。常驻占位、淡入淡出最稳。
    /// ★ 判据里的 <see cref="IsCommentDetailOpen"/>: 详情页有自己的一整页内容与
    ///   「返回评论列表」, 两颗按钮不能同时压在同一格上。
    /// </summary>
    private void UpdateCommentBackToTop(ScrollViewer? sv)
    {
        if (_closing || BtnCommentBackToTop == null) return;

        var onCommentsTab = CommentsPanel != null &&
                            CommentsPanel.Visibility == Visibility.Visible;
        // 直播/本地文件没有评论区, 即使外层 ScrollViewer 停在下滑位置也不该亮
        var usable = onCommentsTab && !IsCommentDetailOpen && !_isLive && !_isLocalPlayback;
        var show = usable && sv is { VerticalOffset: > CommentBackToTopThreshold };
        if (show == _commentBackToTopShown) return;
        _commentBackToTopShown = show;

        BtnCommentBackToTop.IsHitTestVisible = show;
        BtnCommentBackToTop.BeginAnimation(OpacityProperty,
            new DoubleAnimation(BtnCommentBackToTop.Opacity, show ? 1 : 0,
                TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>点击「回到顶部」: 把右侧信息栏(评论列表所在的那一层)滚回顶部。</summary>
    private void OnCommentBackToTopClick(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        InfoScroll?.ScrollToTop();
    }

    private bool _commentsLoaded;

    /// <summary>
    /// 评论区排序: 3 = 热门, 2 = 时间(与 B 站接口的 mode 同值)。
    /// ★ 初值 3 与接口默认一致 —— 用户第一次进评论区看到的顺序不能因为这次改动而变。
    /// </summary>
    private int _commentSort = 3;

    /// <summary>
    /// **本次实际展示**的评论排序(3 热门 / 2 时间)。平时等于 <see cref="_commentSort"/>,
    /// 只有"热门接口返回空、本次按时间回退"时会临时变成 2。
    /// ★ 单独记一份是为了让排序按钮的点击判断跟着**眼前看到的列表**走:
    ///   热门回退成时间后 _commentSort 仍是 3, 若直接拿它判断, 用户点「热门」会被
    ///   "已经是热门"短路掉, 按钮看起来是死的。
    /// </summary>
    private int _effectiveCommentSort = 3;

    /// <summary>当前视频的根评论总数(来自接口 cursor.all_count, 不是本页条数)</summary>
    private long _commentTotal;

    // ------------------------------------------------------------ 评论翻页(2026-11-08, issue #55)
    //
    // ★ 为什么不再直接把接口返回的 List 挂到 ItemsSource:
    //   `CommentList` 在**外层 InfoScroll 的 StackPanel** 里, 没有虚拟化; 翻页时若整份
    //   ItemsSource 重新赋值, WPF 会重建全部容器, 滚动位置会跳回顶部 —— 用户刚滑到底
    //   点了"加载更多"结果被弹回第一页, 体验比"没有加载更多"更差。
    //   改用 ObservableCollection 后只对新增条目生成容器, 已有内容与滚动位置都保留。
    //
    // ★★ 2026-11-09 用户反馈"往下滑会一次性刷新很多条评论":
    //   以前每拉回一页(30 条)就整页塞进列表, 30 棵评论视觉树在同一拍实例化/排版,
    //   表现就是"突然涌出一大批"。现在拉回的页先放进缓冲, 按
    //   CommentWindow.RevealBatch(6 条)分批往列表里放; 只有用户继续往下滑、
    //   底部重新进入触发带时才放下一批, 不做无意义的离屏实例化。

    /// <summary>已展示的根评论集合(翻页时只追加, 不重建)</summary>
    private readonly ObservableCollection<CommentItem> _commentItems = new();

    /// <summary>已拉回、还没放进列表的评论(分批展示用, 见上方说明)</summary>
    private readonly Queue<CommentItem> _commentPending = new();

    /// <summary>已经见过/展示过的 rpid, 用来跨页去重(置顶评论也常同时出现在 replies 里)</summary>
    private readonly HashSet<long> _commentSeenRpids = new();

    /// <summary>已排过一次"继续分批展示"的调度, 避免滚动时排几十个回调</summary>
    private bool _commentRevealScheduled;

    /// <summary>
    /// 评论列表的"代次": 每次首屏/刷新请求开始时 +1。
    /// 旧请求(尤其是翻页请求)回来时如果代次已经变了, 说明列表早被换掉, 必须丢弃 ——
    /// 否则切换排序/换视频后, 上一份列表的第二页会混进新列表。
    /// </summary>
    private int _commentListVersion;

    // ------------------------------------------------------------ 限流冷却(2026-11-09)
    //
    // ★ 用户报"评论区提示请求过于频繁"。B 站 -412 是 **IP 级限流**(见 ApiClient.Core),
    //   恢复窗口实测 45~180 秒。旧实现撞上之后只会在 API 层退避 1.5s/3s 再试两次,
    //   然后返回错误; 用户切 tab / 切排序 / 换视频又会立刻再打一轮, 反而把限流拖得更久。
    //   这里在评论区加一层"冷却闸":
    //     · 命中限流 → 30 秒内不再发起评论请求, 提示里显示倒计时;
    //     · 冷却结束自动重试一次; 再次被限流则翻倍(30 → 60 → 120 → 180 秒);
    //     · 成功加载后清零, 下次限流重新从 30 秒开始。
    //   ★ 冷却状态**不随换视频清空**: 限流是 IP 级的, 换一个视频也照样会被拦。

    private DateTime _commentCooldownUntilUtc;
    private int _commentCooldownSeconds;
    private DispatcherTimer? _commentCooldownTimer;

    /// <summary>下一页游标(data.cursor.next); 0 = 没有下一页</summary>
    private long _commentNext;

    /// <summary>接口是否还有下一页(不包含"本地缓冲里还没展示的部分")</summary>
    private bool _commentHasMore;

    /// <summary>是否正在补下一页(滚轮会连续触发 ScrollChanged, 必须防重入)</summary>
    private bool _commentLoadingMore;

    /// <summary>上一次补页是否失败(失败后底部显示"点击重试", 而不是假装到底了)</summary>
    private bool _commentLoadMoreFailed;

    // ------------------------------------------------------------ 评论内容窗口(2026-11-08, 2026-11-09 重做)
    //
    // ★ 无限加载 + 无虚拟化(容器不销毁)会让每条评论的 RichTextBox/FlowDocument
    //   永久常驻内存, 滚几十页后能涨到 100~200MB。这里按**滚动位置**释放离屏卡片的
    //   正文文档与图片, 回到视野附近再重建 —— 效果相当于手动虚拟化内容层。
    //   容器本身保留(只冻结高度), 所以滚动位置与列表版式都不变。
    //
    // ★★ 2026-11-09 三处修正(区间纯计算见 Helpers/CommentWindow.cs):
    //   ① 同步恢复视口内的卡片 —— 用户按住滚动条往上拖时, 不能等 120ms 定时器;
    //   ② 预取/释放区间从 ±4 屏收到 ±0.75 / ±2 屏, 不再一次重建几十条;
    //   ③ 向上滚动也要执行窗口扫描 —— 旧实现 `VerticalChange < 0` 直接 return,
    //      拖回上面时被释放的评论根本没人补, 才会"先空白随后刷新"。

    private readonly Dictionary<ContentPresenter, CommentCardParts> _commentCardParts = new();
    private DispatcherTimer? _commentContentWindowTimer;

    /// <summary>正在做窗口扫描(UpdateLayout 可能触发 ScrollChanged, 必须防重入)</summary>
    private bool _commentWindowUpdating;

    /// <summary>一张评论卡片的可释放部件(正文/图片)</summary>
    private sealed class CommentCardParts
    {
        public CommentText[] Texts = Array.Empty<CommentText>();
        public Border[] Covers = Array.Empty<Border>();
        public bool Released;

        /// <summary>离屏时被换下来的原模板(换模板 = 销毁整棵卡片视觉树, 比只清文档更省内存)</summary>
        public DataTemplate? OriginalTemplate;
        public bool TemplateSwapped;
    }

    private DataTemplate? _commentPlaceholderTemplate;

    /// <summary>离屏评论用的空模板(见 PlayerWindow.xaml 的 CommentPlaceholderTemplate)</summary>
    private DataTemplate? CommentPlaceholderTemplate
        => _commentPlaceholderTemplate ??=
            TryFindResource("CommentPlaceholderTemplate") as DataTemplate;

    private async Task LoadCommentsAsync(bool force = false)
    {
        if (_closing) return;
        // ★ 记住发起时的 aid: 网络回来时用户可能已经换片了。没有这道的话,
        //   上一条视频的评论会灌进新视频(列表/条数/排序入口全是旧的)。
        var aidAtStart = _aid;
        WriteLibVLCDebug($"\n[Comments] start aid={aidAtStart} loaded={_commentsLoaded} force={force}\n");
        // force: 刚发表完评论要重新拉一次(rpid/楼层/审核状态本地编不出来)。
        // 但"看过就不再拉"这个短路必须保留 —— 切 Tab 会反复调到这里, 不短路就是白刷接口。
        // ★ 切换排序必须走 force=true: 它要的就是"同一份数据换个顺序重拉"。
        // ★★ 2026-11-05 修"评论详情页点 热门/时间 被退回评论区":
        //    这里原来无条件 CloseCommentDetail() —— 用户明明是在详情页里点的排序,
        //    结果刚点亮按钮就被弹回顶层列表, 看起来像"点一下退出了详情"。
        //    force 的两个来源要区别对待:
        //      · 切换排序: 详情页必须留在原地(详情页的 DataContext 是 _detailThreadRoot
        //        这个**对象**本身, 重拉只替换 CommentList.ItemsSource, 不碰它);
        //      · 发表评论后刷新: 在详情页里发表走的是"回复"分支(ReloadThreadRepliesAsync),
        //        走不到这里; 真走到这里时详情页本来就没开, 所以保留现场也不会留旧内容。
        //    换视频/切 tab/主动返回这些需要关详情的地方, 由各自调用方显式 CloseCommentDetail()。
        if (_commentsLoaded && !force) { CommentHint.Text = "已加载"; return; }
        if (aidAtStart <= 0)
        {
            CommentHint.Text = _isLocalPlayback ? "本地文件没有评论" : "未获取到视频 aid";
            UpdateCommentSortUi(visible: false);
            return;
        }
        // 冷却中: 不再发请求(见字段区的说明)。提示里带倒计时, 到点自动重试。
        if (CommentCooldownActive(out _))
        {
            ShowCommentCooldown();
            return;
        }
        // 这一代列表开始: 在途的翻页请求从此作废(见 _commentListVersion)
        var versionAtStart = ++_commentListVersion;
        // 首屏/刷新期间禁止翻页: 否则用户滚动时可能拿旧的游标去请求第二页
        _commentLoadingMore = true;
        _commentPending.Clear();
        _commentLoadMoreFailed = false;
        CommentLoadingRing.Visibility = Visibility.Visible;
        CommentHint.Text = force ? "正在刷新评论…" : "加载评论中…";
        // 重拉期间先把两个词收起来: 否则"正在刷新评论…"右边还挂着可点的排序词, 会出现
        // "点一下又刷新一次"的连点窗口期。
        UpdateCommentSortUi(visible: false);
        // 本次排序先按用户偏好; 若后面回退成时间, 在回调里改 _effectiveCommentSort
        _effectiveCommentSort = _commentSort;
        try
        {
            var (cmtOk, cmtErr, list, total, nextCursor, isEnd) =
                await Svc.Api.GetCommentsAsync(aidAtStart, 30, _commentSort);
            // 实际拿到的列表按哪个顺序排(热门的"空列表回退"会把它改成 2)。
            // ★ 不回写 _commentSort: 那是用户跨视频记住的偏好, 不能因为一个视频热门为空
            //   就永久改成时间; 排序入口只按本次实际顺序点亮。
            var displaySort = _commentSort;
            // ★★ 2026-11-04 修"评论区只显示置顶一条"(BV1Nxxr6oES2 实测):
            //   该视频 mode=3(热门) 的 data.replies 是**空数组**, 只剩 data.top 里的置顶;
            //   同一时刻 mode=2(时间) 能正常返回 14 条。接口不给热门列表, 不是我们解析错。
            //   这里在"热门 + 只剩置顶"时按时间重拉一次, 排序入口切到「时间」——
            //   否则热门一直亮着、列表却只有置顶, 用户会以为评论区坏了。
            //
            // ★★ 2026-11-09 修 BV1irxX6LE9R "评论显示不全":
            //   该视频 mode=3 返回的是 **置顶 + 1 条回复**(共 2 条), 而 all_count=3668。
            //   旧判据 `list.Count <= 1` 只覆盖"只剩置顶", 碰到"置顶 + 少量回复"就不回退,
            //   用户在热门排序下只能看到两条评论。
            //   ⇒ 只要热门页的**非置顶根评论**少得明显不正常(不到半页 / 少于总条数),
            //     就按时间重拉一页做对比; 时间页更多才替换。这样既修好"2 条"的极端情况,
            //     也不会在热门正常返回一整页时误切排序。
            var hotRootCount = list.Count(c => !c.IsPinned);
            if (cmtOk && _commentSort == 3 && CommentWindow.ShouldFallbackHot(hotRootCount, total))
            {
                var (fbOk, _, fbList, fbTotal, fbNext, fbEnd) =
                    await Svc.Api.GetCommentsAsync(aidAtStart, 30, 2);
                if (fbOk && fbList.Count > list.Count && _commentSort == 3)
                {
                    WriteLibVLCDebug(
                        $"[Comments] hot fallback aid={aidAtStart} hotRoots={hotRootCount} " +
                        $"total={total} -> timeRoots={fbList.Count}\n");
                    list = fbList;
                    if (fbTotal > 0) total = fbTotal;
                    // 回退成时间后, 翻页游标也必须跟着这一份列表走
                    nextCursor = fbNext;
                    isEnd = fbEnd;
                    cmtOk = true;
                    cmtErr = null;
                    displaySort = 2;
                }
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (_closing || _aid != aidAtStart || _commentListVersion != versionAtStart) return;
                // 首屏/刷新: 整份集合换掉(翻页走 LoadMoreCommentsAsync, 只追加)
                // 容器会被重新生成/复用: 先把离屏时换掉的模板与冻结高度还原,
                // 否则新评论会沿用占位模板, 整页变空白。
                ResetCommentCardParts();
                _commentItems.Clear();
                foreach (var c in list)
                {
                    // 置顶评论经常同时出现在 replies 里, 按 rpid 去重, 免得同一层楼挂两遍
                    if (c.Rpid > 0 && !_commentSeenRpids.Add(c.Rpid)) continue;
                    _commentItems.Add(c);
                }
                if (!ReferenceEquals(CommentList.ItemsSource, _commentItems))
                    CommentList.ItemsSource = _commentItems;
                _commentNext = nextCursor;
                _commentHasMore = !isEnd && nextCursor > 0;
                _commentLoadMoreFailed = false;
                _commentLoadingMore = false;
                // ★ 失败时不要标记"已加载": 否则下次切回评论 tab 会被
                //   `_commentsLoaded && !force` 短路, 网络抖动一次就再也刷不出来。
                _commentsLoaded = cmtOk;
                _commentTotal = total;
                _effectiveCommentSort = displaySort;
                // 热门没数据回退成时间时, 详情页里的楼中楼也要跟着换回"时间"顺序:
                // 否则按钮亮的是「时间」, 眼前回复还是「热门」序。
                if (IsCommentDetailOpen) SortDetailReplies();
                CommentLoadingRing.Visibility = Visibility.Collapsed;
                // ★ 命中 IP 限流: 不要再把裸错误文案丢给用户, 进入冷却并显示倒计时。
                //   冷却结束后由 OnCommentCooldownTick 自动重试(见字段区说明)。
                if (!cmtOk && IsCommentRateLimitError(cmtErr))
                {
                    EnterCommentRateLimitCooldown();
                }
                else
                {
                    ClearCommentCooldown();
                    // ★ 原来"接口失败"和"这视频真的一条评论都没有"都显示"暂无评论" ——
                    //   网络失败被伪装成了正常状态。现在失败时说清原因。
                    // ★ 条数用接口给的**总数**(_commentTotal), 不再拿 list.Count —— 一页只拉 30 条,
                    //   拿它当总数的话几千条评论的视频会显示成"共 30 条评论"。总数缺失(0)时才回落到本页条数。
                    CommentHint.Text = !cmtOk ? (cmtErr ?? "评论加载失败")
                        : list.Count == 0 ? "暂无评论"
                        : $"共 {VideoItem.FormatCount(total > 0 ? total : list.Count)} 条评论";
                }
                // 只有真的加载成功且有评论, 排序入口才出现
                // ★ 详情页开着时不受"列表为空/失败"影响: 用户还在详情页里,
                //   此时把排序入口收起来等于把"切回另一个排序"的退路也一起藏了。
                // ★ 传 displaySort: 热门回退成时间时, 让「时间」亮起来与列表实际顺序一致
                UpdateCommentSortUi(visible: cmtOk && (list.Count > 0 || IsCommentDetailOpen), sortOverride: displaySort);
                UpdateCommentMoreUi();
                // 内容不足一屏(窗口很高 / 评论很短)时 ScrollChanged 不会触发,
                // 主动检查一次, 否则用户看到的就是"只有这一页, 也滑不动"。
                ScheduleCommentAutoLoad();
                QueueCommentContentWindow();
            });
        }
        catch
        {
            if (_closing || _aid != aidAtStart || _commentListVersion != versionAtStart) return;
            CommentLoadingRing.Visibility = Visibility.Collapsed;
            CommentHint.Text = "评论加载失败";
            _commentLoadingMore = false;
            // 详情页里切换排序失败时保留两个排序词: 否则用户被卡在详情页,
            // 连"再点一次重试/切回原排序"的入口都没有了。
            UpdateCommentSortUi(visible: IsCommentDetailOpen);
            UpdateCommentMoreUi();
        }
    }

    // ------------------------------------------------------------ 限流冷却实现

    /// <summary>接口错误文案是不是 B 站 -412(IP 限流)。</summary>
    private static bool IsCommentRateLimitError(string? err)
        => CommentWindow.IsRateLimitMessage(err);

    /// <summary>冷却是否还没结束; remainingSeconds 为剩余秒数(向上取整)。</summary>
    private bool CommentCooldownActive(out int remainingSeconds)
    {
        remainingSeconds = 0;
        var left = _commentCooldownUntilUtc - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) return false;
        remainingSeconds = (int)Math.Ceiling(left.TotalSeconds);
        return true;
    }

    /// <summary>
    /// 命中限流: 进入/延长冷却。冷却时长按 30 → 60 → 120 → 180 秒翻倍 ——
    /// B 站说恢复要 45~180 秒, 固定 30 秒会连续撞墙, 每次都多打一轮请求。
    /// </summary>
    private void EnterCommentRateLimitCooldown()
    {
        if (_closing) return;
        _commentCooldownSeconds = CommentWindow.NextRateLimitCooldown(_commentCooldownSeconds);
        _commentCooldownUntilUtc = DateTime.UtcNow.AddSeconds(_commentCooldownSeconds);
        ShowCommentCooldown();
    }

    /// <summary>成功加载(或失败原因不是限流)时清零冷却与退避档位。</summary>
    private void ClearCommentCooldown()
    {
        _commentCooldownSeconds = 0;
        _commentCooldownUntilUtc = default;
        _commentCooldownTimer?.Stop();
    }

    /// <summary>把"请求过于频繁 + 倒计时"显示出来, 并确保倒计时定时器在跑。</summary>
    private void ShowCommentCooldown()
    {
        if (_closing) return;
        if (!CommentCooldownActive(out var seconds)) return;

        CommentLoadingRing.Visibility = Visibility.Collapsed;
        // 列表还没加载成功时把排序入口收起(避免用户点来点去又触发请求);
        // 已经有旧列表时不动它, 只是提示暂时刷不了。
        if (!_commentsLoaded) UpdateCommentSortUi(visible: false);
        CommentHint.Text = $"请求过于频繁，{seconds} 秒后自动重试";
        StartCommentCooldownTimer();
    }

    private void StartCommentCooldownTimer()
    {
        if (_closing) return;
        if (_commentCooldownTimer == null)
        {
            _commentCooldownTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _commentCooldownTimer.Tick += (_, _) => OnCommentCooldownTick();
        }
        if (!_commentCooldownTimer.IsEnabled) _commentCooldownTimer.Start();
    }

    private void OnCommentCooldownTick()
    {
        if (_closing)
        {
            _commentCooldownTimer?.Stop();
            return;
        }

        if (CommentCooldownActive(out var seconds))
        {
            // 用户切到别的 tab 时不刷这句提示, 回来自会按剩余时间继续显示
            if (CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible)
                CommentHint.Text = $"请求过于频繁，{seconds} 秒后自动重试";
            return;
        }

        _commentCooldownTimer?.Stop();
        // 冷却结束: 还停在评论 tab、且没有加载成功过, 自动重试一次。
        // 再次被限流会走 EnterCommentRateLimitCooldown, 时长翻倍。
        if (!_commentsLoaded && !_commentLoadingMore && _aid > 0 &&
            CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible &&
            !IsCommentDetailOpen)
        {
            _ = LoadCommentsAsync(force: true);
        }
    }

    /// <summary>
    /// 同步排序入口的可见性与选中态。
    ///
    /// ★ 选中态只在这里写 Tag(与 ContentActionTabStyle 同一套约定: 字符串 "on"/"off")。
    ///   分散着改两个按钮的 Tag 必然出现"两边都亮"或"两边都不亮"。
    /// ★ sortOverride: 热门接口返回空、本次实际按时间拉了一次时传 2, 让高亮跟着真实顺序走;
    ///   不传就用用户记住的 _commentSort。
    /// </summary>
    private void UpdateCommentSortUi(bool visible, int? sortOverride = null)
    {
        var sort = sortOverride ?? _commentSort;
        var vis = visible ? Visibility.Visible : Visibility.Collapsed;
        if (BtnCommentSortHot != null)
        {
            BtnCommentSortHot.Visibility = vis;
            BtnCommentSortHot.Tag = sort == 3 ? "on" : "off";
        }
        if (BtnCommentSortTime != null)
        {
            BtnCommentSortTime.Visibility = vis;
            BtnCommentSortTime.Tag = sort == 2 ? "on" : "off";
        }
    }

    private void OnCommentSortHotClick(object sender, RoutedEventArgs e) => SetCommentSort(3);

    private void OnCommentSortTimeClick(object sender, RoutedEventArgs e) => SetCommentSort(2);

    // ------------------------------------------------------------ 评论翻页(2026-11-08, issue #55)

    /// <summary>
    /// 补下一页根评论(滚动到列表底部附近时自动调, 失败后点底部提示也会调)。
    ///
    /// ★ 排序必须用 <see cref="_effectiveCommentSort"/> 而不是 _commentSort:
    ///   热门返回空、本次按时间回退时, 用户眼前看到的是时间序, 后续翻页也要按时间继续 ——
    ///   否则第二页会突然按热门序混进来, 列表顺序直接乱掉。
    /// ★ 不能只看 is_end: 热门模式在深层页码偶尔会重复返回同一页, 若每页都去重后新增 0 条,
    ///   继续翻只会无限打接口 —— added == 0 时直接收口。
    /// ★ 拉回来的整页先进 _commentPending, 由 <see cref="RevealPendingComments"/> 分批展示:
    ///   一次创建 30 棵评论视觉树就是用户说的"一次性刷新很多条评论"。
    /// </summary>
    private async Task LoadMoreCommentsAsync()
    {
        if (_closing || _commentLoadingMore || !_commentsLoaded || !_commentHasMore) return;
        // 上一页还没展示完: 不急着打接口, 先把本地缓冲放出去
        if (_commentPending.Count > 0) { RevealPendingComments(); return; }
        if (CommentsPanel == null || CommentsPanel.Visibility != Visibility.Visible || IsCommentDetailOpen) return;
        if (_aid <= 0 || _commentNext <= 0) return;
        // 限流冷却中: 连"点击重试"也不发请求, 等倒计时走完
        if (CommentCooldownActive(out _)) { ShowCommentCooldown(); return; }

        var aidAtStart = _aid;
        var versionAtStart = _commentListVersion;
        var next = _commentNext;
        _commentLoadingMore = true;
        _commentLoadMoreFailed = false;
        UpdateCommentMoreUi();
        try
        {
            var (ok, err, more, total, nextCursor, isEnd) =
                await Svc.Api.GetCommentsAsync(aidAtStart, 30, _effectiveCommentSort, next);
            if (_closing || _aid != aidAtStart || _commentListVersion != versionAtStart) return;
            if (!ok)
            {
                // 失败不当作"到底了": 网络抖一下就把「已经到底了」亮出来, 用户会以为评论只有这些。
                // 限流则进入冷却, 避免用户连点重试把窗口拖得更长。
                if (IsCommentRateLimitError(err)) EnterCommentRateLimitCooldown();
                _commentLoadMoreFailed = true;
                return;
            }
            ClearCommentCooldown();

            var added = 0;
            foreach (var c in more)
            {
                // 跨页去重: 热门模式偶尔会重复返回上一页的条目
                if (c.Rpid > 0 && !_commentSeenRpids.Add(c.Rpid)) continue;
                _commentPending.Enqueue(c);
                added++;
            }

            if (total > 0)
            {
                _commentTotal = total;
                CommentHint.Text = $"共 {VideoItem.FormatCount(total)} 条评论";
            }
            _commentNext = nextCursor;
            if (isEnd || nextCursor <= 0 || added == 0) _commentHasMore = false;
        }
        catch (Exception ex)
        {
            if (_closing || _aid != aidAtStart || _commentListVersion != versionAtStart) return;
            App.ReportError(ex);
            _commentLoadMoreFailed = true;
        }
        finally
        {
            // ★ 换片/刷新列表后这些状态已经属于"新的一份列表": 旧请求的收尾不能去清新请求的闸门
            if (!_closing && _aid == aidAtStart && _commentListVersion == versionAtStart)
            {
                _commentLoadingMore = false;
                // 只放一小批, 剩下的等用户继续往下滑 —— 这就是"刷新机制"的改动
                RevealPendingComments();
                UpdateCommentMoreUi();
                // 新追加的一批在视口下方, 已滚过的旧评论此刻在视口上方 —— 排一轮离屏释放
                QueueCommentContentWindow();
            }
        }
    }

    /// <summary>
    /// 从缓冲里往列表放一小批评论。
    ///
    /// ★ 为什么要分批: 每放一条, WPF 就要实例化一整棵评论卡片(RichTextBox/头像/图片)。
    ///   30 条一起放会在同一帧里做 30 次排版, 用户看到的就是"哗一下涌出很多条"。
    /// ★ 放完一批如果缓冲还有, 且用户仍在底部附近, 下一帧再放; 不在底部就停手,
    ///   避免用户已经翻回上面看旧评论时, 下面还在白创建视觉树。
    /// </summary>
    private int RevealPendingComments(int max = CommentWindow.RevealBatch)
    {
        if (_closing || _commentPending.Count == 0) return 0;
        // 已经切到别的 tab / 进了详情页: 先不往隐藏的列表里创建视觉树
        if (CommentsPanel == null || CommentsPanel.Visibility != Visibility.Visible || IsCommentDetailOpen)
            return 0;

        var added = 0;
        while (added < max && _commentPending.Count > 0)
        {
            _commentItems.Add(_commentPending.Dequeue());
            added++;
        }
        if (added == 0) return 0;

        UpdateCommentMoreUi();
        QueueCommentContentWindow();
        if (_commentPending.Count > 0) ScheduleCommentAutoLoad();
        return added;
    }

    private void OnCommentMoreRetryClick(object sender, RoutedEventArgs e) => _ = LoadMoreCommentsAsync();

    /// <summary>
    /// 同步评论列表底部的翻页状态。
    ///
    /// 三种状态互斥: 正在补页(转圈) / 补页失败(点击重试) / 已经到底了。
    /// 没有评论、详情页打开、不在评论 tab 时整块收起。
    /// ★ "本地缓冲还没放完"也算忙碌: 否则右下角会短暂显示"已经到底了", 下一帧又冒出评论。
    /// </summary>
    private void UpdateCommentMoreUi()
    {
        if (CommentMorePanel == null || CommentMoreLoading == null ||
            CommentMoreEnd == null || CommentMoreRetry == null)
            return;

        var onCommentsTab = CommentsPanel != null && CommentsPanel.Visibility == Visibility.Visible;
        var show = onCommentsTab && !IsCommentDetailOpen && _commentsLoaded && _commentItems.Count > 0;
        CommentMorePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var busy = _commentLoadingMore || _commentPending.Count > 0;
        CommentMoreLoading.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CommentMoreRetry.Visibility = !busy && _commentLoadMoreFailed
            ? Visibility.Visible : Visibility.Collapsed;
        CommentMoreEnd.Visibility = !busy && !_commentLoadMoreFailed && !_commentHasMore
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 排到 Loaded 优先级再检查一次"要不要补下一页 / 放下一批"。
    ///
    /// 为什么需要: 首屏只有 30 条、而窗口很高(或评论都很短)时, 内容可能还没填满一屏,
    /// 此时 ScrollChanged 根本不会触发 —— 用户看到的就是"只有这一页, 也滑不动"。
    /// ★ 用 _commentRevealScheduled 合并: 连续滚动会反复调到这里, 没必要排几十个回调。
    /// </summary>
    private void ScheduleCommentAutoLoad()
    {
        if (_closing || _commentRevealScheduled) return;
        _commentRevealScheduled = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _commentRevealScheduled = false;
            if (_closing) return;
            MaybeLoadMoreComments(InfoScroll);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 评论列表底部进入(或接近)视口时补下一页 / 放下一批。
    /// 判据: 列表底边距离视口底部不足一屏就开始补, 避免用户真滑到底才看到空白。
    /// ★ 这里修掉了一个老错误: TransformToVisual 已经把滚动偏移算进去了(得到的是视口坐标),
    ///   旧代码又减了一次 VerticalOffset, 导致实际触发时机比设计晚得多 —— 用户确实会滑到底。
    /// </summary>
    private void MaybeLoadMoreComments(ScrollViewer? sv)
    {
        if (_closing || sv == null || !_commentsLoaded || _commentLoadingMore) return;
        if (CommentsPanel == null || CommentsPanel.Visibility != Visibility.Visible || IsCommentDetailOpen) return;
        if (CommentList == null || CommentList.Visibility != Visibility.Visible) return;

        double bottom;
        try
        {
            bottom = CommentList.TransformToVisual(sv)
                .Transform(new Point(0, CommentList.ActualHeight)).Y;
        }
        catch
        {
            // 列表还没进可视树/正在切换 tab: 下一帧的 ScrollChanged 会再来
            return;
        }

        var viewport = sv.ViewportHeight;
        if (viewport <= 0) return;
        // 列表底部已经离视口底部超过一屏: 还不用补
        if (bottom - viewport >= viewport) return;

        // 本地还有缓冲: 先分批放, 不放完不请求下一页
        if (_commentPending.Count > 0) { RevealPendingComments(); return; }

        // 自动加载失败后不再随每次滚动重试: 交给底部的「点击重试」,
        // 否则网络差时滚一下打一次接口, 只会更糟。
        if (_commentLoadMoreFailed || !_commentHasMore || _commentNext <= 0) return;
        _ = LoadMoreCommentsAsync();
    }

    /// <summary>
    /// 把"更新评论内容窗口"排到 120ms 后。
    /// 滚动时 ScrollChanged 每帧都发, 每次都全量扫描会把 UI 线程占满; 这里合并成一次。
    /// ★ 视口内那几屏不靠这个定时器 —— 见 <see cref="RestoreVisibleCommentCardsNow"/>,
    ///   否则按住滚动条拖到远处时会先看到一帧空白。
    /// </summary>
    private void QueueCommentContentWindow()
    {
        if (_closing) return;
        if (_commentContentWindowTimer == null)
        {
            _commentContentWindowTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _commentContentWindowTimer.Tick += (_, _) =>
            {
                _commentContentWindowTimer?.Stop();
                UpdateCommentContentWindow();
            };
        }
        // ★ 节流而不是防抖: 持续滚动时也要每 120ms 收一次离屏内容,
        //   否则"一路滚到底"的整段时间里内存都堆着(与封面窗口同一个教训)。
        if (_commentContentWindowTimer.IsEnabled) return;
        _commentContentWindowTimer.Start();
    }

    /// <summary>
    /// **同步**恢复当前视口内的评论内容(ScrollChanged 里直接调)。
    ///
    /// ★★ 这是"按住滚动条往上拖, 先空白后刷新"的正面修复:
    ///   旧实现只把恢复挂在 120ms 的 Background 定时器上, 而且向上滚动时
    ///   OnInfoScrollChanged 提前 return, 连定时器都不排。拖回上面时可见的卡片
    ///   还是空模板, 用户就会先看到一片空白, 过一会儿才刷出来。
    ///   现在只要视口里出现被释放的卡片, 就在**这一帧的布局/渲染之前**把它恢复。
    ///
    /// ★ forceLayout: 跳滚/拖动滚动条时, TransformToAncestor 可能还拿着上一屏的
    ///   渲染位置(与 CardWallPanel 里那段说明同一类问题), 必须先 UpdateLayout 再量。
    /// </summary>
    private void RestoreVisibleCommentCardsNow(bool forceLayout = false)
    {
        if (_closing || _commentWindowUpdating || CommentList == null || !_commentsLoaded) return;
        if (CommentsPanel == null || CommentsPanel.Visibility != Visibility.Visible || IsCommentDetailOpen) return;
        var sv = InfoScroll;
        if (sv == null || sv.ViewportHeight <= 0 || CommentList.Items.Count == 0) return;

        _commentWindowUpdating = true;
        try
        {
            if (forceLayout)
            {
                try { sv.UpdateLayout(); }
                catch { /* 布局异常: 按现有位置继续, 总比不恢复好 */ }
            }

            var viewport = sv.ViewportHeight;
            RestoreCommentBand(CommentWindow.RestoreNowTop(viewport),
                CommentWindow.RestoreNowBottom(viewport), int.MaxValue);
        }
        finally
        {
            _commentWindowUpdating = false;
        }
    }

    /// <summary>
    /// 按滚动位置释放/恢复评论卡片的正文与图片。
    ///
    /// 规则(区间见 <see cref="CommentWindow"/>):
    ///   · 视口 + 一小截余量: 同步恢复(见 <see cref="RestoreVisibleCommentCardsNow"/>);
    ///   · 视口外 ±0.75 屏: 每轮限量预取几张, 不一次重建一大片;
    ///   · 超出 ±2 屏: 释放, 常驻窗口从旧版的 9 屏降到约 5 屏。
    /// ★ 只释放**内容**, 容器保留并把高度冻结 —— 如果把容器删掉, 列表会重排、
    ///   用户正在看的位置会跳; 只清 Document 则版式和滚动条都不动。
    /// </summary>
    private void UpdateCommentContentWindow()
    {
        if (_closing || _commentWindowUpdating || CommentList == null || !_commentsLoaded) return;
        if (CommentsPanel == null || CommentsPanel.Visibility != Visibility.Visible || IsCommentDetailOpen) return;
        var sv = InfoScroll;
        if (sv == null || sv.ViewportHeight <= 0) return;

        _commentWindowUpdating = true;
        try
        {
            var viewport = sv.ViewportHeight;

            // ① 视口内: 定时器兜底(例如切回评论 tab 时没有滚动事件, 但内容被释放过)
            RestoreCommentBand(CommentWindow.RestoreNowTop(viewport),
                CommentWindow.RestoreNowBottom(viewport), int.MaxValue);

            // ② 视口外预取: 限量, 避免一次重建几十棵视觉树造成卡顿
            RestoreCommentBand(CommentWindow.PrefetchTop(viewport),
                CommentWindow.PrefetchBottom(viewport), CommentWindow.PrefetchBurst);

            // ③ 释放离视口足够远的卡片
            var releasedAny = false;
            for (var i = 0; i < CommentList.Items.Count; i++)
            {
                if (CommentList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter card) continue;
                if (!_commentCardParts.TryGetValue(card, out var parts))
                {
                    parts = GetCommentCardParts(card);
                    // 模板还没应用: 下一轮再收
                    if (parts.Texts.Length == 0 && parts.Covers.Length == 0) continue;
                }
                if (parts.Released) continue;
                if (!TryGetCommentCardBounds(card, sv, out var top, out var bottom)) continue;
                if (!CommentWindow.ShouldRelease(top, bottom, viewport)) continue;
                ReleaseCommentCard(card, parts);
                if (parts.Released) releasedAny = true;
            }

            // 释放了一批视觉树/位图后请求一次温和回收: 光靠 GC 自己不会马上把工作集交给系统,
            // 任务管理器里的数字就一直是"只涨不落"。(内部有 96MB 闸门 + 800ms 合并, 不会频繁 GC)
            if (releasedAny) MemoryTrim.RequestTrim();
        }
        finally
        {
            _commentWindowUpdating = false;
        }
    }

    /// <summary>
    /// 恢复 [bandTop, bandBottom] 区间内**已被释放**的评论卡片。
    ///
    /// ★ 用二分查找定位第一张与带上沿相交的卡片, 再向两侧扫到带边界:
    ///   不再每次滚动都从第 0 条扫到最后一条(几千条评论时那是实打实的卡顿源)。
    /// ★ 返回实际恢复的张数(maxRestores 用来限制后台预取一次补太多)。
    /// </summary>
    private int RestoreCommentBand(double bandTop, double bandBottom, int maxRestores)
    {
        if (maxRestores <= 0) return 0;
        var count = CommentList!.Items.Count;
        if (count == 0) return 0;
        var sv = InfoScroll;
        if (sv == null) return 0;

        var first = FindFirstCommentIndexAt(sv, bandTop);
        var restored = 0;

        // 先往回扫: first 前面那几张可能已经压到带上沿
        for (var i = Math.Min(first, count) - 1; i >= 0 && restored < maxRestores; i--)
        {
            if (!TryGetCommentCardBounds(i, sv, out var card, out _, out var bottom)) break;
            if (bottom < bandTop) break;
            if (TryRestoreCommentCard(card)) restored++;
        }

        for (var i = Math.Max(first, 0); i < count && restored < maxRestores; i++)
        {
            if (!TryGetCommentCardBounds(i, sv, out var card, out var top, out _))
            {
                // 容器还没生成: 下一轮再来(继续循环也拿不到)
                break;
            }
            if (top > bandBottom) break;
            if (TryRestoreCommentCard(card)) restored++;
        }

        return restored;
    }

    /// <summary>二分查找第一张"下边缘 ≥ y"的评论卡片(视口坐标, y 单调递增)。</summary>
    private int FindFirstCommentIndexAt(ScrollViewer sv, double y)
    {
        var count = CommentList!.Items.Count;
        var lo = 0;
        var hi = count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (!TryGetCommentCardBounds(mid, sv, out _, out _, out var bottom))
                return FindFirstCommentIndexAtLinear(sv, y);
            if (bottom >= y) hi = mid - 1;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>容器还没全部生成时的兜底扫描(正常路径不会走到)。</summary>
    private int FindFirstCommentIndexAtLinear(ScrollViewer sv, double y)
    {
        var count = CommentList!.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (TryGetCommentCardBounds(i, sv, out _, out _, out var bottom) && bottom >= y)
                return i;
        }
        return count;
    }

    private bool TryGetCommentCardBounds(int index, ScrollViewer sv,
        out ContentPresenter card, out double top, out double bottom)
    {
        card = null!;
        top = bottom = 0;
        if (CommentList == null || index < 0 || index >= CommentList.Items.Count) return false;
        if (CommentList.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter c) return false;
        card = c;
        return TryGetCommentCardBounds(c, sv, out top, out bottom);
    }

    private static bool TryGetCommentCardBounds(ContentPresenter card, ScrollViewer sv,
        out double top, out double bottom)
    {
        top = bottom = 0;
        var h = card.ActualHeight;
        if (h <= 0) return false;
        try { top = card.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
        catch { return false; }
        if (double.IsNaN(top) || double.IsInfinity(top)) return false;
        bottom = top + h;
        return true;
    }

    /// <summary>这张卡如果正处于"已释放"状态, 把内容重建回来; 返回是否真的恢复了一张。</summary>
    private bool TryRestoreCommentCard(ContentPresenter card)
    {
        if (!_commentCardParts.TryGetValue(card, out var parts) || !parts.Released) return false;
        RestoreCommentCard(card, parts);
        return true;
    }

    /// <summary>
    /// 释放**所有**评论卡片的正文/图片(切走评论 tab 或进入详情页时用)。
    ///
    /// ★ 为什么值得做: 这两个时刻根评论列表整块被隐藏, 但它的 RichTextBox/FlowDocument
    ///   仍然常驻 —— 热门视频几十条评论能占十几 MB。切走时放掉, 回来再按视口重建。
    /// ★ 高度拿不到的卡片(还没排版)跳过, 它本来也没有可放的内容。
    /// </summary>
    private void ReleaseAllCommentContent()
    {
        if (_closing || _commentCardParts.Count == 0) return;

        var released = 0;
        foreach (var kv in _commentCardParts)
        {
            if (kv.Value.Released) continue;
            if (kv.Key.ActualHeight <= 0) continue;
            ReleaseCommentCard(kv.Key, kv.Value);
            if (kv.Value.Released) released++;
        }
        if (released > 0) MemoryTrim.RequestTrim();
    }

    /// <summary>
    /// 清空/重建评论列表前调用: 把离屏时冻结的高度与占位模板**就地解除**, 再丢掉缓存。
    ///
    /// ★ 这里**不要**把原模板设回去: temp 列表马上要被清空, 设回原模板会一次性重建
    ///   所有离屏评论的视觉树(RichTextBox/图片), 造成一次巨大的内存与 CPU 尖峰。
    ///   容器随后会被 ItemsControl 清掉/复用, 生成器会用 ItemTemplate 重新准备新容器。
    /// ★ 但冻结高度与本地占位模板必须清掉: 它们挂在容器上是本地值, 容器若被复用会带过去。
    /// ★ 分批展示的缓冲与去重表也一起清: 它们属于"这一份列表", 换列表后必须作废。
    /// </summary>
    private void ResetCommentCardParts()
    {
        foreach (var kv in _commentCardParts)
        {
            var card = kv.Key;
            var parts = kv.Value;
            if (parts.Released) card.ClearValue(FrameworkElement.HeightProperty);
            if (parts.TemplateSwapped) card.ClearValue(ContentPresenter.ContentTemplateProperty);
        }
        _commentCardParts.Clear();
        _commentPending.Clear();
        _commentSeenRpids.Clear();
        _commentRevealScheduled = false;
        _commentWindowUpdating = false;
    }

    /// <summary>缓存一张卡片里的正文控件与封面图(每张只 BFS 一次)。</summary>
    private CommentCardParts GetCommentCardParts(ContentPresenter card)
    {
        if (_commentCardParts.TryGetValue(card, out var parts)) return parts;

        parts = new CommentCardParts();
        var texts = new List<CommentText>();
        CollectCommentTexts(card, texts);
        parts.Texts = texts.ToArray();
        parts.Covers = Cover.FindCoverBorders(card);
        // ★ 模板还没应用时不要缓存"空结果": 否则这张卡会永远被当成"没有可释放部件",
        //   之后即使模板长出来也不会再收集(与卡墙 CoverBorders 的缓存教训同款)。
        if (parts.Texts.Length == 0 && parts.Covers.Length == 0) return parts;
        _commentCardParts[card] = parts;
        return parts;
    }

    private static void CollectCommentTexts(DependencyObject root, List<CommentText> list)
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is CommentText ct) list.Add(ct);
            CollectCommentTexts(child, list);
        }
    }

    /// <summary>
    /// 释放一张离屏评论卡片。
    ///
    /// ★ 优先**换模板**: 把 ContentTemplate 临时换成空模板, 销毁整棵评论卡片视觉树
    ///   (RichTextBox/FlowDocument/头像/图片), 只保留容器和冻结的高度。
    ///   这是"无虚拟化列表"里最接近虚拟化的做法, 内存降得比只清文档彻底得多。
    /// ★ 模板不可用时退化为"只清正文文档 + 释放封面图", 仍然有效。
    /// </summary>
    private void ReleaseCommentCard(ContentPresenter card, CommentCardParts parts)
    {
        if (parts.Released) return;
        var h = card.ActualHeight;
        if (h <= 0) return;

        var placeholder = CommentPlaceholderTemplate;
        var original = card.ContentTemplate;
        if (placeholder != null && original != null)
        {
            card.Height = h;
            parts.OriginalTemplate = original;
            parts.TemplateSwapped = true;
            // 先把引用清掉再换模板: 旧视觉树马上会被销毁, 留着这些引用没有意义
            parts.Texts = Array.Empty<CommentText>();
            parts.Covers = Array.Empty<Border>();
            card.ContentTemplate = placeholder;
            parts.Released = true;
            return;
        }

        // 退化路径: 模板拿不到时只释放正文与图片
        if (parts.Texts.Length == 0) return;
        card.Height = h;
        foreach (var t in parts.Texts) t.ReleaseContent();
        foreach (var b in parts.Covers) Cover.Release(b);
        parts.Released = true;
    }

    /// <summary>把回到视野附近的评论卡片内容重建回来。</summary>
    private void RestoreCommentCard(ContentPresenter card, CommentCardParts parts)
    {
        if (!parts.Released) return;

        if (parts.TemplateSwapped)
        {
            card.ClearValue(FrameworkElement.HeightProperty);
            card.ContentTemplate = parts.OriginalTemplate;
            // 模板换回来后视觉树会重新生成; 从缓存里移除, 下一轮重新收集新的控件引用。
            _commentCardParts.Remove(card);
            return;
        }

        card.ClearValue(FrameworkElement.HeightProperty);
        foreach (var t in parts.Texts) t.RestoreContent();
        foreach (var b in parts.Covers) Cover.Restore(b, force: true);
        parts.Released = false;
    }

    /// <summary>
    /// 切换评论排序。已经**实际展示**的就是这个顺序就直接返回 —— 否则点一下会白刷一次接口,
    /// 而重拉期间入口是收起的, 用户看到的是"点了闪一下什么都没有"。
    /// ★ 比较的是 <see cref="_effectiveCommentSort"/>, 不是 _commentSort:
    ///   热门回退成时间后两者不同, 用户点「热门」应该真的重拉一次, 不能被短路。
    /// </summary>
    private void SetCommentSort(int mode)
    {
        if (_closing || _effectiveCommentSort == mode) return;
        _commentSort = mode;
        _effectiveCommentSort = mode;
        UpdateCommentSortUi(visible: true, sortOverride: mode);
        // ★★ 2026-11-05: 详情页开着时先把**眼前的楼中楼**按新顺序排一遍。
        //   根评论列表在详情页背后, 重拉它用户看不见 —— 不排这边的回复,
        //   点了「热门 / 时间」就是"按钮亮了, 内容一动不动"。
        if (IsCommentDetailOpen) SortDetailReplies();
        _ = LoadCommentsAsync(force: true);
    }

    /// <summary>
    /// 按当前排序重排详情页里**已加载**的楼中楼回复。
    ///
    /// ★ 为什么必须本地排: 根评论列表有接口的 mode(热门=3 / 时间=2),
    ///   但楼中楼接口 /x/v2/reply/reply **没有排序参数**, 永远按时间正序返回。
    ///   详情页里点排序时若不重排, 眼前的顺序不会有任何变化。
    /// ★ 热门 = 点赞数降序(再按时间新→旧); 时间 = 发布时间降序,
    ///   与接口 mode=2 的"最新在前"保持一致。
    /// ★ 用 Move 而不是 Clear+Add: 后者会重建所有回复容器, 长楼中楼会闪一下并丢滚动位置。
    /// </summary>
    private void SortDetailReplies()
    {
        if (_detailThreadRoot != null) SortRepliesInPlace(_detailThreadRoot);
    }

    private void SortRepliesInPlace(CommentItem root)
    {
        if (root == null || root.Replies.Count < 2) return;

        var ordered = _effectiveCommentSort == 2
            ? root.Replies.OrderByDescending(r => r.Ctime)
                .ThenByDescending(r => r.Rpid)
                .ToList()
            : root.Replies.OrderByDescending(r => r.LikeCount)
                .ThenByDescending(r => r.Ctime)
                .ThenByDescending(r => r.Rpid)
                .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var idx = root.Replies.IndexOf(ordered[i]);
            if (idx != i) root.Replies.Move(idx, i);
        }
        root.SyncPreviewReplies();
    }

    /// <summary>
    /// 展开某条评论下的全部回复(楼中楼)。
    /// 首页接口只内联前 3 条, 这里按需把剩下的补齐 —— 补完 HasMoreReplies 会变 false,
    /// 按钮自己就消失了, 不需要额外的状态管理。
    /// </summary>
    // ------------------------------------------------------------ 评论详情(点楼中楼方框进入)

    private CommentItem? _detailThreadRoot;   // 楼中楼的线程根(拉取回复时当 root 参数)
    private CommentItem? _detailFocus;        // 详情卡片上展示的那一条
    private CommentItem? _replyTarget;        // 详情页输入框当前要回复谁; null = 发表普通评论

    /// <summary>
    /// 点评论卡片里的**楼中楼方框** → 进详情页看这条评论的全部回复。
    /// 方框长在顶层评论下面, 所以线程根就是这条评论自己。
    /// (上一版是"查看详情"文字链接 + 楼中楼条目各自可点, 都已并入方框这一个入口。)
    /// </summary>
    private void OnReplyBoxClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_closing || (sender as FrameworkElement)?.DataContext is not CommentItem item) return;
        // ★ 点在预览里的链接上: 那是"打开链接", 不是"看这条评论的全部回复"。
        //   Hyperlink 已经自己处理了 Click, 这里必须让路, 否则一次点击会开两个东西。
        if (VisualTreeUtil.FindAncestor<System.Windows.Documents.Hyperlink>(
                e.OriginalSource as DependencyObject) != null)
            return;
        // ★ 拖选正文后松手不算"点框"(预览里的 CommentText 是可拖选的 RichTextBox)。
        if (_replyBoxPressPoint is { } down && e.GetPosition(this) is var up
            && (Math.Abs(up.X - down.X) > ReplyBoxClickTolerance
                || Math.Abs(up.Y - down.Y) > ReplyBoxClickTolerance))
            return;
        ShowCommentDetail(threadRoot: item, focus: item);
    }

    /// <summary>楼中楼预览框里"按下 → 松开"位移超过它就算拖选, 不当点击。</summary>
    private const double ReplyBoxClickTolerance = 4;

    /// <summary>预览框按下的位置。用于区分"点一下"与"拖选一段文字"。</summary>
    private Point? _replyBoxPressPoint;

    private void OnReplyBoxPress(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _replyBoxPressPoint = e.GetPosition(this);
    }

    private void ShowCommentDetail(CommentItem threadRoot, CommentItem focus)
    {
        _detailThreadRoot = threadRoot;
        _detailFocus = focus;
        // Host 的 DataContext = 线程根: "相关回复共 N 条"/回复列表/加载更多都从它取
        CommentDetailHost.DataContext = threadRoot;
        CommentDetailCard.Content = focus;
        // 进详情时按当前排序先排一次: 楼中楼接口固定按时间正序返回,
        // 而排序入口上亮着的可能是「热门」—— 不排的话两者对不上。
        SortRepliesInPlace(threadRoot);
        // tab 文案随状态走: 进详情后"评论"变"评论详情";
        // 「返回评论列表」在**右下角悬浮工具列**(2026-11-05 从 tab 行搬走:
        // 它压在「热门 / 时间」排序按钮上, 排序点不到), 这里把它亮出来。
        TabComments.Content = "评论详情";
        BtnBackToComments.Visibility = Visibility.Visible;
        // 评论区只有一个输入框(悬浮面板), 进详情页它自动切成"回复 @这层楼"
        // (用户规则: 在详情页评论 = 给这层楼评论); 点提示条上的"取消"才改回发新楼层。
        // **不动用户已经写了一半的草稿** —— 只是想看看回复, 不该把评论内容清掉。
        // focus: false —— 一进门就抢焦点容易误触输入法。
        // ★ openPanel: false(2026-11-01 修"每次进详情页都弹输入框"): 进详情页只切语义,
        //   不弹面板 —— 想回复时点某条的"回复"按钮或右下角 FAB, 面板自然会展开,
        //   且语义已经是对的(回复这层楼)。
        SetReplyTarget(threadRoot, focus: false);
        // 根评论列表马上要被详情页挡住: 先把它的富文本/图片放掉(内存优化)。
        // 返回列表时 CloseCommentDetail 会按当前视口把它们重建回来。
        ReleaseAllCommentContent();
        CommentList.Visibility = Visibility.Collapsed;
        CommentDetailPanel.Visibility = Visibility.Visible;
        // 详情页打开: 底部的"加载更多评论"属于根评论列表, 先收起
        UpdateCommentMoreUi();
        // 「回到顶部」只属于评论列表: 详情页这一格让给「返回评论列表」
        UpdateCommentBackToTop(InfoScroll);
        // "楼中楼自动展开所有评论": 进来就把这条线程剩下的回复补全, 不再要用户点"展开更多"
        if (threadRoot.HasMoreReplies) _ = AutoCompleteThreadRepliesAsync(threadRoot);
        // 滚动交给外层右面板的 ScrollViewer: 让详情面板滚进视野
        CommentDetailPanel.BringIntoView();
    }

    /// <summary>
    /// 把一条线程的回复**自动补全**(用户要求"楼中楼自动展开所有评论")。
    ///
    /// ★ 2026-11-04 上限从 5 页降到 3 页(约 60 条): 每条回复都是一个 RichTextBox,
    ///   100 条一口气实例化 + 排版实测要 6 秒上下, 用户看到的就是"详情页卡死"。
    ///   60 条仍在 1 秒级, 剩下的用底部"展开更多"按钮按需拉。
    /// ★ 每页之间 Dispatcher.Yield 一次, 让窗口有机会处理输入/重绘 —— 否则 3 页连拉
    ///   仍然是一次长阻塞。
    /// 死循环保护: 服务端若重复返回同一页(去重后一条没新增)立即收手。
    /// </summary>
    private async Task AutoCompleteThreadRepliesAsync(CommentItem root)
    {
        try
        {
            // ★ 记住发起时的 aid: 自动补全可能跨几秒, 期间用户可能换片。
            //   不记的话会用**新视频的 aid** 去拉**旧评论的 rpid**, 拿到一堆无关回复。
            var aidAtStart = _aid;
            var guard = 0;
            while (!_closing && root.HasMoreReplies && guard++ < 3)
            {
                var (_, _, more) = await Svc.Api.GetReplyRepliesAsync(aidAtStart, root.Rpid, pn: root.RepliesNextPage, ps: 20);
                if (_closing || _aid != aidAtStart) return;
                // 用户已经关掉详情页(或换到别的线程): 不能再往 root 里塞回复 ——
                // 关闭时已经把 Replies 收回到预览量(见 CommentItem.TrimRepliesToPreview),
                // 这里继续加会把刚释放的内存又堆回来。
                if (!IsCommentDetailOpen || !ReferenceEquals(_detailThreadRoot, root)) return;
                if (more.Count == 0)
                {
                    // 服务端取不全: 用实际条数收口, 否则按钮会一直挂着
                    root.ReplyCount = root.Replies.Count;
                    break;
                }
                root.RepliesNextPage++;
                var seen = new HashSet<long>(root.Replies.Select(x => x.Rpid));
                var added = 0;
                foreach (var r in more)
                    if (r.Rpid > 0 && seen.Add(r.Rpid)) { root.Replies.Add(r); added++; }
                if (added == 0) break;
                // 让出一帧: 20 条富文本模板的实例化/排版会占满 UI 线程
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            // 自动补全的几页是按时间正序回来的: 补完再按"当前排序"归位,
            // 否则「热门」模式下新补进来的旧回复会全堆在列表末尾。
            SortRepliesInPlace(root);
            root.NotifyRepliesChanged();
        }
        catch (Exception ex)
        {
            // 自动补全是"锦上添花": 失败不能影响已经渲染出来的回复
            App.ReportError(ex);
        }
    }

    private void OnCommentDetailBack(object sender, RoutedEventArgs e) => CloseCommentDetail();

    /// <summary>评论详情页当前是否开着(排序刷新失败时据此决定要不要留下排序入口)</summary>
    private bool IsCommentDetailOpen =>
        CommentDetailPanel != null && CommentDetailPanel.Visibility == Visibility.Visible;

    /// <summary>关掉详情视图(返回列表 / 切 tab / 换视频时都要调, 防止残留旧内容)</summary>
    private void CloseCommentDetail()
    {
        if (CommentDetailPanel.Visibility != Visibility.Visible) return;
        // ★ 记下"刚才在看哪一条", 供返回后滚回去用(见 _commentReturnRpid)。
        //   必须在清空 _detailThreadRoot **之前**取, 否则信息就没了。
        var closingRoot = _detailThreadRoot;
        _commentReturnRpid = closingRoot?.Rpid ?? 0;
        CommentDetailPanel.Visibility = Visibility.Collapsed;
        CommentDetailCard.Content = null;
        CommentDetailHost.DataContext = null;
        // 详情页里自动补全的楼中楼收回到列表预览的量(内存优化, 见 CommentItem.TrimRepliesToPreview)。
        // ★ 必须在 _detailThreadRoot 清空之前做; 也必须在根列表恢复显示之前做,
        //   免得用户看到预览方框先变一下又变回来。
        closingRoot?.TrimRepliesToPreview();
        _detailThreadRoot = _detailFocus = null;
        ClearReplyTarget();
        // tab 文案与右下角悬浮工具列里的返回按钮一起还原
        TabComments.Content = "评论";
        BtnBackToComments.Visibility = Visibility.Collapsed;
        CommentList.Visibility = Visibility.Visible;
        // 进详情页时根评论列表的内容被整块释放了(见 ShowCommentDetail), 现在按当前视口补回来。
        // ★ 同步恢复: 让用户返回列表的同一帧就能看到评论, 而不是先看到一屏空占位。
        RestoreVisibleCommentCardsNow(forceLayout: true);
        // 从详情页回到列表: 底部的翻页状态重新按当前根评论列表显示
        UpdateCommentMoreUi();
        // 详情页打开期间缓冲的"下一批"没有展示(见 RevealPendingComments 的闸门):
        // 回到列表后按当前位置继续放。
        ScheduleCommentAutoLoad();
        // 返回列表后「回到顶部」重新按当前滚动位置决定亮不亮
        UpdateCommentBackToTop(InfoScroll);
        QueueCommentContentWindow();
        // ★ 用户报"从某条评论的回复返回后, 列表回到顶部, 得重新下拉找那条评论"
        //   (2026-10-13): 默认回到**刚才那条评论**所在的位置。
        //   ★ 走 Dispatcher 排队: 上面刚把 CommentList 从 Collapsed 恢复成 Visible,
        //     此刻容器还没生成(ContainerFromItem 会返回 null), 直接找必然落空。
        if (_commentReturnRpid > 0)
            Dispatcher.BeginInvoke(new Action(ScrollCommentIntoView),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 从评论详情返回列表后要滚回去的那条评论(rpid); 0 = 不需要滚动。
    ///
    /// ★ 为什么单独存 rpid 而不是留着 CommentItem 引用: 重新拉列表(换排序/发表后刷新)时
    ///   会换成一批**新的** CommentItem 实例, 引用会失效; rpid 是服务端主键, 跨刷新仍然对得上。
    /// </summary>
    private long _commentReturnRpid;

    /// <summary>
    /// 把 <see cref="_commentReturnRpid"/> 对应的那条评论滚进视野并闪一下。
    ///
    /// ★ 找不到就**静默放弃**: 排序换过、或那条已被删除时它确实不在列表里,
    ///   这时弹提示反而打扰用户 —— 回到顶部是合理退路。
    /// ★ 闪一下是必要的: 光滚过去, 用户仍不知道"是哪一条"(与"查看对话"同一个理由)。
    /// </summary>
    private void ScrollCommentIntoView()
    {
        var rpid = _commentReturnRpid;
        _commentReturnRpid = 0;      // 一次性: 用完即清, 免得下次切 tab 又滚一次
        if (rpid <= 0 || _closing || CommentList == null) return;

        var target = (CommentList.ItemsSource as IEnumerable<CommentItem>)
            ?.FirstOrDefault(c => c.Rpid == rpid);
        if (target == null) return;

        if (CommentList.ItemContainerGenerator.ContainerFromItem(target) is not FrameworkElement fe) return;
        fe.BringIntoView();
        // 滚过去之后视口换了一屏: 被释放过的卡片要立刻补回来(否则又会空白一拍)
        RestoreVisibleCommentCardsNow(forceLayout: true);
        // 闪两下: 告诉用户"就是这一条"
        // ★ FillBehavior=Stop 是必须的: 默认 Hold 会让动画把 Opacity 停在末值(0.35),
        //   用户看到的就是"这条评论变透明了, 而且再也回不来"—— 因为它从此被动画接管,
        //   连可见性切换都拉不回 1.0。Stop 表示动画结束后属性**回到本地值**(1)。
        var flash = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(140))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        fe.BeginAnimation(OpacityProperty, flash);
    }

}
