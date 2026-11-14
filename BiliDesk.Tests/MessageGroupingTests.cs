using System.Collections.Generic;
using System.Reflection;
using BiliDesk.Models;
using BiliDesk.ViewModels;
using Xunit;

namespace BiliDesk.Tests;

/// <summary>
/// 微信式消息分组(ShowHeader)的规则测试。
///
/// ★ 为什么值得单测: 它决定"哪一条显示头像", 而这个判断依赖**前后两条的关系** ——
///   写错的表现不是崩溃, 而是"每个人发的每一句都带一次头像"(像一堆散开的卡片),
///   或者反过来"整段对话只有最上面一个头像"。这类错误只能靠断言钉住。
///   MarkGroups 是 private, 这里用反射调: 它没有副作用、只吃 Messages 集合,
///   为此把 VM 拆出一个接口出来反而更绕。
/// </summary>
public class MessageGroupingTests
{
    private static void MarkGroups(object vm)
    {
        var m = vm.GetType().GetMethod("MarkGroups",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(m);
        m!.Invoke(vm, null);
    }

    private static MessagesViewModel NewVm()
    {
        // 注意: 构造 VM 不会发任何网络请求(加载全在 LoadAsync 里), 可以安全地裸建
        return new MessagesViewModel();
    }

    [Fact]
    public void SameSenderConsecutive_OnlyFirstShowsHeader()
    {
        var vm = NewVm();
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "一" });
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "二" });
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "三" });

        MarkGroups(vm);

        Assert.True(vm.Messages[0].ShowHeader);
        Assert.False(vm.Messages[1].ShowHeader);
        Assert.False(vm.Messages[2].ShowHeader);
    }

    [Fact]
    public void SenderSwitch_StartsNewGroup()
    {
        var vm = NewVm();
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "对方" });
        vm.Messages.Add(new MsgItem { SenderUid = 2, IsMine = true, Text = "我" });
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "对方又说" });

        MarkGroups(vm);

        Assert.All(vm.Messages, m => Assert.True(m.ShowHeader));
    }

    [Fact]
    public void MineWithUnknownUid_StillGroupsTogether()
    {
        // ★ 乐观追加的消息里 SenderUid 取不到时是 -1, 而服务端回来的真实 mid 是另一个数 ——
        //   若按 SenderUid 分组, 自己连着发的两句之间会莫名其妙再冒一次头像。
        //   分组键必须是"IsMine ? 0 : SenderUid"。
        var vm = NewVm();
        vm.Messages.Add(new MsgItem { SenderUid = 99, IsMine = true, Text = "服务端回来的" });
        vm.Messages.Add(new MsgItem { SenderUid = -1, IsMine = true, Text = "刚乐观追加的" });

        MarkGroups(vm);

        Assert.True(vm.Messages[0].ShowHeader);
        Assert.False(vm.Messages[1].ShowHeader);
    }

    [Fact]
    public void SystemTip_BreaksGroupAndAlwaysShows()
    {
        var vm = NewVm();
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "一" });
        vm.Messages.Add(new MsgItem { SenderUid = 0, IsMine = false, MsgType = 18, Text = "系统提示" });
        vm.Messages.Add(new MsgItem { SenderUid = 1, IsMine = false, Text = "二" });

        MarkGroups(vm);

        Assert.True(vm.Messages[0].ShowHeader);
        Assert.True(vm.Messages[1].ShowHeader);   // 系统提示永远不带头像, 但它会断开分组
        Assert.True(vm.Messages[2].ShowHeader);   // 提示之后重新开一组
    }

    [Fact]
    public void ShowHeader_RaisesPropertyChanged()
    {
        // 分组是在消息已经绑到界面之后才算的: 不发通知的话那一条仍然显示上一次的值
        var item = new MsgItem { SenderUid = 1, ShowHeader = true };
        var raised = new List<string>();
        item.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        item.ShowHeader = false;

        Assert.Contains(nameof(MsgItem.ShowHeader), raised);
    }

    [Fact]
    public void ShowHeader_NoNotificationWhenUnchanged()
    {
        var item = new MsgItem { SenderUid = 1, ShowHeader = true };
        var count = 0;
        item.PropertyChanged += (_, _) => count++;

        item.ShowHeader = true;

        Assert.Equal(0, count);
    }
}
