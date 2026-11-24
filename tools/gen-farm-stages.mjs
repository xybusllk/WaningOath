// gen-farm-stages.mjs —— 生成「定期清剿」的 16 个刷本关卡，并补齐武器素材道具。
//
// 为什么用脚本生成而不是手写 16 个 JSON：这 16 个文件的结构完全同构，
// 差别只在"哪一版"（属性 / 武器类型）与掉落物。手写 16 份等于把同一个错字复制 16 遍。
//
// 数值口径（全部来自策划案，没有发明）：
//   阶跃核心本   40 配额 · 突破强敌档（精英）—— 02 §2.3：阶跃核心由突破强敌掉落，每次 40 配额
//   高危样本本   60 配额 · 周本首领档       —— 02 §2.3：首领每周 3 次免费，之后 60 配额/次，每次掉 4~5 个
//   武器素材本   30 配额 · 杂兵档           —— 03 §2：武器素材由经验本 / 材料本掉落（四类）
//
// 属性的合并口径来自作者本轮的口述：**离谒与冰共用、咏叹与共振共用** ⇒ 8 属性折成 6 关。

import { writeFileSync, readFileSync, mkdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const stagesDir = join(root, 'data', 'stages');
const growthPath = join(root, 'data', 'tables', 'growth.json');

const NOTE = '「定期清剿」刷本。数值口径：02 §2.3（阶跃核心 40 配额 / 高危样本 60 配额）与 03 §2 的材料表。'
  + '敌人暂用已落盘的异常体（属性刷本的"属性"体现在掉落物与入口上，异常体本身还没有逐属性落盘）。';

const PARTY = ['char-shouyueren', 'char-qianyu-xingyao', 'char-belrand', 'char-alice'];

/** 属性刷本：6 关（离谒与冰共用、咏叹与共振共用）。 */
const ELEMENTS = [
  { variant: 'physical', label: '物理' },
  { variant: 'ice', label: '冰 · 离谒' },
  { variant: 'fire', label: '火' },
  { variant: 'lightning', label: '雷' },
  { variant: 'wind', label: '风' },
  { variant: 'resonance', label: '共振 · 咏叹' },
];

/** 武器素材：4 类（03 §2 的「单刀 / 长剑 / 施术仪 / 配枪」）。 */
const WEAPONS = [
  { variant: 'dao', label: '单刀', item: 'weaponMatDao' },
  { variant: 'sword', label: '长剑', item: 'weaponMatSword' },
  { variant: 'focus', label: '施术仪', item: 'weaponMatFocus' },
  { variant: 'gun', label: '配枪', item: 'weaponMatGun' },
];

function stage({ id, name, kind, variant, enemies, stamina, unlock, repeat, first, maxRound = 15 }) {
  return {
    id,
    name,
    kind,
    variant,
    _note: NOTE,
    party: PARTY,
    enemies,
    maxRound,
    reward: {
      note: '产出与消耗照抄 02 §2.3 / 03 §2；首通奖励翻倍属【我提议】。',
      repeatable: true,
      staminaCost: stamina,
      unlockProtocolLevel: unlock,
      firstClear: first,
      repeat,
    },
  };
}

mkdirSync(stagesDir, { recursive: true });
const written = [];

for (const e of ELEMENTS) {
  // 阶跃核心本：精英档（突破强敌）
  const coreId = `stage-core-${e.variant}`;
  writeFileSync(join(stagesDir, `${coreId}.json`), JSON.stringify(stage({
    id: coreId,
    name: `阶跃核心 · ${e.label}`,
    kind: 'core',
    variant: e.variant,
    enemies: ['anom-ccrf-502'],
    stamina: 40,
    unlock: 1,
    repeat: { items: { jumpCore: 2 } },
    first: { items: { jumpCore: 4 } },
  }), null, 2) + '\n', 'utf8');
  written.push(coreId);

  // 高危样本本：首领档（周本）
  const hazardId = `stage-hazard-${e.variant}`;
  writeFileSync(join(stageDirSafe(), `${hazardId}.json`), JSON.stringify(stage({
    id: hazardId,
    name: `高危样本 · ${e.label}`,
    kind: 'hazard',
    variant: e.variant,
    enemies: ['anom-ccrf-411'],
    stamina: 60,
    unlock: 3,
    repeat: { items: { hazardSample: 4 } },
    first: { items: { hazardSample: 6 } },
  }), null, 2) + '\n', 'utf8');
  written.push(hazardId);
}

function stageDirSafe() { return stagesDir; }

for (const w of WEAPONS) {
  const id = `stage-weapon-${w.variant}`;
  writeFileSync(join(stagesDir, `${id}.json`), JSON.stringify(stage({
    id,
    name: `武器素材 · ${w.label}`,
    kind: 'weapon',
    variant: w.variant,
    enemies: ['anom-ccrf-017', 'anom-ccrf-017', 'anom-ccrf-017'],
    stamina: 30,
    unlock: 1,
    repeat: { items: { [w.item]: 5 } },
    first: { items: { [w.item]: 8 } },
  }), null, 2) + '\n', 'utf8');
  written.push(id);
}

// ── 武器素材道具（03 §2 的四类）──
const growth = JSON.parse(readFileSync(growthPath, 'utf8'));
const weaponItems = {
  weaponMatDao: '单刀素材',
  weaponMatSword: '长剑素材',
  weaponMatFocus: '施术仪素材',
  weaponMatGun: '配枪素材',
};
let added = 0;
for (const [id, name] of Object.entries(weaponItems)) {
  if (!growth.items[id]) {
    growth.items[id] = { name, kind: 'weapon' };
    added++;
  }
}
writeFileSync(growthPath, JSON.stringify(growth, null, 2) + '\n', 'utf8');

console.log(`wrote ${written.length} stage files`);
console.log(written.join(' '));
console.log(`added ${added} weapon material items to growth.json`);
