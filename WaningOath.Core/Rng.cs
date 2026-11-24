namespace WaningOath.Core;

/// <summary>
/// 可复现随机数发生器（SplitMix64）。
/// </summary>
/// <remarks>
/// <b>为什么自己写而不用 <see cref="System.Random"/></b>（06 §8.1 的 Core 确定性纪律，硬约束）：
/// <list type="number">
///   <item>禁止调用 <c>Random</c> / 时间 / 系统熵 —— 否则"同一种子 + 同一动作序列 → 重放结果一致"无法成立。</item>
///   <item>算法必须由我们锁定：BCL 换一个大版本就可能换掉 <c>System.Random</c> 的内部算法，
///         而存档里的战斗重放（06 §2.3）要跨越客户端与服务端、跨越时间。</item>
/// </list>
/// <para>
/// <b>用法纪律</b>：
/// <list type="bullet">
///   <item>一场战斗 <b>只创建一个</b> Rng，种子来自服务器；所有随机都从这里取。</item>
///   <item><see cref="State"/> 可读可写，用于存档快照与中途重放定位。</item>
///   <item>需要"互不干扰的随机流"时用 <see cref="Fork"/>，<b>不要</b>再 new 一个 <see cref="Rng"/> 自己编种子。</item>
/// </list>
/// </para>
/// <para>
/// SplitMix64 的常数与位移取自 Steele / Lea / Flood 的公开算法，全部为编译期常量，
/// 因此每次构建的随机序列是逐位一致的。
/// </para>
/// </remarks>
public sealed class Rng
{
    private const ulong Gamma = 0x9E3779B97F4A7C15UL;
    private const ulong Mix1 = 0xBF58476D1CE4E5B9UL;
    private const ulong Mix2 = 0x94D049BB133111EBUL;

    private ulong _state;

    /// <summary>用种子创建。<paramref name="seed"/> 为 0 时替换为非零常数（SplitMix64 在 0 处退化）。</summary>
    public Rng(ulong seed)
    {
        _state = seed == 0UL ? Gamma : seed;
    }

    /// <summary>
    /// 当前内部状态。**可读可写** —— 存档重放需要它（06 §2.3）。
    /// </summary>
    public ulong State
    {
        get => _state;
        set => _state = value == 0UL ? Gamma : value;
    }

    /// <summary>取一个 64 位无符号随机数。</summary>
    public ulong NextU64()
    {
        _state += Gamma;
        ulong z = _state;
        z = (z ^ (z >> 30)) * Mix1;
        z = (z ^ (z >> 27)) * Mix2;
        return z ^ (z >> 31);
    }

    /// <summary>取 <c>[0, maxExclusive)</c> 的整数。</summary>
    /// <remarks>
    /// 用取模实现。对 2^64 的模偏差在 <c>maxExclusive</c> 远小于 2^32 时可忽略
    /// （本作的取值范围是敌人数量、弹射目标、层数这类小整数）。
    /// 结论：**确定性优先**，不为了消除理论偏差引入拒绝采样带来的可变迭代次数。
    /// </remarks>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextU64() % (ulong)maxExclusive);
    }

    /// <summary>取 <c>[minInclusive, maxExclusive)</c> 的整数。</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(minInclusive, maxExclusive);
        return minInclusive + NextInt(maxExclusive - minInclusive);
    }

    /// <summary>取 <c>[0, 1)</c> 的浮点数（53 位有效，与 double 的尾数等宽）。</summary>
    public double NextDouble()
    {
        // 右移 11 位得到 53 位，再除以 2^53 —— 结果严格落在 [0, 1)。
        return (NextU64() >> 11) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// 基础概率判定：以概率 <paramref name="chance"/> 返回 true。
    /// </summary>
    /// <remarks>
    /// <paramref name="chance"/> ∈ [0,1]。边界写死：<c>0</c> 恒 false、<c>1</c> 恒 true
    /// （不能依赖浮点比较的巧合 —— 01 §2.3 的效果命中/抵抗、各异常的"100% 基础概率"都走这里）。
    /// </remarks>
    public bool Chance(double chance)
    {
        if (chance <= 0.0) return false;
        if (chance >= 1.0) return true;
        return NextDouble() < chance;
    }

    /// <summary>从列表中等概率取一个元素。</summary>
    public T Pick<T>(IReadOnlyList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("不能从空列表中取元素。", nameof(items));
        return items[NextInt(items.Count)];
    }

    /// <summary>原地洗牌（Fisher–Yates）。</summary>
    public void Shuffle<T>(IList<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// 派生一条**互不干扰**的随机流（例：每个战斗阶段一条）。
    /// </summary>
    /// <remarks>
    /// 用途：避免"多加一次判定就整体错位"。若所有随机共用一个流，
    /// 在中间插入一次 <see cref="Chance"/> 会让后续所有结果全体平移，
    /// 使重放对比无法定位到真正的分歧点。<paramref name="salt"/> 用固定的语义编号。
    /// </remarks>
    public Rng Fork(ulong salt) => new(NextU64() ^ (salt * Gamma));
}
