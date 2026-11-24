namespace WaningOath.Core.Battle;

/// <summary>
/// 作用对象口径。**字符串常量集中在这里**，避免同一句话在十来个地方各写一遍字面量。
/// </summary>
/// <remarks>
/// 依据 `01 §6.3`（「我方角色」vs「我方单位」）与 `01 §1.6`（单位态度）：
/// <list type="bullet">
///   <item>伤害与减益**默认只作用于「敌对」**；治疗与增益**默认只作用于「可控友好」**。</item>
///   <item>未加限定时，「我方全体」**含召唤物**。</item>
/// </list>
/// </remarks>
public static class TargetSpec
{
    // ── 自己 / 队友 ──
    public const string Self = "self";

    /// <summary>我方全体 = 4 名角色 + 全部召唤物（`01 §6.3`）。</summary>
    public const string PartyAll = "partyAll";

    /// <summary>我方**角色**——**不含召唤物**（`01 §6.3`）。用于"我方 4 人全部无法行动"这类判定。</summary>
    public const string PartyCharacters = "partyCharacters";

    /// <summary>我方一号位（最左）。部分技能按阵营判定（星遥元素共鸣，`07 §2.2`）。</summary>
    public const string PartySlot1 = "partySlot1";

    public const string PartyLowestHp = "partyLowestHp";

    // ── 敌方 ──
    public const string MainTarget = "mainTarget";
    public const string AllEnemies = "allEnemies";
    public const string RandomEnemy = "randomEnemy";
    public const string AdjacentEnemies = "adjacentEnemies";

    /// <summary>本场战斗中**最近一次被任意我方目标造成伤害的敌方单位**（`01 §6.4`）。**不是**站位最近。</summary>
    public const string MostRecentlyDamagedEnemy = "mostRecentlyDamagedEnemy";

    /// <summary>**本次协同攻击选中的我方角色**（`01 §5.5`）。供共生灵天赋给它上增益。</summary>
    public const string CoopTarget = "coopTarget";

    // ── 召唤物 ──
    public const string OwnSummons = "ownSummons";
    public const string AllSummons = "allSummons";

    /// <summary>解析：把 `null` / 空串按"攻击类默认敌对、增益类默认友好"的规则补全（`01 §1.6`）。</summary>
    public static bool TryResolve(string? spec, bool isBeneficial, out string resolved)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            resolved = isBeneficial ? PartyAll : MainTarget;
            return true;
        }
        resolved = spec.Trim();
        return true;
    }
}
