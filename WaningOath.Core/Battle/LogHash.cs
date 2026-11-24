using System.Text;

namespace WaningOath.Core.Battle;

/// <summary>
/// 战斗日志的指纹（FNV-1a 64）。
/// </summary>
/// <remarks>
/// <b>它存在的唯一理由是"让客户端与服务端能就一整场战斗达成一致"</b>：
/// 客户端跑完之后把这个指纹连同动作序列一起提交，服务端重放一遍再算一次 —— 对不上就是有人在说谎，
/// 或者两边的内核版本不一致。比对一整个列表比逐行 diff 便宜，且足够定位问题
/// （对不上就退回逐行比对，那一步只有在真的出错时才做）。
/// <para>
/// ⚠️ 放在 **Core** 而不是服务端：如果两边各写一份，两边的实现只要差一个分隔符，
/// 校验就会永远失败 —— 而"永远失败"和"永远成功"一样没用。
/// </para>
/// </remarks>
public static class LogHash
{
    public static string Fnv1a64(IEnumerable<string> lines)
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        ulong h = Offset;
        foreach (string line in lines)
        {
            // 逐字符（UTF-16 码元）—— 与平台无关，且不需要先编码成字节数组。
            foreach (char c in line)
            {
                h ^= c;
                h *= Prime;
            }
            h ^= '\n';
            h *= Prime;
        }
        return "fnv1a64:" + h.ToString("x16");
    }

    /// <summary>给日志行加上序号再算指纹 —— 仅用于调试时把"哪一行不同"暴露成"指纹不同"。</summary>
    public static string Fnv1a64Numbered(IEnumerable<string> lines)
    {
        StringBuilder sb = new();
        int i = 0;
        foreach (string line in lines) sb.Append(i++).Append(':').Append(line).Append('\n');
        return Fnv1a64([sb.ToString()]);
    }
}
