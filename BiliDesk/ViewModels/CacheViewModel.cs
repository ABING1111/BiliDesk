using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using BiliDesk.Helpers;
using BiliDesk.Models;
using BiliDesk.Services;

namespace BiliDesk.ViewModels;

/// <summary>
/// 离线缓存页: 管理下载目录(`AppPaths.DownloadDir`)里的视频文件。
///
/// 为什么不再单开一套缓存存储: 应用的「下载视频到本地」本来就是把完整的 MP4 落到下载目录,
/// 下完即可离线播放。再做一份私有缓存等于同一份文件存两遍, 既费磁盘又容易两边不同步。
/// 所以这一页的职责就是"看见 + 播 + 删": 列目录、在应用内播放、打开所在位置、删除、看总占用。
/// </summary>
public class CacheViewModel : ObservableObject
{
    public ObservableCollection<CacheEntry> Items { get; } = new();

    private bool _loading;
    private string _error = "";
    private string _summary = "";

    public bool Loading
    {
        get => _loading;
        private set { if (SetProperty(ref _loading, value)) RaiseEmpty(); }
    }

    public string Error
    {
        get => _error;
        private set { if (SetProperty(ref _error, value)) RaiseEmpty(); }
    }

    /// <summary>"N 个文件 · 共 XXX MB"</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>是否显示"还没有离线缓存"空状态(加载中 / 出错时不显示)</summary>
    public bool ShowEmpty => !Loading && Items.Count == 0 && Error.Length == 0;

    private void RaiseEmpty() => OnPropertyChanged(nameof(ShowEmpty));

    /// <summary>缓存目录的实际路径(直接显示出来, 用户点"打开文件夹"之前就知道文件在哪)。
    /// 名字不能叫 Directory —— 会和 System.IO.Directory 撞名, 同文件里调用静态方法就会报 CS0120。</summary>
    public string CacheDirectory => AppPaths.DownloadDir;

    public ICommand RefreshCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenFolderCommand { get; }

    /// <summary>认作"可离线播放的视频"的扩展名</summary>
    private static readonly string[] MediaExts =
        { ".mp4", ".mkv", ".flv", ".avi", ".mov", ".webm", ".ts", ".m4v", ".wmv" };

    public CacheViewModel()
    {
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        PlayCommand = new RelayCommand(p => { if (p is CacheEntry e) Play(e); });
        RevealCommand = new RelayCommand(p => { if (p is CacheEntry e) RevealInExplorer(e.FilePath); });
        DeleteCommand = new RelayCommand(p => { if (p is CacheEntry e) Delete(e); });
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public async Task LoadAsync()
    {
        // 防重入: 刷新按钮和页面首次显示可能几乎同时触发, 用"正在加载"当哨兵比"列表为空"可靠
        if (Loading) return;
        Loading = true;
        Error = "";
        try
        {
            // 扫目录是同步 IO, 目录里可能躺着几百个文件 —— 放后台线程, 否则切页时会卡一下
            var (list, total) = await Task.Run(ScanDir);

            Items.Clear();
            // 按修改时间倒序(最近下载的在最上面); TimeText 是同格式字符串, 直接倒序排即可
            foreach (var e in list.OrderByDescending(x => x.TimeText)) Items.Add(e);

            Summary = list.Count == 0
                ? "还没有离线缓存"
                : $"{list.Count} 个文件 · 共 {FormatSize(total)}";
        }
        catch (Exception ex)
        {
            Error = "读取缓存目录失败: " + ex.Message;
        }
        finally
        {
            Loading = false;
            RaiseEmpty();
        }
    }

    private static (List<CacheEntry> items, long total) ScanDir()
    {
        var result = new List<CacheEntry>();
        long total = 0;
        try
        {
            var dir = AppPaths.DownloadDir;
            if (!Directory.Exists(dir)) return (result, 0);

            foreach (var f in new DirectoryInfo(dir).EnumerateFiles())
            {
                if (Array.IndexOf(MediaExts, f.Extension.ToLowerInvariant()) < 0) continue;
                // 下载中途的 .part 文件不该出现在列表里
                if (f.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;

                result.Add(new CacheEntry
                {
                    FilePath = f.FullName,
                    Name = Path.GetFileNameWithoutExtension(f.Name),
                    Bytes = f.Length,
                    SizeText = FormatSize(f.Length),
                    TimeText = f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                });
                total += f.Length;
            }
        }
        catch
        {
            // 目录不可读时返回已经扫到的部分, 不把整页打成错误
        }
        return (result, total);
    }

    private void Play(CacheEntry entry)
    {
        if (entry == null) return;
        if (!File.Exists(entry.FilePath))
        {
            Svc.Toast.Show("文件已不存在, 可能被移动或删除了");
            _ = LoadAsync();
            return;
        }
        // 走播放器窗口的本地播放分支(不查接口、不加载弹幕)
        Svc.Player.PlayLocalFile(entry.FilePath, entry.Name);
    }

    private void Delete(CacheEntry entry)
    {
        if (entry == null) return;
        var r = MessageBox.Show(
            $"确定要删除这个离线文件吗?\n\n{entry.Name}\n{entry.SizeText}",
            "BiliDesk", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(entry.FilePath);
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("删除失败: " + ex.Message);
            return;
        }
        Items.Remove(entry);
        UpdateSummary();
        Svc.Toast.Show("已删除");
    }

    private void UpdateSummary()
    {
        var total = Items.Sum(x => x.Bytes);
        Summary = Items.Count == 0
            ? "还没有离线缓存"
            : $"{Items.Count} 个文件 · 共 {FormatSize(total)}";
        RaiseEmpty();
    }

    private static void RevealInExplorer(string path)
    {
        try
        {
            // /select 让资源管理器直接定位到这个文件, 比只打开目录更好找
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开文件夹失败: " + ex.Message);
        }
    }

    private void OpenFolder()
    {
        try
        {
            var dir = AppPaths.DownloadDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Svc.Toast.Show("打开文件夹失败: " + ex.Message);
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024 / 1024).ToString("0.##") + " GB";
        if (bytes >= 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.#") + " MB";
        if (bytes >= 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return bytes + " B";
    }
}
