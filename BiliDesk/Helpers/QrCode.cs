using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BiliDesk.Helpers;

/// <summary>
/// 二维码(QR Code Model 2)编码器: **字节模式(UTF-8) + 纠错等级 M + 版本 1..10 自动选择**。
/// 版本 10 在纠错 M 下能装 213 字节, 登录链接约 120 字节, 绰绰有余。
///
/// ★ 为什么自己写而不是引第三方库(2026-10-11):
///   登录要自己把 `account.bilibili.com/...?qrcode_key=...` 画成二维码单独呈现,
///   而项目里既没有条码库, 也没有 System.Drawing —— 只用 WPF 矢量绘图就够(见 LoginWindow 的
///   BuildQrImage)。为一个用途引一个 NuGet 包, 会打破"零第三方控件/图形依赖"的现状。
///
/// 覆盖标准全流程: 数据码字 → RS 纠错 → 分块交织 → 功能图案 → 8 种掩码打分选优。
/// 两张表(纠错块结构 / 对齐点位置)与独立实现(python `qrcode` 的 rs_blocks)逐版本核对过,
/// 码字序列与独立实现**逐字节一致**; 渲染出来的图再用 ZXing / OpenCV 解码回读,
/// 版本 1..10 的容量边界用例全部通过(2026-10-11 探针实测)。
/// </summary>
public static class QrCode
{
    /// <summary>
    /// 纠错等级 M 在格式信息里的编码。
    /// 标准规定 L=01 / M=00 / Q=11 / H=10 —— 注意 M 是 0, 不是 1。
    /// </summary>
    private const int EccBitsM = 0;

    // ==================== 版本 1..10 / 纠错等级 M 的标准表 ====================

    /// <summary>每版的数据码字数(= 总码字 - 纠错码字)</summary>
    private static readonly int[] DataCodewords = { 16, 28, 44, 64, 86, 108, 124, 154, 182, 216 };

    /// <summary>每块的纠错码字数</summary>
    private static readonly int[] EcCodewordsPerBlock = { 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };

    /// <summary>分块结构第 1 组: (块数, 每块数据码字数)</summary>
    private static readonly (int Blocks, int Data)[] Group1 =
    {
        (1, 16), (1, 28), (1, 44), (2, 32), (2, 43), (4, 27), (4, 31), (2, 38), (3, 36), (4, 43)
    };

    /// <summary>分块结构第 2 组(比第 1 组每块多 1 个数据码字; 版本 8 起才存在)</summary>
    private static readonly (int Blocks, int Data)[] Group2 =
    {
        (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0), (2, 39), (2, 37), (1, 44)
    };

    /// <summary>对齐图案的中心坐标(行列通用; 版本 1 没有对齐图案)</summary>
    private static readonly int[][] AlignmentCenters =
    {
        Array.Empty<int>(),
        new[] { 6, 18 },
        new[] { 6, 22 },
        new[] { 6, 26 },
        new[] { 6, 30 },
        new[] { 6, 34 },
        new[] { 6, 22, 38 },
        new[] { 6, 24, 42 },
        new[] { 6, 26, 46 },
        new[] { 6, 28, 50 },
    };

    // ★ 2026-10-13 删掉了这里的 RemainderBits 数组(QR 标准规定的"剩余位", 恒为 0):
    //   本项目只编码到版本 2 的字节模式, 而版本 1..2 的剩余位都是 0, 数组从未被读过。
    //   真要往高版本扩时再按标准补回来 —— 版本 2..6 是 7, 其余是 0。

    // ==================== 对外入口 ====================

    /// <summary>
    /// 编码文本, 返回模块矩阵: `true` = 深色。矩阵边长 = 17 + 版本×4(不含静默区)。
    /// 调用方负责加 4 个模块的静默区并按整数倍像素放大绘制。
    /// </summary>
    /// <exception cref="ArgumentException">内容为空</exception>
    /// <exception cref="InvalidOperationException">内容超过版本 10 / 纠错 M 的容量(213 字节)</exception>
    public static bool[,] Encode(string text)
    {
        if (string.IsNullOrEmpty(text)) throw new ArgumentException("二维码内容不能为空", nameof(text));

        var payload = Encoding.UTF8.GetBytes(text);

        int version = 0;
        for (int v = 1; v <= 10; v++)
        {
            // 需要的位数 = 模式指示符(4 位) + 字符计数(1..9 版 8 位, 10 版起 16 位) + 数据
            int countBits = v <= 9 ? 8 : 16;
            if (4 + countBits + payload.Length * 8 <= DataCodewords[v - 1] * 8)
            {
                version = v;
                break;
            }
        }
        if (version == 0)
            throw new InvalidOperationException(
                $"二维码内容过长({payload.Length} 字节), 本编码器支持到版本 10(纠错 M, 上限 213 字节)");

        var dataBits = BuildDataBits(payload, version);
        var codewords = AddErrorCorrection(dataBits, version);
        return BuildMatrix(codewords, version);
    }

    // ==================== 数据位流 ====================

    /// <summary>拼出纠错前的数据位: 模式 + 计数 + 内容 + 结束符 + 填充字节</summary>
    private static List<bool> BuildDataBits(byte[] payload, int version)
    {
        int capacityBits = DataCodewords[version - 1] * 8;
        var bits = new List<bool>(capacityBits);

        void Add(int value, int len)
        {
            for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        Add(0b0100, 4);                                          // 模式指示符: 字节模式
        Add(payload.Length, version <= 9 ? 8 : 16);              // 字符计数
        foreach (var b in payload) Add(b, 8);

        // 结束符: 标准允许最多 4 个 0, 空间不够就少写几个
        Add(0, Math.Min(4, capacityBits - bits.Count));

        // 补到字节边界
        while (bits.Count % 8 != 0) bits.Add(false);

        // 剩下的码字用 0xEC / 0x11 交替填满(标准规定的填充字节, 不能随便填 0)
        for (int i = 0; bits.Count < capacityBits; i++) Add(i % 2 == 0 ? 0xEC : 0x11, 8);

        return bits;
    }

    // ==================== 纠错与交织 ====================

    /// <summary>数据码字 → 分块算 RS 纠错 → 按标准交织成一整串码字</summary>
    private static byte[] AddErrorCorrection(List<bool> bits, int version)
    {
        int dataCw = DataCodewords[version - 1];
        var data = new byte[dataCw];
        for (int i = 0; i < dataCw; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
                if (bits[i * 8 + j]) b |= 1 << (7 - j);
            data[i] = (byte)b;
        }

        var gen = RsGenerator(EcCodewordsPerBlock[version - 1]);
        var blocks = new List<byte[]>();
        var eccs = new List<byte[]>();

        int pos = 0;
        foreach (var (count, size) in new[] { Group1[version - 1], Group2[version - 1] })
        {
            for (int i = 0; i < count; i++)
            {
                var block = new byte[size];
                Array.Copy(data, pos, block, 0, size);
                pos += size;
                blocks.Add(block);
                eccs.Add(RsRemainder(block, gen));
            }
        }

        // 交织: 先逐"轮"取各块的第 i 个数据码字(短块在越界那一轮缺席), 再逐轮取纠错码字
        var result = new List<byte>(dataCw + EcCodewordsPerBlock[version - 1] * blocks.Count);
        int maxData = blocks.Max(b => b.Length);
        for (int i = 0; i < maxData; i++)
            foreach (var b in blocks)
                if (i < b.Length) result.Add(b[i]);

        int ecLen = EcCodewordsPerBlock[version - 1];
        for (int i = 0; i < ecLen; i++)
            foreach (var e in eccs) result.Add(e[i]);

        return result.ToArray();
    }

    // ---- GF(256): 本原多项式 x^8+x^4+x^3+x^2+1 (0x11D) ----

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCode()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        // 指数表翻倍存一份, 这样乘法里 log 相加(最大 254+254)不用再取模
        for (int i = 255; i < 512; i++) GfExp[i] = GfExp[i - 255];
    }

    private static byte GfMul(byte a, byte b)
        => (a == 0 || b == 0) ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    /// <summary>
    /// RS 生成多项式 g(x) = (x-α^0)(x-α^1)...(x-α^(n-1))。
    /// 返回 n+1 个系数(按 x 的降幂), 首项恒为 1。
    /// </summary>
    private static byte[] RsGenerator(int degree)
    {
        var poly = new byte[] { 1 };
        for (int i = 0; i < degree; i++)
        {
            var next = new byte[poly.Length + 1];
            for (int j = 0; j < poly.Length; j++)
            {
                next[j] ^= poly[j];                          // 乘 x: 系数整体后移一位
                next[j + 1] ^= GfMul(poly[j], GfExp[i]);     // 乘 α^i
            }
            poly = next;
        }
        return poly;
    }

    /// <summary>多项式除法取余: data·x^n mod g(x), 就是这一块的纠错码字</summary>
    private static byte[] RsRemainder(byte[] block, byte[] gen)
    {
        int degree = gen.Length - 1;
        var rem = new byte[degree];

        foreach (var b in block)
        {
            byte factor = (byte)(b ^ rem[0]);            // 当前最高位与寄存器首位的差
            Array.Copy(rem, 1, rem, 0, degree - 1);
            rem[degree - 1] = 0;
            for (int i = 0; i < degree; i++)
                rem[i] ^= GfMul(gen[i + 1], factor);     // gen[0] 恒为 1, 不用参与
        }
        return rem;
    }

    // ==================== 矩阵构建 ====================

    private static bool[,] BuildMatrix(byte[] codewords, int version)
    {
        int size = 17 + version * 4;
        var isFunction = new bool[size, size];    // 功能图案/预留区, 不参与数据填充
        var matrix = new bool[size, size];

        void SetFunction(int row, int col, bool dark)
        {
            matrix[row, col] = dark;
            isFunction[row, col] = true;
        }

        // ---- 定位图案 + 分隔带(分隔带是"围一圈浅色", 一并标记为功能位, 否则会被填进数据) ----
        void Finder(int top, int left)
        {
            for (int r = -1; r <= 7; r++)
            {
                for (int c = -1; c <= 7; c++)
                {
                    int rr = top + r, cc = left + c;
                    if (rr < 0 || rr >= size || cc < 0 || cc >= size) continue;
                    bool dark =
                        (r >= 0 && r <= 6 && (c == 0 || c == 6)) ||
                        (c >= 0 && c <= 6 && (r == 0 || r == 6)) ||
                        (r >= 2 && r <= 4 && c >= 2 && c <= 4);
                    SetFunction(rr, cc, dark);
                }
            }
        }
        Finder(0, 0);
        Finder(0, size - 7);
        Finder(size - 7, 0);

        // ---- 定时图案: 第 6 行/第 6 列黑白相间 ----
        for (int i = 8; i < size - 8; i++)
        {
            bool dark = i % 2 == 0;
            SetFunction(6, i, dark);
            SetFunction(i, 6, dark);
        }

        // ---- 对齐图案: 中心点在标准表里, 跳过与三个定位图案重叠的三处 ----
        var centers = AlignmentCenters[version - 1];
        foreach (int cr in centers)
        {
            foreach (int cc in centers)
            {
                if ((cr == 6 && cc == 6) ||
                    (cr == 6 && cc == size - 7) ||
                    (cr == size - 7 && cc == 6)) continue;

                for (int dr = -2; dr <= 2; dr++)
                {
                    for (int dc = -2; dc <= 2; dc++)
                    {
                        // 5x5 对齐图案: 外框深色 + 中间一圈浅色 + 正中心深色
                        bool dark = Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1;
                        SetFunction(cr + dr, cc + dc, dark);
                    }
                }
            }
        }

        // ---- 预留格式信息区(真正的值随掩码不同, 在下面逐个候选里写) ----
        for (int i = 0; i <= 8; i++)
        {
            isFunction[8, i] = true;
            isFunction[i, 8] = true;
        }
        for (int i = 0; i < 8; i++)
        {
            isFunction[8, size - 1 - i] = true;
            isFunction[size - 1 - i, 8] = true;
        }

        // ---- 版本信息(v7 起才有): 两个 3×6 的区块, 与掩码无关 ----
        if (version >= 7)
        {
            int vbits = VersionInfoBits(version);
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((vbits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                SetFunction(b, a, bit);     // 右上
                SetFunction(a, b, bit);     // 左下
            }
        }

        // ---- 数据位: 从右下角起, 每次两列一"对", 蛇形上下折返, 跳过功能位 ----
        int bitIndex = 0;
        bool NextBit()
        {
            if (bitIndex >= codewords.Length * 8)
            {
                bitIndex++;          // 剩余位(v2..v6 有 7 个): 只占位, 值恒为 0
                return false;
            }
            int idx = bitIndex++;
            return ((codewords[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
        }

        bool upward = true;
        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;      // 让开竖直定时图案那一列(它不参与数据填充)
            for (int i = 0; i < size; i++)
            {
                int row = upward ? size - 1 - i : i;
                for (int k = 0; k < 2; k++)
                {
                    int c = col - k;
                    if (isFunction[row, c]) continue;
                    matrix[row, c] = NextBit();
                }
            }
            upward = !upward;
        }

        // ---- 8 种掩码各试一遍, 取惩罚分最低的那个 ----
        bool[,] best = null!;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var candidate = (bool[,])matrix.Clone();
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!isFunction[r, c] && MaskBit(mask, r, c))
                        candidate[r, c] = !candidate[r, c];

            PlaceFormatBits(candidate, size, mask);
            int penalty = Penalty(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>掩码条件: true 表示该位置取反</summary>
    private static bool MaskBit(int mask, int r, int c) => mask switch
    {
        0 => (r + c) % 2 == 0,
        1 => r % 2 == 0,
        2 => c % 3 == 0,
        3 => (r + c) % 3 == 0,
        4 => (r / 2 + c / 3) % 2 == 0,
        5 => (r * c) % 2 + (r * c) % 3 == 0,
        6 => ((r * c) % 2 + (r * c) % 3) % 2 == 0,
        _ => ((r + c) % 2 + (r * c) % 3) % 2 == 0,
    };

    /// <summary>格式信息: 5 位原始数据(纠错等级 + 掩码) + BCH(15,5) 校验, 再异或固定掩码 0x5412</summary>
    private static void PlaceFormatBits(bool[,] m, int size, int mask)
    {
        int data = (EccBitsM << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);   // BCH 生成多项式 0x537
        int bits = ((data << 10) | rem) ^ 0x5412;

        bool Bit(int i) => ((bits >> i) & 1) != 0;

        // 第一份: 左上定位图案旁边(注意这里是 (列, 行) 的书写顺序)
        for (int i = 0; i <= 5; i++) m[i, 8] = Bit(i);
        m[7, 8] = Bit(6);
        m[8, 8] = Bit(7);
        m[8, 7] = Bit(8);
        for (int i = 9; i < 15; i++) m[8, 14 - i] = Bit(i);

        // 第二份: 右上 / 左下各半
        for (int i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(i);
        for (int i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(i);
        m[size - 8, 8] = true;   // 固定深色模块
    }

    /// <summary>版本信息: 6 位版本号 + BCH(18,6) 校验(生成多项式 0x1F25)</summary>
    private static int VersionInfoBits(int version)
    {
        int rem = version;
        for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
        return (version << 12) | rem;
    }

    // ==================== 掩码惩罚评分(标准四条规则) ====================

    private static int Penalty(bool[,] m, int size)
    {
        int score = 0;

        // 规则 1: 行/列上连续 5 个及以上同色 → 3 + (长度 - 5)
        for (int i = 0; i < size; i++)
        {
            bool rowColor = m[i, 0], colColor = m[0, i];
            int rowRun = 1, colRun = 1;
            for (int j = 1; j < size; j++)
            {
                if (m[i, j] == rowColor)
                {
                    rowRun++;
                    if (rowRun == 5) score += 3;
                    else if (rowRun > 5) score++;
                }
                else { rowColor = m[i, j]; rowRun = 1; }

                if (m[j, i] == colColor)
                {
                    colRun++;
                    if (colRun == 5) score += 3;
                    else if (colRun > 5) score++;
                }
                else { colColor = m[j, i]; colRun = 1; }
            }
        }

        // 规则 2: 2×2 同色块 → 每处 3
        for (int r = 0; r < size - 1; r++)
        {
            for (int c = 0; c < size - 1; c++)
            {
                bool v = m[r, c];
                if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1]) score += 3;
            }
        }

        // 规则 3: 出现"像定位图案"的 1:1:3:1:1 且一侧有 4 个浅色 → 每处 40
        // 标准给的两种形状: 深-浅-深深深-浅-深 + 4 浅, 以及它的镜像。
        // 只用这 11 格窗口判定(与多数实现一致); 掩码选择本来就是"哪个分数低选哪个",
        // 分数细则略有出入不影响可扫性 —— 选中的掩码会如实写进格式信息。
        for (int a = 0; a < size; a++)
        {
            for (int b = 0; b + 10 < size; b++)
            {
                score += WindowPenalty(m, a, b, dr: 0, dc: 1);   // 第 a 行, 从第 b 列起的横向窗口
                score += WindowPenalty(m, b, a, dr: 1, dc: 0);   // 第 a 列, 从第 b 行起的纵向窗口
            }
        }

        // 规则 4: 深色占比每偏离 50% 达 5%(向下取整) → 10
        int dark = 0;
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++)
                if (m[r, c]) dark++;
        int total = size * size;
        score += Math.Abs(dark * 20 - total * 10) / total * 10;

        return score;
    }

    /// <summary>规则 3 的两种形状: 深 浅 深深深 浅 深, 前后各带 4 个浅色</summary>
    private static readonly int[] FinderLikeA = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
    private static readonly int[] FinderLikeB = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };

    /// <summary>检查从 (row, col) 起、每步走 (dr, dc) 的 11 格窗口是否命中规则 3 的两种形状</summary>
    private static int WindowPenalty(bool[,] m, int row, int col, int dr, int dc)
    {
        bool matchA = true, matchB = true;
        for (int i = 0; i < 11; i++)
        {
            int v = m[row + dr * i, col + dc * i] ? 1 : 0;
            if (v != FinderLikeA[i]) matchA = false;
            if (v != FinderLikeB[i]) matchB = false;
        }
        return (matchA ? 40 : 0) + (matchB ? 40 : 0);
    }
}
