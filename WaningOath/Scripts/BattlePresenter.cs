using Godot;
using WaningOath.Core;
using WaningOath.Core.Battle;
using WaningOath.Core.Data;

namespace WaningOath;

/// <summary>
/// 把一场战斗包成「一拍一步」的样子，供界面推进。
/// </summary>
/// <remarks>
/// <b>为什么不直接用 <see cref="Runner.Run"/></b>：那是"整场跑完"的接口，
/// 界面需要的是"玩家点一下、走一拍"。内核为此拆出了
/// <see cref="Runner.Begin"/> / <see cref="Runner.Step"/> / <see cref="Runner.Finish"/> 三件套，
/// 而 `Run` 就是它们三个的循环 —— **日志逐行一致**（`Sim battle` 的日志 diff 保证）。
/// <para>
/// 界面因此**不需要自己实现任何规则**：血量、行动值、异常层数、共鸣指数全部读内核的实时状态。
/// 这条边界一旦破了（界面开始自己算伤害），客户端与服务端就会开始分叉。
/// </para>
/// </remarks>
public sealed class BattlePresenter(GameData data, StageDef stage, ulong seed, string? modeOverride,
    IReadOnlyDictionary<string, int>? eidolonRanks = null,
    IReadOnlyDictionary<string, WaningOath.Core.Progression.Loadout>? loadouts = null)
{
    /// <summary>
    /// 谁来决定每一步做什么。默认是 <see cref="GreedyPolicy"/>；
    /// 教学关换成 <see cref="TutorialPolicy"/>（`04 §3.4`）。
    /// </summary>
    public IActionPolicy Policy { get; set; } = new GreedyPolicy();

    /// <summary>本场种子。重放校验要原样提交（`06 §2.2`）。</summary>
    public ulong Seed { get; } = seed;

    public BattleState State { get; private set; } = null!;
    public BattleOutcome Outcome => State.Outcome;
    public bool Finished => State.IsOver;

    /// <summary>本拍新增的日志行数起点 —— 界面只显示"这一拍发生了什么"。</summary>
    public int LastBeatStart { get; private set; }

    public void Start()
    {
        // 升格模态是**编队决策**，写在关卡 / 队伍上（01 §5.4）。
        if (!string.IsNullOrEmpty(modeOverride))
        {
            foreach (string pid in stage.Party)
            {
                CharacterDef cd = data.Character(pid);
                if (cd.Modes is not null && cd.Modes.Entries.ContainsKey(modeOverride)) stage.PartyModes[pid] = modeOverride;
            }
        }

        State = BattleFactory.Create(new BattleConfig
        {
            Data = data,
            Stage = stage,
            Seed = Seed,
            EidolonRanks = eidolonRanks,
            Loadouts = loadouts,
        });
        Runner.Begin(State);
        LastBeatStart = 0;
    }

    /// <summary>推进一拍。返回本拍产生的日志行。</summary>
    public List<string> Advance()
    {
        if (Finished) return [];

        int from = State.Log.Count;
        bool cont = Runner.Step(State, Policy);
        if (!cont && !Finished)
        {
            // `Step` 返回 false 且战斗未结束 = 无单位可行动（理论上不会发生）；这里收尾以免界面卡死。
            Runner.Finish(State);
        }
        else if (!cont)
        {
            Runner.Finish(State);
        }

        LastBeatStart = from;
        return [.. State.Log.Skip(from)];
    }

    /// <summary>一直推进到结束（"自动打完"按钮）。</summary>
    /// <remarks>
    /// 自动托管永远走 <see cref="GreedyPolicy"/>：即使当前 <see cref="Policy"/> 是为手动战斗准备的
    /// <see cref="ManualPolicy"/>（它的 Choices 可能是空的），也不能让整队站着挨打。
    /// </remarks>
    public void RunToEnd()
    {
        IActionPolicy auto = Policy is ManualPolicy ? new GreedyPolicy() : Policy;
        while (!Finished)
        {
            int from = State.Log.Count;
            bool cont = Runner.Step(State, auto);
            if (!cont) Runner.Finish(State);
            if (State.Log.Count == from && !Finished) break; // 防死循环
        }
    }

    public double MaxHpOf(BattleUnit u) => u.MaxHp;
}
