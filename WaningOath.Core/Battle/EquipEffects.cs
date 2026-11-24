using WaningOath.Core.Data;
using WaningOath.Core.Progression;

namespace WaningOath.Core.Battle;

/// <summary>
/// **把配装里"不进面板"的那一半接进战斗**（`M7`）。
/// </summary>
/// <remarks>
/// `PanelCalculator` 只折"面板类"属性（攻 / 生 / 防 / 速 / 双暴 / 充能 …）。
/// 剩下的三类东西进不了面板，但必须在战斗里生效：
/// <list type="number">
///   <item><b>武器五段特效</b> —— 有触发时机（释放终结技后 / 追加攻击后 / 施加异常时）与时长。→ 走 <see cref="TriggerDef"/>。</item>
///   <item><b>武器的常驻特效</b>（`effect.constant`）—— 无时机，本场一直生效。→ 走一个永久状态。</item>
///   <item><b>装备套装的件数效果</b>（2 件 / 4 件）—— 同样是"挂上去就一直在"。→ 走 <c>EffectResolver</c> 的 <c>modifyStat</c>。</item>
/// </list>
/// <para>
/// ⚠️ <b>无法表达的键会被报出来</b>（`state.Note`），而不是安静地丢掉。
/// 例：`extraCombustChance` 是"爆燃判定里读的一个旋钮"，它既不是面板词条、
/// 也不是按属性记的增伤 —— 那类键需要各自的实现点，归在"接口层已就位、实现层待补"。
/// </para>
/// </remarks>
public static class EquipEffects
{
    /// <summary>把一个角色的配装效果接进战斗（在 <see cref="BattleFactory"/> 建单位之后立刻调用）。</summary>
    public static void Apply(BattleState state, BattleUnit unit, CharacterDef def, Loadout loadout)
    {
        ApplyWeapon(state, unit, def, loadout);
        ApplyGearSets(state, unit, def, loadout);
    }

    private static void ApplyWeapon(BattleState state, BattleUnit unit, CharacterDef def, Loadout loadout)
    {
        if (loadout.WeaponId is null) return;
        WeaponDef? w = state.Data.Weapon(loadout.WeaponId);
        if (w?.Effect is not { } eff) return;

        // **类型契合**（`02 §5.3`）：特效仅在"角色适用武器类型 == 武器类型"时生效。
        if (!PanelCalculator.TypeMatches(def, w))
        {
            state.Note($"武器「{w.Name}」的特效未生效：类型不契合（角色 {def.WeaponType} ≠ 武器 {w.WeaponType}）。");
            return;
        }

        // ── ① 常驻部分：一个永久状态 ──
        if (eff.Constant.Count > 0)
        {
            string cid = $"{StatusFactory.Prefix}{w.Id}:constant";
            StatusDef cdef = StatusFactory.Build(state.Data, cid, $"{w.Name}·常驻",
                [.. eff.Constant.Select(static kv => (kv.Key, kv.Value))], duration: 0.0, appliesTo: null,
                out List<string> missed);
            if (missed.Count == 0)
            {
                StatusFactory.Attach(unit, cdef);
            }
            else
            {
                state.Note($"武器「{w.Name}」的常驻特效里有引擎不认的键：{string.Join(" / ", missed)} —— 未生效。");
            }
        }

        // ── ② 五段特效：一条或多条触发器 + 一个带时长的状态 ──
        List<WeaponTriggerDef> triggers = eff.Triggers.Count > 0
            ? eff.Triggers
            : eff.Trigger is null ? [] : [eff.Trigger];
        if (triggers.Count == 0 || triggers.All(static t => string.IsNullOrEmpty(t.Hook)))
        {
            if (eff.Tiers.Count > 0)
            {
                state.Note($"武器「{w.Name}」有五段特效，但 `effect.trigger` / `triggers` 没写（不知道什么时候生效）—— 本次未接入。");
            }
            return;
        }

        int rank = Math.Clamp(loadout.WeaponOverwrite, 1, Math.Max(1, eff.Tiers.Count));
        WeaponTierDef? tier = eff.Tiers.FirstOrDefault(t => t.Rank == rank);
        if (tier is null) return;

        // 把该段的键按触发声明**分组**：先满足有 `keys` 限定的，剩下的给第一条不限定的。
        Dictionary<WeaponTriggerDef, List<(string Key, double Value)>> grouped = new();
        foreach (WeaponTriggerDef t in triggers) grouped[t] = [];
        WeaponTriggerDef? fallback = triggers.FirstOrDefault(static t => t.Keys.Count == 0);

        foreach (KeyValuePair<string, System.Text.Json.JsonElement> kv in tier.Values)
        {
            if (kv.Value.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            double value = kv.Value.GetDouble();
            WeaponTriggerDef? ownerTrigger = triggers.FirstOrDefault(t => t.Keys.Contains(kv.Key, StringComparer.Ordinal))
                                            ?? fallback;
            if (ownerTrigger is null)
            {
                state.Note($"武器「{w.Name}」{rank} 阶的键 '{kv.Key}' 没有任何触发声明认领 —— 本次未生效。");
                continue;
            }
            grouped[ownerTrigger].Add((kv.Key, value));
        }

        foreach ((WeaponTriggerDef trig, List<(string Key, double Value)> raw) in grouped)
        {
            if (string.IsNullOrEmpty(trig.Hook) || raw.Count == 0) continue;
            List<EffectDef> triggerEffects = [];
            List<(string Key, double Value)> pairs = [];

            foreach ((string key, double value) in raw)
            {
                // **动作键**（自己扣血 / 我方行动提前）不是一个"状态"，而是一件**当场做的事**，
                // 所以它们不塞进状态袋，而是直接翻成对应的原子。
                if (StatKeyBinder.ActionKeys.TryGetValue(key, out (string Atom, string Field) a))
                {
                    EffectDef atom = new() { Type = a.Atom, Target = trig.Target };
                    if (string.Equals(a.Field, "amount", StringComparison.Ordinal)) atom.Amount = value;
                    else atom.Value = value;
                    triggerEffects.Add(atom);
                    continue;
                }
                pairs.Add((key, value));
            }

            if (pairs.Count > 0)
            {
                string tid = $"{StatusFactory.Prefix}{w.Id}@{rank}#{trig.Hook}";
                StatusDef tdef = StatusFactory.Build(state.Data, tid, $"{w.Name}@{rank}阶",
                    pairs, trig.Duration,
                    string.Equals(trig.Target, "partyAll", StringComparison.Ordinal) ? "partyAll" : null,
                    out List<string> unmapped);

                if (unmapped.Count > 0)
                {
                    // 有一部分键引擎不认（还没登记的机制旋钮）。**认出来的那部分照样生效** ——
                    // 一条"额外施加可燃的概率"不该把同段的"爆燃伤害 +8%"也一起拖下水。
                    state.Note($"武器「{w.Name}」{rank} 阶特效里有引擎不认的键（属未登记的机制旋钮）：" +
                               string.Join(" / ", unmapped));
                }

                if (tdef.Mods.Count > 0 || tdef.ElemMods.Count > 0 || tdef.Knobs.Count > 0)
                {
                    triggerEffects.Add(new EffectDef
                    {
                        Type = "applyBuff",
                        BuffId = tid,
                        Stacks = 1,
                        Duration = trig.Duration,
                        Target = trig.Target,
                    });
                }
            }

            if (triggerEffects.Count == 0) continue;

            unit.Triggers.Add(new TriggerDef
            {
                Hook = trig.Hook,
                Scope = "self",
                Condition = trig.Condition,
                Effects = triggerEffects,
            });
        }
    }

    private static void ApplyGearSets(BattleState state, BattleUnit unit, CharacterDef def, Loadout loadout)
    {
        if (loadout.Pieces.Count == 0) return;

        // 件数统计：键 = 套装 id，值 = 穿了几件。
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (GearPiece p in loadout.Pieces)
        {
            if (string.IsNullOrEmpty(p.SetId)) continue;
            counts[p.SetId] = counts.GetValueOrDefault(p.SetId) + 1;
        }

        foreach (KeyValuePair<string, int> kv in counts.OrderBy(static k => k.Key, StringComparer.Ordinal))
        {
            GearSetDef? set = state.Data.GearSet(kv.Key);
            if (set is null) continue;

            foreach (GearSetTierDef tier in set.Tiers.OrderBy(static t => t.Pieces))
            {
                if (kv.Value < tier.Pieces) continue;
                if (tier.Effects.Count == 0) continue;

                // 分两路：
                //   * **带 `triggerHook` 的** → 挂成触发器（"释放追加攻击后"这类）。
                //   * **其余** → 立刻结算（"两件套：风属性伤害 +10%" 这类常驻）。
                // 混在一起处理过一次，症状是"释放追加攻击后"的增益**开局就挂上了、还从第 0 回合开始倒计时**。
                List<EffectDef> immediate = [];
                foreach (EffectDef e in tier.Effects)
                {
                    if (string.IsNullOrEmpty(e.TriggerHook)) { immediate.Add(e); continue; }
                    unit.Triggers.Add(new TriggerDef
                    {
                        Hook = e.TriggerHook,
                        Scope = "self",
                        Effects = [e],
                    });
                }

                if (immediate.Count == 0) continue;

                // 套装效果走**普通的原子执行路径** —— 它们就是一组 `modifyStat`。
                // 因此 `01 §1.3` 的时长 / 叠层 / 计时源规则对它们全部适用，不需要另开一套。
                EffectContext ctx = new() { Actor = unit, Target = unit };
                EffectResolver.Run(state, immediate, ctx, depth: 0);
            }
        }
    }
}
