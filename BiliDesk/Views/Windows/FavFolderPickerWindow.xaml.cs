using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using BiliDesk.Helpers;
using BiliDesk.Models;

namespace BiliDesk.Views
{
    /// <summary>
    /// 收藏夹勾选窗(播放器"收藏"按钮的二级浮层):
    /// 打开时按"视频当前所在的夹"(fav_state)预勾选, 用户增删勾选后点确定,
    /// 调用方对比初始/最终勾选算出 add/del 差集, 一次 /x/v3/fav/resource/deal 提交。
    /// 用模态窗而不是 Popup: 播放器视频区有单击暂停/双击全屏手势,
    /// Popup 的"点外部关闭"会让那次点击漏进视频区误触发手势。
    /// </summary>
    public partial class FavFolderPickerWindow : FluentWindow
    {
        /// <summary>界面态包装: FavFolder 是纯数据模型, 勾选状态属于本次弹窗, 不写回模型</summary>
        private sealed class PickerItem : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;

            public FavFolder Folder { get; init; } = null!;
            public string MetaText { get; init; } = "";

            /// <summary>打开时的勾选态(= 视频当前是否已在该夹), 供确定时算差集</summary>
            public bool InitialChecked { get; init; }

            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
                }
            }
        }

        private readonly ObservableCollection<PickerItem> _items = new();

        /// <summary>是否点了"确定"(false = 用户取消, 调用方直接忽略所有结果)</summary>
        public bool Confirmed { get; private set; }

        /// <summary>确定后相对打开时**新勾上**的收藏夹(要加入的)</summary>
        public List<long> AddedIds =>
            _items.Where(i => i.IsChecked && !i.InitialChecked).Select(i => i.Folder.Id).ToList();

        /// <summary>确定后相对打开时**被取消勾选**的收藏夹(要移出的)</summary>
        public List<long> RemovedIds =>
            _items.Where(i => !i.IsChecked && i.InitialChecked).Select(i => i.Folder.Id).ToList();

        /// <summary>确定后最终勾选的收藏夹(决定播放器"已收藏"高亮态)</summary>
        public List<long> FinalCheckedIds =>
            _items.Where(i => i.IsChecked).Select(i => i.Folder.Id).ToList();

        public FavFolderPickerWindow(IEnumerable<FavFolder> folders, string videoTitle)
        {
            InitializeComponent();
            SubtitleText.Text = string.IsNullOrWhiteSpace(videoTitle) ? "勾选要加入的收藏夹" : videoTitle;

            foreach (var f in folders)
            {
                // 副行元信息: "N 条视频 · 私密"(公开态不写, 减少噪音)
                var meta = f.MediaCount > 0 ? $"{f.MediaCount} 条视频" : "";
                if (f.Privacy == 1) meta = string.IsNullOrEmpty(meta) ? "私密" : meta + " · 私密";
                _items.Add(new PickerItem
                {
                    Folder = f,
                    MetaText = meta,
                    InitialChecked = f.HasVideo,
                    IsChecked = f.HasVideo,
                });
            }
            FolderList.ItemsSource = _items;
            UpdateEmptyHint();
        }

        /// <summary>
        /// 列表空的时候给一句提示 —— 账号下一个收藏夹都没有时, 这里原本是一片空白,
        /// 用户只能靠自己发现左下角那颗「新建收藏夹」(2026-11-19)。
        /// </summary>
        private void UpdateEmptyHint()
            => EmptyHint.Visibility = _items.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// 新建收藏夹: 建完直接插进列表并**默认勾选** —— 用户在建夹的这一刻,
        /// 目的基本就是"把这条视频收进这个新夹", 再让他手动点一下是多余的一步。
        /// ★ InitialChecked 保持 false: 视频本来就不在这个新夹里, 确认时它会进 AddedIds。
        /// </summary>
        private async void OnCreateFolderClick(object sender, RoutedEventArgs e)
        {
            BtnCreateFolder.IsEnabled = false;
            try
            {
                var created = await FavFolderActions.CreateAsync(this);
                if (created == null) return;

                _items.Add(new PickerItem
                {
                    Folder = created,
                    MetaText = created.Privacy == 1 ? "私密" : "",
                    InitialChecked = false,
                    IsChecked = true,
                });
                UpdateEmptyHint();

                // 新夹在列表末尾(按收藏夹顺序它本来就排在最后), 滚到底让用户看见它被勾上了
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    new Action(() => FolderScroll.ScrollToEnd()));
            }
            finally
            {
                BtnCreateFolder.IsEnabled = true;
            }
        }

        private void OnConfirmClick(object sender, RoutedEventArgs e)
        {
            Confirmed = true;
            Close();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            // 不置 Confirmed, Close 即取消
            Close();
        }
    }
}
