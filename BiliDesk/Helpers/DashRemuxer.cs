using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 把 B 站 DASH 的两路分片流(视频/音频各一个 .m4s)**纯拷贝**合成一个本地 mp4。
///
/// 为什么需要它 —— 2026-09-25 的实测对照(同一个 76.5 秒视频, 都跳到 60s 处):
/// <code>
///   方案                                  画面恢复    冻结最久   位置回跳   结尾缺失
///   durl 单文件(现状·短视频 720P)          380ms      499ms      0         260ms
///   纯视频远端 DASH(无音轨)                491ms      499ms      0         1911ms
///   远端双流 DASH+input-slave(现状 1080P)  1383ms     577~800ms  1         1911ms
///   本地 MPD + VLC adaptive(方案B)         3805ms     3319ms     3         770ms
/// </code>
/// 结论: **只有"单一输入"(一个本地 mp4)能同时拿到快跳转与完整结尾** —— 远端双流那套
/// `input-slave` 的两个硬伤(音轨 EOF 拖垮整个 input、跳转只作用在主输入上)与音轨在本地
/// 还是远端无关(实测把音轨换成本地文件, 结尾照样少 1911ms)。
///
/// ★ **产出必须是"普通 MP4"(完整样本表), 不是分片 MP4** —— 这是踩了两轮才定下来的:
///   第一版把两路的 moof/mdat 按时间交错、拼成分片 MP4。数据是对的(32 个分片的样本字节
///   逐字节校验一致, data_offset、mfhd 序号、elst 也都补齐了), 但真机上**跳转花屏 + 音轨
///   不同步**。原因是没有索引的分片 MP4 在跳转时只能靠解复用器边扫边定位, 落点不保证落在
///   同步样本上。B 站自己那份能完美跳转的单流文件(durl)是**普通 MP4**: `ftyp+moov+free+mdat`,
///   moov 里是完整的 `stbl`(stts/ctts/stss/stsc/stsz/stco), 解复用器据此能精确跳到关键帧。
///   所以这里把分片"展开"成普通 MP4: 样本数据仍是**原字节拷贝、不重编码**, 只是把 trun 里的
///   样本表翻译成 stts/ctts/stss/stsc/stsz/stco。
///
/// 六个"不经手就会错"的点(前四个是分片版踩出来的, 后两个是展开时必须守住的):
///   1. **样本数据只搬不改**: 每个分片的样本在源文件里是连续的(起点 = moof 起点 + trun.data_offset,
///      长度 = 各样本 size 之和), 整段拷贝即可。
///   2. **tfdt / 样本时长用 mdhd 的媒体时间基换算秒**(视频 16000 / 音频 44100), 不是 mvhd 的 1000 ——
///      用错会把两路的交错顺序整体排错。
///   3. 两路各自 trak 的 track_id **原本都是 1**, 合进同一 moov 必须把音频改成 2。
///   4. **`elst` 必须原样保留 `media_time`**(视频 1600 / 音频 0): 拿 B 站自己的单流文件比对过,
///      那是它有意的 0.1 秒音画对位(视频首个样本的 cts 偏移正好是 1600)。只有 `segment_duration`
///      (源文件里是占位的 0) 要补成该轨的真实时长。
///   5. **同步样本表(stss)只能由样本标志推**: 视频每个分片的第 1 个样本是同步样本
///      (`first_sample_flags=0x02000000`, 即 non-sync 位为 0), 其余用 tfhd 的默认标志
///      (`0x01010000`, non-sync 位为 1)。**这份表就是跳转能不能准的关键**, 少了它只能靠猜。
///   6. **写 moov 需要知道 mdat 里每个分片的位置, 而 moov 在 mdat 前面** —— 所以先算布局,
///      用占位偏移组一遍 moov 拿到长度(长度只取决于表的条目数, 与偏移值无关), 再填真实偏移重建。
///      两次长度必须一致, 不一致说明中间有依赖偏移长度的东西, 直接放弃(宁可回落到远端播放)。
/// </summary>
public static class DashRemuxer
{
    private const int CopyBufSize = 256 * 1024;

    /// <summary>合流。失败返回 false 并给出原因(调用方会当作"没这个优化", 照旧远端播放)</summary>
    public static bool TryMerge(string videoPath, string audioPath, string outPath, out string? error)
    {
        error = null;
        FileStream? vFs = null, aFs = null;
        var tmp = outPath + ".tmp";
        try
        {
            vFs = OpenRead(videoPath);
            aFs = OpenRead(audioPath);

            var v = SourceTrack.Parse(vFs, 1, out var verr);
            if (v == null) { error = "视频流: " + verr; return false; }
            var a = SourceTrack.Parse(aFs, 2, out var aerr);
            if (a == null) { error = "音频流: " + aerr; return false; }
            if (v.Frags.Count == 0 || a.Frags.Count == 0) { error = "没有解析到分片"; return false; }

            // 交错顺序: 按各分片的解码时刻(同刻先视频)
            var all = new List<Frag>(v.Frags.Count + a.Frags.Count);
            all.AddRange(v.Frags);
            all.AddRange(a.Frags);
            all.Sort((x, y) =>
            {
                var c = x.Seconds.CompareTo(y.Seconds);
                return c != 0 ? c : x.TrackId.CompareTo(y.TrackId);
            });

            // 1) 先排好 mdat 里的位置(写入顺序 = 上面这个顺序), 供 stco 用
            long offset = 0;
            foreach (var f in all)
            {
                f.OutOffset = offset;
                offset += f.TotalSize;
            }
            var mdatPayload = offset;

            // 2) 占位偏移组一遍 moov, 只为拿长度
            var moovProbe = BuildMoov(v, a, 0);
            var mdatStart = v.Ftyp.Length + moovProbe.Length + 8;   // +8 是 mdat 自己的头

            // 3) 填真实偏移重建(moov 里没有长度依赖偏移值的东西, 两次长度必须相等)
            var moov = BuildMoov(v, a, mdatStart);
            if (moov.Length != moovProbe.Length)
            {
                error = "内部错误: moov 两次构建长度不一致";
                return false;
            }

            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                       FileShare.None, CopyBufSize, false))
            {
                dst.Write(v.Ftyp, 0, v.Ftyp.Length);
                dst.Write(moov, 0, moov.Length);

                // mdat: 先写头(用 total - 8 反推实际大小, 保证与实际写入一致)
                var hdr = new byte[8];
                WriteU32(hdr, 0, (uint)(mdatPayload + 8));
                Encoding.ASCII.GetBytes("mdat").CopyTo(hdr, 4);
                dst.Write(hdr, 0, 8);

                foreach (var f in all) CopyBytes(f.Track.Fs, f.SrcDataStart, dst, f.TotalSize);
            }

            // 全部写完才改名: 中断/失败时不会留下一个"看着正常其实是半截"的文件
            if (File.Exists(outPath)) File.Delete(outPath);
            File.Move(tmp, outPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响结果 */ }
            vFs?.Dispose();
            aFs?.Dispose();
        }
    }

    // ------------------------------------------------------------------ 数据模型

    /// <summary>一个样本(只需要能写回 stbl 的字段)</summary>
    private sealed class Sample
    {
        public int Size;
        public long Duration;     // 媒体时间基单位
        public int Flags;         // sample flags(non-sync 位在 0x00010000)
        public int CtsOffset;     // composition time offset(PTS = DTS + 它)
        public bool IsSync => (Flags & 0x0001_0000) == 0;
    }

    /// <summary>一个源分片(展开成输出里"一段连续样本" = 一个 chunk)</summary>
    private sealed class Frag
    {
        public SourceTrack Track = null!;
        public int TrackId => Track.TrackId;
        public double Seconds;          // 解码时刻(秒)
        public long SrcDataStart;       // 该分片样本数据在源文件里的起点
        public long TotalSize;          // 所有样本大小之和
        public readonly List<Sample> Samples = new();
        public long OutOffset;          // 在输出 mdat 载荷中的偏移(回填到 stco)
    }

    private sealed class SourceTrack
    {
        public FileStream Fs = null!;
        /// <summary>**输出**里的 track id(视频 1 / 音频 2)</summary>
        public int TrackId;
        /// <summary>**源文件里**自己声明的 track id。两路各自都是独立 init 段, 所以都是 1 ——
        /// 校验只能验"同一文件内前后一致", 不能拿它跟输出 id 比。</summary>
        public int SrcTrackId;
        public byte[] Ftyp = Array.Empty<byte>();
        public byte[] Mvhd = Array.Empty<byte>();
        public int MvhdVersion;
        public long MvhdTimescale = 1000;
        public int MvhdDurationOffset;
        public byte[] Trak = Array.Empty<byte>();
        public byte[] Stsd = Array.Empty<byte>();
        public int MdiaSizeOffset = -1;
        public int MinfSizeOffset = -1;
        public int TkhdVersion;
        public int TkhdIdOffset = -1;
        public int TkhdDurationOffset = -1;
        public int MdhdVersion;
        public int MdhdDurationOffset = -1;
        public int MdhdTimescale;
        public int ElstVersion;
        public int ElstSegDurOffset = -1;
        public byte[] Udta = Array.Empty<byte>();
        public readonly List<Frag> Frags = new();

        /// <summary>媒体时长(媒体时间基单位) = 所有样本时长之和</summary>
        public long MediaDuration
        {
            get
            {
                long sum = 0;
                foreach (var f in Frags) foreach (var s in f.Samples) sum += s.Duration;
                return sum;
            }
        }

        /// <summary>
        /// 解析一个 .m4s: 取 init 段(ftyp/moov 里需要的东西) + 逐分片展开成样本表。
        /// 结构不认识就返回 null 并给出原因 —— 宁可回落到远端播放, 也不要产出一个坏文件。
        /// </summary>
        public static SourceTrack? Parse(FileStream fs, int trackId, out string error)
        {
            error = "";
            var t = new SourceTrack { Fs = fs, TrackId = trackId };
            var top = TopLevelBoxes(fs);

            foreach (var b in top)
            {
                if (b.Type == "ftyp") t.Ftyp = ReadBox(fs, b.Offset, b.Size);
                else if (b.Type == "moov" && !t.ParseMoov(ReadBox(fs, b.Offset, b.Size), out error)) return null;
            }
            if (t.Ftyp.Length == 0) { error = "缺 ftyp"; return null; }
            if (t.Trak.Length == 0) { error = "缺 trak"; return null; }
            if (t.Stsd.Length == 0) { error = "缺 stsd"; return null; }
            if (t.MdhdTimescale <= 0) { error = "缺 mdhd.timescale"; return null; }

            for (var i = 0; i < top.Count; i++)
            {
                if (top[i].Type != "moof") continue;
                if (i + 1 >= top.Count || top[i + 1].Type != "mdat") { error = "moof 后面不是 mdat"; return null; }
                var moof = ReadBox(fs, top[i].Offset, top[i].Size);
                var frag = ParseFragment(t, moof, top[i].Offset, top[i + 1], out error);
                if (frag == null) return null;
                t.Frags.Add(frag);
            }
            return t;
        }

        /// <summary>解析 moov: 留下需要搬/打补丁的东西, 并记下各长度字段的位置</summary>
        private bool ParseMoov(byte[] moov, out string error)
        {
            error = "";
            foreach (var (type, off, size, hdr) in ChildrenOf(moov))
            {
                switch (type)
                {
                    case "mvhd":
                        Mvhd = Slice(moov, off, size);
                        MvhdVersion = Mvhd[8];
                        if (MvhdVersion == 0)
                        {
                            MvhdTimescale = ReadU32(Mvhd, 20);
                            MvhdDurationOffset = 24;
                        }
                        else
                        {
                            MvhdTimescale = ReadU32(Mvhd, 28);
                            MvhdDurationOffset = 32;
                        }
                        break;

                    case "trak":
                        {
                            Trak = Slice(moov, off, size);
                            var path = FindPath(moov, off + hdr, off + size, "mdia", "minf", "stbl");
                            if (path.Count != 3) { error = "trak 里找不到 mdia/minf/stbl"; return false; }
                            // path 是 moov 内的绝对下标, 换算成 Trak 数组内的相对下标
                            MdiaSizeOffset = path[0].Offset - off;
                            MinfSizeOffset = path[1].Offset - off;

                            var stblOff = path[2].Offset;
                            var stblSize = path[2].Size;
                            foreach (var (bt, bo, bs, _) in Children(moov, stblOff + 8, stblOff + stblSize))
                            {
                                if (bt == "stsd") Stsd = Slice(moov, bo, bs);
                            }
                            if (Stsd.Length == 0) { error = "stbl 里没有 stsd"; return false; }

                            var tk = FindPath(moov, off + hdr, off + size, "tkhd");
                            if (tk.Count != 1) { error = "trak 里找不到 tkhd"; return false; }
                            TkhdVersion = moov[tk[0].Offset + 8];
                            TkhdIdOffset = tk[0].Offset + (TkhdVersion == 0 ? 20 : 28) - off;
                            TkhdDurationOffset = tk[0].Offset + (TkhdVersion == 0 ? 28 : 40) - off;

                            var md = FindPath(moov, off + hdr, off + size, "mdia", "mdhd");
                            if (md.Count != 2) { error = "trak 里找不到 mdia/mdhd"; return false; }
                            MdhdVersion = moov[md[1].Offset + 8];
                            MdhdTimescale = (int)(MdhdVersion == 0
                                ? ReadU32(moov, md[1].Offset + 20)
                                : ReadU32(moov, md[1].Offset + 28));
                            MdhdDurationOffset = md[1].Offset + (MdhdVersion == 0 ? 24 : 32) - off;

                            var elst = FindPath(moov, off + hdr, off + size, "edts", "elst");
                            if (elst.Count == 2)
                            {
                                ElstVersion = moov[elst[1].Offset + 8];
                                ElstSegDurOffset = elst[1].Offset + 16 - off;
                            }
                            break;
                        }

                    case "udta":
                        if (Udta.Length == 0) Udta = Slice(moov, off, size);
                        break;
                }
            }
            if (Mvhd.Length == 0) { error = "缺 mvhd"; return false; }
            return true;
        }

        /// <summary>把一(单 traf、单 trun 的)分片展开成样本列表</summary>
        private static Frag? ParseFragment(SourceTrack t, byte[] moof, long moofStart,
            Box mdat, out string error)
        {
            error = "";
            var frag = new Frag { Track = t };

            long defDur = 0, defSize = 0;
            var defFlags = 0;
            long tfdt = -1, dataOffset = 0;
            var sampleCount = 0;
            var firstSampleFlags = -1;
            var haveDur = false; var haveSize = false; var haveFlags = false; var haveCts = false;
            long[] durs = Array.Empty<long>(), sizes = Array.Empty<long>(), ctss = Array.Empty<long>();
            int[] flags = Array.Empty<int>();
            var trunVersion = 0;
            var trafSeen = 0;

            foreach (var (type, off, size, hdr) in ChildrenOf(moof))
            {
                if (type != "traf") continue;
                trafSeen++;
                foreach (var (t2, off2, size2, _) in Children(moof, off + hdr, off + size))
                {
                    switch (t2)
                    {
                        case "tfhd":
                            {
                                var fl = (int)ReadU24(moof, off2 + 9);
                                var p = off2 + 12;
                                var tid = (int)ReadU32(moof, p); p += 4;
                                if (t.SrcTrackId == 0) t.SrcTrackId = tid;
                                else if (tid != t.SrcTrackId) { error = $"tfhd.track_id 前后不一致({tid} != {t.SrcTrackId})"; return null; }
                                if ((fl & 0x1) != 0) p += 8;                 // base_data_offset
                                if ((fl & 0x2) != 0) p += 4;                 // sample_description_index
                                if ((fl & 0x8) != 0) { defDur = ReadU32(moof, p); p += 4; }
                                if ((fl & 0x10) != 0) { defSize = ReadU32(moof, p); p += 4; }
                                if ((fl & 0x20) != 0) { defFlags = (int)ReadU32(moof, p); p += 4; }
                                break;
                            }
                        case "tfdt":
                            {
                                var ver = moof[off2 + 8];
                                tfdt = ver == 0 ? ReadU32(moof, off2 + 12) : (long)ReadU64(moof, off2 + 12);
                                break;
                            }
                        case "trun":
                            {
                                trunVersion = moof[off2 + 8];
                                var fl = (int)ReadU24(moof, off2 + 9);
                                sampleCount = (int)ReadU32(moof, off2 + 12);
                                var p = off2 + 16;
                                if ((fl & 0x1) != 0) { dataOffset = (int)ReadU32(moof, p); p += 4; }
                                if ((fl & 0x4) != 0) { firstSampleFlags = (int)ReadU32(moof, p); p += 4; }

                                haveDur = (fl & 0x100) != 0;
                                haveSize = (fl & 0x200) != 0;
                                haveFlags = (fl & 0x400) != 0;
                                haveCts = (fl & 0x800) != 0;

                                var need = sampleCount * (
                                    (haveDur ? 4 : 0) + (haveSize ? 4 : 0) +
                                    (haveFlags ? 4 : 0) + (haveCts ? 4 : 0));
                                if (p + need > off2 + size2) { error = "trun 样本表越界"; return null; }

                                if (haveDur) durs = new long[sampleCount];
                                if (haveSize) sizes = new long[sampleCount];
                                if (haveFlags) flags = new int[sampleCount];
                                if (haveCts) ctss = new long[sampleCount];

                                for (var i = 0; i < sampleCount; i++)
                                {
                                    if (haveDur) { durs[i] = ReadU32(moof, p); p += 4; }
                                    if (haveSize) { sizes[i] = ReadU32(moof, p); p += 4; }
                                    if (haveFlags) { flags[i] = (int)ReadU32(moof, p); p += 4; }
                                    if (haveCts)
                                    {
                                        // trun version 1 时 composition offset 是有符号的
                                        ctss[i] = trunVersion == 1 ? (int)ReadU32(moof, p) : ReadU32(moof, p);
                                        p += 4;
                                    }
                                }
                                break;
                            }
                    }
                }
            }

            if (trafSeen != 1) { error = $"一个 moof 里有 {trafSeen} 个 traf, 不认识"; return null; }
            if (tfdt < 0) { error = "缺 tfdt"; return null; }
            if (sampleCount <= 0) { error = "缺 trun 或样本数为 0"; return null; }

            // 样本数据起点: tfhd 没给 base_data_offset 时基准就是 moof 起点(本片源带 default-base-is-moof)
            frag.SrcDataStart = moofStart + dataOffset;
            frag.Seconds = tfdt / (double)t.MdhdTimescale;

            long cursor = frag.SrcDataStart;
            long total = 0;
            for (var i = 0; i < sampleCount; i++)
            {
                var s = new Sample
                {
                    Size = (int)(haveSize ? sizes[i] : defSize),
                    Duration = haveDur ? durs[i] : defDur,
                    // 单个样本的标志优先; 否则第 1 个样本用 first_sample_flags, 其余用 tfhd 缺省
                    Flags = haveFlags ? flags[i]
                        : (i == 0 && firstSampleFlags >= 0 ? firstSampleFlags : defFlags),
                    CtsOffset = haveCts ? (int)ctss[i] : 0
                };
                if (s.Size <= 0) { error = "样本大小为 0"; return null; }
                if (s.Duration <= 0) { error = "样本时长为 0"; return null; }
                frag.Samples.Add(s);
                cursor += s.Size;
                total += s.Size;
            }
            frag.TotalSize = total;

            if (frag.SrcDataStart + total > mdat.Offset + mdat.Size)
            {
                error = "样本数据超出 mdat";
                return null;
            }
            return frag;
        }
    }

    // ------------------------------------------------------------------ 组 moov

    /// <summary>
    /// 组新的 moov: 两条 trak(视频 1 / 音频 2) + 补好的 mvhd/udta。
    /// 每条 trak 都是**原样搬过来**再替换掉 stbl、打几个补丁 —— 这样 hdlr/stsd/编解码参数
    /// 全部保持 B 站原样, 只把我们能算准的表换成新的。
    /// </summary>
    private static byte[] BuildMoov(SourceTrack v, SourceTrack a, long mdatStart)
    {
        var vDurMovie = ToMovieScale(v, v.MediaDuration);
        var aDurMovie = ToMovieScale(a, a.MediaDuration);

        var mvhd = (byte[])v.Mvhd.Clone();
        var maxDur = Math.Max(vDurMovie, aDurMovie);
        if (v.MvhdVersion == 0) WriteU32(mvhd, v.MvhdDurationOffset, (uint)maxDur);
        else WriteU64(mvhd, v.MvhdDurationOffset, (ulong)maxDur);

        var vtrak = BuildTrak(v, v.MediaDuration, vDurMovie, trackId: 1, mdatStart);
        var atrak = BuildTrak(a, a.MediaDuration, aDurMovie, trackId: 2, mdatStart);

        return MakeBox("moov", Concat(mvhd, vtrak, atrak, v.Udta));
    }

    private static long ToMovieScale(SourceTrack t, long mediaDuration) =>
        t.MdhdTimescale <= 0 ? mediaDuration
            : (long)Math.Round(mediaDuration / (double)t.MdhdTimescale * t.MvhdTimescale);

    private static byte[] BuildTrak(SourceTrack t, long mediaDuration, long movieDuration,
        int trackId, long mdatStart)
    {
        var stbl = BuildStbl(t, mdatStart);

        var (stblOff, stblSize) = FindStblInTrak(t);
        var trak = new byte[t.Trak.Length - stblSize + stbl.Length];
        Buffer.BlockCopy(t.Trak, 0, trak, 0, stblOff);
        Buffer.BlockCopy(stbl, 0, trak, stblOff, stbl.Length);
        Buffer.BlockCopy(t.Trak, stblOff + stblSize, trak, stblOff + stbl.Length,
            t.Trak.Length - stblOff - stblSize);

        // 长度字段: trak / mdia / minf 都要跟着新 stbl 改(它们的起点都在 stbl 之前, 偏移没变)
        WriteU32(trak, 0, (uint)trak.Length);
        if (t.MdiaSizeOffset >= 0) WriteU32(trak, t.MdiaSizeOffset, (uint)(trak.Length - t.MdiaSizeOffset));
        if (t.MinfSizeOffset >= 0) WriteU32(trak, t.MinfSizeOffset, (uint)(trak.Length - t.MinfSizeOffset));

        // track_id: 视频 1 / 音频 2(源文件里两边都是 1)
        if (t.TkhdIdOffset >= 0) WriteU32(trak, t.TkhdIdOffset, (uint)trackId);
        if (t.TkhdDurationOffset >= 0)
        {
            if (t.TkhdVersion == 0) WriteU32(trak, t.TkhdDurationOffset, (uint)movieDuration);
            else WriteU64(trak, t.TkhdDurationOffset, (ulong)movieDuration);
        }
        if (t.MdhdDurationOffset >= 0)
        {
            if (t.MdhdVersion == 0) WriteU32(trak, t.MdhdDurationOffset, (uint)mediaDuration);
            else WriteU64(trak, t.MdhdDurationOffset, (ulong)mediaDuration);
        }
        // elst.segment_duration: 源文件里是占位的 0, 补成真实时长(与 B 站自己的单流文件一致)。
        // **media_time 绝不动**: 那是 B 站有意的 0.1 秒音画对位(视频首个样本 cts 偏移正好 1600)。
        if (t.ElstSegDurOffset >= 0 && movieDuration > 0)
        {
            if (t.ElstVersion == 0) WriteU32(trak, t.ElstSegDurOffset, (uint)movieDuration);
            else WriteU64(trak, t.ElstSegDurOffset, (ulong)movieDuration);
        }
        return trak;
    }

    private static (int Offset, int Size) FindStblInTrak(SourceTrack t)
    {
        var trak = t.Trak;
        foreach (var (type, off, size, hdr) in ChildrenOf(trak))
        {
            if (type != "mdia") continue;
            foreach (var (t2, off2, size2, hdr2) in Children(trak, off + hdr, off + size))
            {
                if (t2 != "minf") continue;
                foreach (var (t3, off3, size3, _) in Children(trak, off2 + hdr2, off2 + size2))
                {
                    if (t3 == "stbl") return (off3, size3);
                }
            }
        }
        throw new InvalidOperationException("trak 里找不到 stbl");
    }

    /// <summary>新 stbl: stsd(原样) + stts + [ctts] + [stss] + stsc + stsz + stco</summary>
    private static byte[] BuildStbl(SourceTrack t, long mdatStart)
    {
        var sampleCount = 0;
        foreach (var f in t.Frags) sampleCount += f.Samples.Count;

        var stts = new List<(uint Count, uint Delta)>();
        var ctts = new List<(uint Count, int Offset)>();
        var needCtts = false;
        var stss = new List<uint>();
        var sizes = new uint[sampleCount];
        var chunkOffsets = new List<uint>();
        var stsc = new List<(uint FirstChunk, uint SamplesPerChunk)>();

        var idx = 0;
        uint chunkNo = 0;
        foreach (var f in t.Frags)
        {
            chunkNo++;
            if (stsc.Count == 0 || stsc[^1].SamplesPerChunk != (uint)f.Samples.Count)
                stsc.Add((chunkNo, (uint)f.Samples.Count));

            chunkOffsets.Add((uint)(mdatStart + f.OutOffset));

            foreach (var s in f.Samples)
            {
                sizes[idx] = (uint)s.Size;

                if (stts.Count > 0 && stts[^1].Delta == s.Duration)
                    stts[^1] = (stts[^1].Count + 1, stts[^1].Delta);
                else
                    stts.Add((1, (uint)s.Duration));

                // ctts 只要出现过非零偏移就要整表写出(否则各样本时间对不上)
                if (s.CtsOffset != 0) needCtts = true;
                if (needCtts)
                {
                    if (ctts.Count > 0 && ctts[^1].Offset == s.CtsOffset)
                        ctts[^1] = (ctts[^1].Count + 1, ctts[^1].Offset);
                    else
                        ctts.Add((1, s.CtsOffset));
                }

                // 同步样本表: 视频靠它才能精确跳到关键帧; 音频全是同步样本, 不写 stss
                if (t.TrackId == 1 && s.IsSync) stss.Add((uint)(idx + 1));

                idx++;
            }
        }

        var parts = new List<byte[]> { t.Stsd };

        var p = new byte[4 + stts.Count * 8];
        WriteU32(p, 0, (uint)stts.Count);
        for (var i = 0; i < stts.Count; i++)
        {
            WriteU32(p, 4 + i * 8, stts[i].Count);
            WriteU32(p, 8 + i * 8, stts[i].Delta);
        }
        parts.Add(MakeFullBox("stts", 0, p));

        if (needCtts && ctts.Count > 0)
        {
            var ver = 0;   // 有负偏移时才用 version 1(有符号); 本片源全非负
            foreach (var c in ctts) if (c.Offset < 0) ver = 1;
            var cb = new byte[4 + ctts.Count * 8];
            WriteU32(cb, 0, (uint)ctts.Count);
            for (var i = 0; i < ctts.Count; i++)
            {
                WriteU32(cb, 4 + i * 8, ctts[i].Count);
                WriteU32(cb, 8 + i * 8, (uint)ctts[i].Offset);
            }
            parts.Add(MakeFullBox("ctts", (byte)ver, cb));
        }

        if (t.TrackId == 1 && stss.Count > 0)
        {
            var sb = new byte[4 + stss.Count * 4];
            WriteU32(sb, 0, (uint)stss.Count);
            for (var i = 0; i < stss.Count; i++) WriteU32(sb, 4 + i * 4, stss[i]);
            parts.Add(MakeFullBox("stss", 0, sb));
        }

        var sc = new byte[4 + stsc.Count * 12];
        WriteU32(sc, 0, (uint)stsc.Count);
        for (var i = 0; i < stsc.Count; i++)
        {
            WriteU32(sc, 4 + i * 12, stsc[i].FirstChunk);
            WriteU32(sc, 8 + i * 12, stsc[i].SamplesPerChunk);
            WriteU32(sc, 12 + i * 12, 1);          // sample_description_index
        }
        parts.Add(MakeFullBox("stsc", 0, sc));

        var sz = new byte[8 + sampleCount * 4];
        WriteU32(sz, 0, 0);                        // sample_size = 0: 大小逐个列在后面
        WriteU32(sz, 4, (uint)sampleCount);
        for (var i = 0; i < sampleCount; i++) WriteU32(sz, 8 + i * 4, sizes[i]);
        parts.Add(MakeFullBox("stsz", 0, sz));

        var co = new byte[4 + chunkOffsets.Count * 4];
        WriteU32(co, 0, (uint)chunkOffsets.Count);
        for (var i = 0; i < chunkOffsets.Count; i++) WriteU32(co, 4 + i * 4, chunkOffsets[i]);
        parts.Add(MakeFullBox("stco", 0, co));

        return MakeBox("stbl", Concat(parts.ToArray()));
    }

    // ------------------------------------------------------------------ box 基础操作

    private sealed class Box
    {
        public string Type = "";
        public long Offset;
        public long Size;
        public int Header;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufSize, false);

    private static List<Box> TopLevelBoxes(FileStream fs)
    {
        var list = new List<Box>();
        var len = fs.Length;
        var hdr = new byte[16];
        long pos = 0;
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            if (!ReadExactly(fs, hdr, 0, 8)) break;
            long size = ReadU32(hdr, 0);
            var type = Encoding.ASCII.GetString(hdr, 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (!ReadExactly(fs, hdr, 8, 8)) break;
                size = (long)ReadU64(hdr, 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = len - pos;
            }
            if (size < header || pos + size > len) break;
            list.Add(new Box { Type = type, Offset = pos, Size = size, Header = header });
            pos += size;
        }
        return list;
    }

    private static byte[] ReadBox(FileStream fs, long offset, long size)
    {
        var buf = new byte[size];
        fs.Position = offset;
        if (!ReadExactly(fs, buf, 0, buf.Length)) throw new EndOfStreamException("读取 box 失败");
        return buf;
    }

    private static bool ReadExactly(FileStream fs, byte[] buf, int offset, int count)
    {
        var got = 0;
        while (got < count)
        {
            var n = fs.Read(buf, offset + got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    private static void CopyBytes(FileStream src, long srcOffset, FileStream dst, long count)
    {
        src.Position = srcOffset;
        var buf = new byte[CopyBufSize];
        var left = count;
        while (left > 0)
        {
            var want = (int)Math.Min(buf.Length, left);
            var n = src.Read(buf, 0, want);
            if (n <= 0) throw new EndOfStreamException("拷贝样本数据时提前结束");
            dst.Write(buf, 0, n);
            left -= n;
        }
    }

    /// <summary>
    /// 一个 box 的直接子 box。
    /// **必须跳过它自己的头**: `Children` 是"从 start 开始逐个同级 box", 直接传 0 会把 box
    /// 自己当成第一个子 box(Python 原型与 C# 版各踩过一次, 表现是"结构不认识")。
    /// </summary>
    private static IEnumerable<(string Type, int Offset, int Size, int Header)> ChildrenOf(byte[] box)
    {
        var header = ReadU32(box, 0) == 1 ? 16 : 8;
        return Children(box, header, box.Length);
    }

    private static IEnumerable<(string Type, int Offset, int Size, int Header)> Children(
        byte[] buf, int start, int end)
    {
        var i = start;
        while (i + 8 <= end && i + 8 <= buf.Length)
        {
            long size = ReadU32(buf, i);
            var type = Encoding.ASCII.GetString(buf, i + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (i + 16 > end) break;
                size = (long)ReadU64(buf, i + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - i;
            }
            if (size < header || i + size > end) break;
            yield return (type, i, (int)size, header);
            i += (int)size;
        }
    }

    /// <summary>
    /// 按 "a/b/c" 逐级下钻, 返回路径上每个 box 的 (下标, 长度, 头长)。
    /// 只用于**内存里**的 box 数组, 所以下标用 int。
    /// </summary>
    private static List<(int Offset, int Size, int Header)> FindPath(
        byte[] buf, int start, int end, params string[] names)
    {
        var none = new List<(int Offset, int Size, int Header)>();
        var result = new List<(int Offset, int Size, int Header)>();

        (int Offset, int Size, int Header)? first = null;
        foreach (var (type, off, size, hdr) in Children(buf, start, end))
        {
            if (type != names[0]) continue;
            first = (off, size, hdr);
            break;
        }
        if (first == null) return none;
        result.Add(first.Value);

        for (var level = 1; level < names.Length; level++)
        {
            var prev = result[^1];
            (int Offset, int Size, int Header)? found = null;
            foreach (var (type, off, size, hdr) in
                     Children(buf, prev.Offset + prev.Header, prev.Offset + prev.Size))
            {
                if (type != names[level]) continue;
                found = (off, size, hdr);
                break;
            }
            if (found == null) return none;
            result.Add(found.Value);
        }
        return result;
    }

    private static byte[] MakeBox(string type, byte[] payload)
    {
        var buf = new byte[payload.Length + 8];
        WriteU32(buf, 0, (uint)buf.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(buf, 4);
        Buffer.BlockCopy(payload, 0, buf, 8, payload.Length);
        return buf;
    }

    private static byte[] MakeFullBox(string type, byte version, byte[] payload)
    {
        var buf = new byte[payload.Length + 12];
        WriteU32(buf, 0, (uint)buf.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(buf, 4);
        buf[8] = version;
        // flags = 0
        Buffer.BlockCopy(payload, 0, buf, 12, payload.Length);
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var buf = new byte[total];
        var at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, at, p.Length);
            at += p.Length;
        }
        return buf;
    }

    private static byte[] Slice(byte[] src, int offset, int size)
    {
        var buf = new byte[size];
        Buffer.BlockCopy(src, offset, buf, 0, size);
        return buf;
    }

    private static uint ReadU32(byte[] b, int i) =>
        (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);

    private static uint ReadU24(byte[] b, int i) =>
        (uint)((b[i] << 16) | (b[i + 1] << 8) | b[i + 2]);

    private static ulong ReadU64(byte[] b, int i) =>
        ((ulong)ReadU32(b, i) << 32) | ReadU32(b, i + 4);

    private static void WriteU32(byte[] b, int i, uint v)
    {
        b[i] = (byte)(v >> 24);
        b[i + 1] = (byte)(v >> 16);
        b[i + 2] = (byte)(v >> 8);
        b[i + 3] = (byte)v;
    }

    private static void WriteU64(byte[] b, int i, ulong v)
    {
        WriteU32(b, i, (uint)(v >> 32));
        WriteU32(b, i + 4, (uint)v);
    }
}
