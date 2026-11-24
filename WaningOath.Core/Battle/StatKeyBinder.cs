using WaningOath.Core.Data;

namespace WaningOath.Core.Battle;

/// <summary>
/// 把数据里的**属性键**翻译成引擎认识的 <see cref="StatMod"/> / <see cref="ElemMod"/>。
/// </summary>
/// <remarks>
/// <b>为什么需要这一层</b>：装备 / 武器 / 套装里写的键是**给人看的中文语义名的英文缩写**
/// （`atkPct` / `critRateBonus` / `windDmgBonus` …），而引擎只认
/// <see cref="StatKind"/> 与 <see cref="ElemStat"/>。中间缺一层翻译，症状就是
/// **"写得进数据、算不出来、还不报错"** —— 本项目最贵的一类 bug。
/// <para>
/// 因此这里的映射表是**唯一**的真相：`PanelCalculator`（面板）与
/// <see cref="EffectResolver"/> 的 `modifyStat`（战斗内）都走它，
/// 不在两处各写一份。
/// </para>
/// </remarks>
public static class StatKeyBinder
{
    /// <summary>属性名 → 元素（用于 `xxxDmgBonus` 这一类按属性记的词条）。</summary>
    private static readonly Dictionary<string, Element> ElementPrefixes = new(StringComparer.Ordinal)
    {
        ["physical"] = Element.Physical,
        ["ice"] = Element.Ice,
        ["fire"] = Element.Fire,
        ["lightning"] = Element.Lightning,
        ["wind"] = Element.Wind,
        ["resonance"] = Element.Resonance,
        ["liye"] = Element.Liye,
        ["aria"] = Element.Aria,
    };

    /// <summary>翻译结果：标量修正 + 按属性修正 + **机制旋钮** + **没认出来的那些键**。</summary>
    public sealed record Bound(List<StatMod> Mods, List<ElemMod> Elems, Dictionary<string, double> Knobs, List<string> Unmapped);

    /// <summary>
    /// **机制旋钮**：不是"面板上的一个数"，而是"引擎某处会去读的一个开关量"。
    /// </summary>
    /// <remarks>
    /// 例：`extraCombustChance` 不改变任何面板，它改的是<b>爆燃判定里的那条概率曲线</b>。
    /// 把它们统一收进一张"旋钮袋"，好处是**消费点只需要 `KnobValue("键")` 一句话**，
    /// 而不是每加一个机制就在装备代码里开一个分支。
    /// <para>
    /// ⚠️ 名单外的键仍然报"不认识" —— 旋钮袋不改变"拼错的名字必须被看见"这条纪律。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> KnownKnobs = new(StringComparer.Ordinal)
    {
        "extraCombustChance",
        "detonateDamageBonus",
        "counterDamageBonus",
    };

    /// <summary>
    /// **机制动作键**：它们不是数值，而是"做一件事"（`05 §3.2` 的原子）。
    /// </summary>
    /// <remarks>
    /// 装在武器的 `tiers` 里写的是 `(键, 值)`，但有两把武器的特效本质上是**动作**而不是状态：
    /// 「璀璨不再的启明星」释放时扣自身 10% 生命、「明镜鉴心」让我方全体行动提前。
    /// 把它们留在一个"状态"里没有意义（扣血是瞬时的），所以这里做一次显式映射：
    /// **键 → 原子名 + 字段**。
    /// </remarks>
    public static readonly Dictionary<string, (string Atom, string Field)> ActionKeys = new(StringComparer.Ordinal)
    {
        ["selfHpCostPct"] = ("selfHpCost", "value"),
        ["partyAdvanceActionPct"] = ("advanceAction", "value"),
        ["energyOnHit"] = ("gainEnergy", "amount"),
    };

    /// <summary>
    /// 翻译一条 `(键, 值)`。
    /// </summary>
    /// <param name="source">来源描述（写进 <see cref="StatMod.Source"/>，便于日志与自测）。</param>
    public static Bound Bind(string key, double value, string source)
    {
        List<StatMod> mods = [];
        List<ElemMod> elems = [];
        Dictionary<string, double> knobs = new(StringComparer.Ordinal);
        List<string> unmapped = [];

        string k = key.Trim();
        switch (k)
        {
            // ── 标量：百分比 ──
            case "atkPct": mods.Add(new StatMod(StatKind.Atk, ModOp.Pct, value, source)); break;
            case "hpPct": case "hpMaxBonus": mods.Add(new StatMod(StatKind.Hp, ModOp.Pct, value, source)); break;
            case "defPct": mods.Add(new StatMod(StatKind.Def, ModOp.Pct, value, source)); break;
            case "critRate": case "critRateBonus": mods.Add(new StatMod(StatKind.CritRate, ModOp.Pct, value, source)); break;
            case "critDmg": case "critDmgBonus": mods.Add(new StatMod(StatKind.CritDmg, ModOp.Pct, value, source)); break;
            case "energyRegen": case "energyRegenBonus": case "selfEnergyRegenBonus":
                mods.Add(new StatMod(StatKind.EnergyRegen, ModOp.Pct, value, source)); break;
            case "healBonus": mods.Add(new StatMod(StatKind.HealBonus, ModOp.Pct, value, source)); break;
            case "shieldBonus": mods.Add(new StatMod(StatKind.ShieldBonus, ModOp.Pct, value, source)); break;
            case "effectHit": mods.Add(new StatMod(StatKind.EffectHit, ModOp.Pct, value, source)); break;
            case "effectRes": mods.Add(new StatMod(StatKind.EffectRes, ModOp.Pct, value, source)); break;
            case "anomalyBuildUp": case "anomalyBuildUpBonus":
                mods.Add(new StatMod(StatKind.AnomalyBuildUp, ModOp.Pct, value, source)); break;
            case "damageReduction": mods.Add(new StatMod(StatKind.DamageReduction, ModOp.Add, value, source)); break;

            // ── 标量：固定值 ──
            case "atk": case "atkFlat": mods.Add(new StatMod(StatKind.Atk, ModOp.Add, value, source)); break;
            case "hp": case "hpFlat": case "selfHpFlatBonus": mods.Add(new StatMod(StatKind.Hp, ModOp.Add, value, source)); break;
            case "def": case "defFlat": mods.Add(new StatMod(StatKind.Def, ModOp.Add, value, source)); break;
            case "speed": mods.Add(new StatMod(StatKind.Speed, ModOp.Add, value, source)); break;

            // ── 按属性记的词条 ──
            case "dmgAmp":
                // 「伤害加深」是唯一一条同时进增伤区与穿透区的词条 —— 数据里给了它几条就铺几条。
                foreach (Element e in ElementInfo.All)
                {
                    elems.Add(new ElemMod(ElemStat.DmgAmp, e, ModOp.Add, value, source));
                }
                break;

            default:
            {
                if (KnownKnobs.Contains(k)) { knobs[k] = value; break; }

                // `xxxDmgBonus` / `partyXxxDmgBonus` → 该属性的增伤。
                if (TrySplitElement(k, out Element element, out bool isParty))
                {
                    elems.Add(new ElemMod(ElemStat.DmgBonus, element, ModOp.Add, value,
                        source + (isParty ? "（我方全体）" : "")));
                    break;
                }
                unmapped.Add(k);
                break;
            }
        }

        return new Bound(mods, elems, knobs, unmapped);
    }

    /// <summary>把一条**已经认出来**的键写进一份 <see cref="StatusDef"/>。</summary>
    public static bool TryApply(StatusDef def, string key, double value, string source)
    {
        Bound b = Bind(key, value, source);
        if (b.Unmapped.Count > 0) return false;
        def.Mods.AddRange(b.Mods);
        def.ElemMods.AddRange(b.Elems);
        foreach (KeyValuePair<string, double> kv in b.Knobs) def.Knobs[kv.Key] = kv.Value;
        return true;
    }

    /// <summary>这个键引擎认不认（`false` = 拼错的名字，或者一条还没登记的机制旋钮）。</summary>
    public static bool IsKnown(string key) => Bind(key, 0.0, "probe").Unmapped.Count == 0;

    /// <summary>这个键是不是"做一件事"的动作键。</summary>
    public static bool IsActionKey(string key) => ActionKeys.ContainsKey(key.Trim());

    /// <summary>
    /// `windDmgBonus` / `dmgBonusWind` / `partyIceDmgBonus` / `symbiontWindDamageBonus` → 属性 + 是否全体。
    /// </summary>
    /// <remarks>
    /// ⚠️ **两种词序都要认**。本项目的数据里同时存在 `windDmgBonus`（`101 §4.1`）与
    /// `dmgBonusWind`（`07 §1.6` 的套装写法）—— 第一版只认了前者，
    /// 结果套装的"风属性伤害 +10%"被报成"引擎不认识"，也就是**一条真实存在的增益悄悄没生效**。
    /// </remarks>
    private static bool TrySplitElement(string key, out Element element, out bool isParty)
    {
        isParty = false;
        element = Element.Physical;

        string k = key;
        if (k.StartsWith("party", StringComparison.Ordinal)) { isParty = true; k = k[5..]; }
        if (k.StartsWith("self", StringComparison.Ordinal)) k = k[4..];
        if (k.StartsWith("symbiont", StringComparison.Ordinal)) k = k[8..];
        if (k.StartsWith("summon", StringComparison.Ordinal)) k = k[6..];

        // 词序 A：`WindDmgBonus` / `IceDamageBonus`
        foreach (KeyValuePair<string, Element> kv in ElementPrefixes)
        {
            if (!k.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = k[kv.Key.Length..];
            if (IsBonusSuffix(rest)) { element = kv.Value; return true; }
        }

        // 词序 B：`dmgBonusWind` / `damageBonusIce`
        foreach ((string prefix, _) in new[] { ("dmgBonus", 0), ("damageBonus", 0) })
        {
            if (!k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = k[prefix.Length..];
            if (ElementPrefixes.TryGetValue(rest.ToLowerInvariant(), out Element e)) { element = e; return true; }
        }

        return false;
    }

    private static bool IsBonusSuffix(string rest)
        => rest.StartsWith("DmgBonus", StringComparison.OrdinalIgnoreCase)
        || rest.StartsWith("DamageBonus", StringComparison.OrdinalIgnoreCase)
        || rest.StartsWith("Dmg", StringComparison.OrdinalIgnoreCase);
}
