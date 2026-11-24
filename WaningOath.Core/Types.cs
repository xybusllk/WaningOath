namespace WaningOath.Core;

/// <summary>
/// 攻击属性。依据 01 §2.1：六种基础属性 + 两种特殊属性。
/// </summary>
/// <remarks>
/// 特殊属性的口径（已定 · 辛1）：
///   <see cref="Liye"/>  = 冰的变体：独立进度条、共用冰异常、可与冰互相触发紊乱、受冰抗性、吃冰增益。
///   <see cref="Aria"/>  = 共振的变体：与 Liye 同构，共用共振异常。
/// <para>
/// ⚠️ 代码名 <c>aria</c>（咏叹）是本次取的，文档里尚未登记 —— 05 §3 的 element 注释当前只列到 liye。
/// 若要改成拼音 <c>yongtan</c> 以与 <c>liye</c> 对齐，只需改这里 + 两处 JSON 字符串。
/// </para>
/// </remarks>
public enum Element
{
    Physical = 0,
    Ice = 1,
    Fire = 2,
    Lightning = 3,
    Wind = 4,
    Resonance = 5,

    /// <summary>离谒 —— 冰的变体。01 §2.1。</summary>
    Liye = 6,

    /// <summary>咏叹 —— 共振的变体。01 §2.1。</summary>
    Aria = 7,
}

/// <summary>星级。5★ 三件武器部件 / 技能等级上限 14；4★ 两件 / 12；3★ 一件 / —。依据 101 §0.1、01 §5.2。</summary>
public enum Rarity
{
    Three = 3,
    Four = 4,
    Five = 5,
}

/// <summary>武器类别。依据 02 §5.1（四种）。</summary>
public enum WeaponType
{
    /// <summary>单刀</summary>
    Dao = 0,
    /// <summary>长剑</summary>
    Sword = 1,
    /// <summary>施术仪</summary>
    Focus = 2,
    /// <summary>配枪</summary>
    Gun = 3,
}

/// <summary>定位。依据 05 §3 的 path 枚举。</summary>
public enum RolePath
{
    Dps = 0,
    SubDps = 1,
    Support = 2,
    Survival = 3,
    Healer = 4,
}

/// <summary>
/// 单位态度。依据 01 §1.6（三档）。
/// ⚠️ 与"阵营"是两条独立的轴：阵营决定"阵营判定"类技能，态度决定"对谁生效"类技能。
/// </summary>
public enum Disposition
{
    Hostile = 0,
    Neutral = 1,
    Friendly = 2,
}

/// <summary>
/// 代码里统一使用的"待办"标记。跑一次 <c>grep TODO(M1)</c> 就能列出当前里程碑还要做什么。
/// </summary>
internal static class Todo
{
    // 这里刻意留空：标记写在各个实现点上，例如
    //     // TODO(M1): 行动条 —— 01 §1.2
}
