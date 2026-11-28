using BiliDesk.Services;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 诊断状态/事件流水的行为契约(2026-11-28 新增)。
///
/// ★ 这些数据是"冻结/崩溃现场"的唯一来源, 所以它的两条性质必须锁住:
///   ① 状态是**覆盖写**的当前快照(不是流水) —— 读到的必须是"此刻"的;
///   ② 流水是**只追加**的, 且有上限 —— 不能因为用户用了一整天就把内存撑爆。
/// </summary>
public class DiagnosticsStateTests
{
    [Fact]
    public void 状态是覆盖写()
    {
        DiagnosticsState.Set("测试键", "第一值");
        Assert.Equal("第一值", DiagnosticsState.Get("测试键"));
        DiagnosticsState.Set("测试键", "第二个值");
        Assert.Equal("第二个值", DiagnosticsState.Get("测试键"));
        DiagnosticsState.Set("测试键", null);
        Assert.Null(DiagnosticsState.Get("测试键"));
    }

    [Fact]
    public void 没设过的键返回null()
        => Assert.Null(DiagnosticsState.Get("从没设过的键-" + Guid.NewGuid().ToString("N")));

    [Fact]
    public void 流水按追加顺序保留()
    {
        var tag = Guid.NewGuid().ToString("N");
        DiagnosticsState.Note($"[{tag}] 第一条");
        DiagnosticsState.Note($"[{tag}] 第二条");

        var journal = DiagnosticsState.SnapshotJournal();
        var i1 = journal.ToList().FindIndex(l => l.Contains($"[{tag}] 第一条"));
        var i2 = journal.ToList().FindIndex(l => l.Contains($"[{tag}] 第二条"));
        Assert.True(i1 >= 0, "第一条流水没进环形缓冲");
        Assert.True(i2 > i1, "流水的先后顺序不对");
    }

    [Fact]
    public void 流水超过上限就丢最老的()
    {
        // 先塞满, 再塞一条 —— 最早那条应该被挤掉
        for (var i = 0; i < DiagnosticsState.JournalCapacity + 5; i++)
            DiagnosticsState.Note($"填充-{i}-" + Guid.NewGuid().ToString("N"));

        Assert.True(DiagnosticsState.SnapshotJournal().Count <= DiagnosticsState.JournalCapacity,
            "流水长度超过了容量上限");
    }

    [Fact]
    public void 空白消息不进流水()
    {
        var before = DiagnosticsState.SnapshotJournal().Count;
        DiagnosticsState.Note("   ");
        DiagnosticsState.Note("");
        Assert.Equal(before, DiagnosticsState.SnapshotJournal().Count);
    }

    [Fact]
    public void 描述文本同时包含状态与流水()
    {
        var tag = Guid.NewGuid().ToString("N");
        DiagnosticsState.Set($"状态键-{tag}", "值");
        DiagnosticsState.Note($"流水-{tag}");

        var text = DiagnosticsState.Describe();
        Assert.Contains("当前状态", text);
        Assert.Contains("最近事件", text);
        Assert.Contains($"状态键-{tag}", text);
        Assert.Contains($"流水-{tag}", text);
    }

    [Fact]
    public void 快照是副本_外部改动不影响内部状态()
    {
        var key = "副本键-" + Guid.NewGuid().ToString("N");
        DiagnosticsState.Set(key, "原值");
        var snapshot = DiagnosticsState.SnapshotState();
        // 快照的运行时类型是 Dictionary, 故意改它 —— 内部状态不能跟着变
        ((Dictionary<string, string>)snapshot)[key] = "被外部改了";
        Assert.Equal("原值", DiagnosticsState.Get(key));
    }

    // ----------------- UI 操作标记 -----------------

    [Fact]
    public void UI操作标记在作用域内有效_出了就没了()
    {
        Assert.Null(DiagnosticsState.CurrentUiOperation);
        using (DiagnosticsState.BeginUiOperation("标记甲"))
        {
            Assert.Equal("标记甲", DiagnosticsState.CurrentUiOperation);
        }
        Assert.Null(DiagnosticsState.CurrentUiOperation);
    }

    [Fact]
    public void UI操作标记可嵌套_内层先撤()
    {
        using (DiagnosticsState.BeginUiOperation("外层"))
        {
            using (DiagnosticsState.BeginUiOperation("内层"))
            {
                Assert.Equal("内层", DiagnosticsState.CurrentUiOperation);
                Assert.Equal(2, DiagnosticsState.ActiveUiOperations.Count);
            }
            // 内层撤了, 外层还在
            Assert.Equal("外层", DiagnosticsState.CurrentUiOperation);
        }
        Assert.Null(DiagnosticsState.CurrentUiOperation);
    }

    [Fact]
    public void 同一个名字重复进入不会互相拆掉()
    {
        using (DiagnosticsState.BeginUiOperation("同名"))
        {
            using (DiagnosticsState.BeginUiOperation("同名"))
            {
                // 同名嵌套是两条记录(按栈记, 不去重)
                Assert.Equal(2, DiagnosticsState.ActiveUiOperations.Count);
            }
            // ★ 内层退出只该撤掉**一条**, 外层那条必须还在 ——
            //   否则冻结现场会显示"什么也没在做", 把线索丢掉。
            Assert.Single(DiagnosticsState.ActiveUiOperations);
            Assert.Equal("同名", DiagnosticsState.CurrentUiOperation);
        }
        Assert.Empty(DiagnosticsState.ActiveUiOperations);
        Assert.Null(DiagnosticsState.CurrentUiOperation);
    }

    [Fact]
    public void 操作标记按进入顺序_最内层才算当前()
    {
        using (DiagnosticsState.BeginUiOperation("外层"))
        {
            using (DiagnosticsState.BeginUiOperation("中层"))
            {
                using (DiagnosticsState.BeginUiOperation("内层"))
                {
                    // ★ 顺序必须是"进入顺序": Dictionary 的枚举顺序不保证这个,
                    //   一旦乱了, 报告里就会把外层的大步骤当成"当时在做的事"。
                    Assert.Equal(new[] { "外层", "中层", "内层" },
                        DiagnosticsState.ActiveUiOperations.ToArray());
                    Assert.Equal("内层", DiagnosticsState.CurrentUiOperation);
                }
                Assert.Equal("中层", DiagnosticsState.CurrentUiOperation);
            }
            Assert.Equal("外层", DiagnosticsState.CurrentUiOperation);
        }
    }

    [Fact]
    public void UI操作标记也写进状态快照()
    {
        var name = "快照里的标记-" + Guid.NewGuid().ToString("N");
        using (DiagnosticsState.BeginUiOperation(name))
        {
            Assert.Equal(name, DiagnosticsState.Get("UI 线程当前操作"));
            Assert.Contains(name, DiagnosticsState.Describe());
        }
    }

    [Fact]
    public void 空白名字不会留下标记()
    {
        using (DiagnosticsState.BeginUiOperation("   "))
        {
            Assert.Null(DiagnosticsState.CurrentUiOperation);
        }
    }
}
