// 把 剧情文本/*.md 转成客户端读的 Assets/Story/*.story.json
//
// 标记规范见 剧情文本/第一章 旧账未清.md 的「标记」一节：
//   **角色：**"台词"       -> { kind: "say",  speaker, text }
//   *（……）*              -> { kind: "act",  text }        演出/动作
//   > **【……】**          -> { kind: "note", text }        系统提示
//   > **——战斗开始——**    -> { kind: "battle", text }      战斗节点
//   | … | … |             -> { kind: "note", table: [...] } 档案/表单
//   其它非空行             -> { kind: "act",  text }        旁白
//
// 场景按"演出/旁白里出现的第一个地名"切换（关键词表见 SCENES），切不出来就沿用上一拍。

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';

const ROOT = process.cwd();
const OUT_DIR = join(ROOT, 'WaningOath', 'Assets', 'Story');

// 战斗节点 -> 关卡 id（只有「——战斗开始——」算节点，「战斗结束」丢掉）
const STAGE_AT = {
  'chapter-1/s4': 'stage-main-c1-s4',
  'chapter-1/s6': 'stage-main-c1-s6',
};

// 按小节定场（旁白里出现地名就切场景并不可靠 —— 车停在门口时也会提到"检查站"）。
// 以后要在一节内切景，就在这里加 `sN: [[拍号, 'slug'], …]` 的覆盖表。
const SECTION_SCENE = {
  'chapter-1/s1': 'site14-lobby',
  'chapter-1/s2': 'site14-storage-d3',
  'chapter-1/s3': 'site14-archive',
  'chapter-1/s4': 'site14-storage-d3',
  'chapter-1/s5': 'site14-director-office',
  'chapter-1/s6': 'site14-director-office',
  'chapter-1/s7': 'site14-canteen',
  'interlude-1/s1': 'wilds-old-road',
  'interlude-1/s2': 'wilds-318-tunnel',
  'interlude-1/s3': 'wilds-coast-overlook',
};

// 旧的关键词表：只作为"以后要自动切景"的备查，不再参与生成。
const SCENES = [
  ['惰性库房', 'site14-storage-d3'],
  ['库房', 'site14-storage-d3'],
  ['档案室', 'site14-archive'],
  ['总监办公室', 'site14-director-office'],
  ['办公室', 'site14-director-office'],
  ['门厅', 'site14-lobby'],
  ['走廊', 'site14-corridor-d'],
  ['安保室', 'site14-corridor-d'],
  ['食堂', 'site14-canteen'],
  ['备勤通道', 'site14-gate-c7'],
  ['C 区 7 号', 'site14-gate-c7'],
  ['大门', 'site14-main-gate'],
  ['第四根路灯', 'site14-main-gate'],
  ['旧路', 'wilds-old-road'],
  ['高危区', 'wilds-318-edge'],
  ['内部通道', 'wilds-318-tunnel'],
  ['触手通道', 'wilds-318-tunnel'],
  ['海边', 'wilds-coast-overlook'],
  ['岔口', 'wilds-coast-overlook'],
  ['检查站', 'uneg-checkpoint'],
  ['边境', 'uneg-checkpoint'],
];

// 说话人 -> 立绘 key（GameRoot.ResolveStoryPortrait 认的值；缺省 = 没有立绘）
const PORTRAITS = {
  守约人: 'protagonist',
  千语星遥: 'xingyao',
  千语镜星: 'jingxing',
  爱丽丝: 'alice',
};

// 说话人 -> 阵营（与 GameRoot.FactionOf 同口径；缺省 = 不写阵营行）
const FACTIONS = {
  千语星遥: 'CCRF · SITE 14',
  千语镜星: 'CCRF · SITE 14',
  爱丽丝: 'CCRF · 318',
  'CCRF-318': 'CCRF · 318',
};

const stripInline = (s) =>
  s
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\*\*/g, '')
    .replace(/`/g, '')
    .trim();

const detectScene = (text, current) =>
  SCENES.find(([word]) => text.includes(word))?.[1] ?? current;

function parseBody(body, sectionKey) {
  const beats = [];
  let scene = '';
  let table = [];
  const flush = () => {
    if (table.length) { beats.push({ kind: 'note', table: table.slice() }); table = []; }
  };

  for (const raw of body) {
    const line = raw.trim();
    if (!line) { flush(); continue; }
    if (/^>\s*(所属|前置|后续)/.test(line)) continue;
    if (/^>\s*\*\*分支/.test(line)) continue;
    if (/^-{3,}/.test(line) || /^#/.test(line)) continue;
    // 导航/引用块（`> 返回目录 ｜ 下一小节`、空 `>`）不是正文
    if (line === '>' || (line.startsWith('>') && !/^>\s*\*\*【/.test(line) && !/^>\s*\*\*——战斗/.test(line))) continue;

    if (line.startsWith('|')) {
      const cells = line.split('|').slice(1, -1).map(stripInline);
      if (!cells.length || cells.every((c) => /^-*$/.test(c))) continue;
      if (cells[0] === '项' && cells[1] === '内容') continue; // 小节开头的元信息表
      if (cells[0] === '场景' || cells[0] === '出场' || cells[0] === '说明') continue;
      table.push(cells);
      continue;
    }
    flush();

    const note = line.match(/^>\s*\*\*【(.+?)】\*\*(.*)$/);
    if (note) {
      beats.push({ kind: 'note', text: `【${note[1].trim()}】${stripInline(note[2])}` });
      continue;
    }

    if (/^>\s*\*\*——战斗/.test(line)) {
      if (line.includes('开始')) {
        const beat = { kind: 'battle', text: '——战斗——' };
        const stage = STAGE_AT[sectionKey];
        if (stage) beat.stage = stage;
        beats.push(beat);
      }
      continue;
    }

    const say = line.match(/^\*\*([^*]+?)[：:]\*\*\s*(.+)$/);
    if (say) {
      const speaker = say[1].trim();
      const text = stripInline(say[2]).replace(/^[“"]|[”"]$/g, '').trim();
      if (text) {
        const beat = { kind: 'say', speaker, text };
        if (PORTRAITS[speaker]) beat.portrait = PORTRAITS[speaker];
        if (FACTIONS[speaker]) beat.faction = FACTIONS[speaker];
        beats.push(beat);
      }
      continue;
    }

    const act = stripInline(line.replace(/^\*+|\*+$/g, ''));
    if (act) beats.push({ kind: 'act', text: act });
  }
  flush();
  return beats;
}

/** 一个 md 文件 -> 若干 section：有 H2 就按 H2 切，否则整篇算一段（标题取 H1）。 */
function splitSections(file) {
  const text = readFileSync(join(ROOT, '剧情文本', file), 'utf8');
  const lines = text.split(/\r?\n/);
  const h1 = lines.find((l) => /^#\s+/.test(l));
  const docTitle = h1 ? stripInline(h1.replace(/^#\s+/, '')) : file;

  const hasH2 = lines.some((l) => /^##\s+/.test(l));
  if (!hasH2) return [{ title: docTitle, body: lines }];

  const out = [];
  let cur = null;
  for (const line of lines) {
    const m = line.match(/^##\s+(.*)$/);
    if (m) {
      if (cur) out.push(cur);
      cur = { title: stripInline(m[1]), body: [] };
      continue;
    }
    if (cur) cur.body.push(line);
  }
  if (cur) out.push(cur);
  return out;
}

function sectionId(title, index) {
  const m = title.match(/第\s*(\d+)\s*小节/);
  return 's' + (m ? m[1] : index + 1);
}

function convert(id, title, files) {
  const sections = [];
  files.forEach((file, i) => {
    for (const raw of splitSections(file)) {
      const beats = parseBody(raw.body, `${id}/s${sections.length + 1}`);
      if (!beats.length) continue;
      const sid = sectionId(raw.title, sections.length);
      const section = { id: sid, title: raw.title, beats };
      const scene = SECTION_SCENE[`${id}/${sid}`];
      if (scene) section.scene = scene;
      sections.push(section);
    }
  });

  const doc = { id, title, sections, beatCount: sections.reduce((n, s) => n + s.beats.length, 0) };
  mkdirSync(OUT_DIR, { recursive: true });
  const out = join(OUT_DIR, id + '.story.json');
  writeFileSync(out, JSON.stringify(doc) + '\n', 'utf8');
  console.log(`${id}: ${sections.length} 段 / ${doc.beatCount} 拍 / ${(JSON.stringify(doc).length / 1024).toFixed(0)} KB`);
  return doc;
}

convert('chapter-1', '第一章 · 旧账未清', [
  '第一章 第1小节 应到六十一.md',
  '第一章 第2小节 三毫米的旧账.md',
  '第一章 第3小节 被划掉的那一行.md',
  '第一章 第4小节 第一次断线.md',
  '第一章 第5小节 借来的名字.md',
  '第一章 第6小节 两清.md',
  '第一章 第7小节 名单之外.md',
]);

convert('interlude-1', '第一章幕间 · 请低头', [
  '第一章幕间 请低头.md',
]);
