#!/usr/bin/env node
// ============================================================
//  WaningOath - 清洗策划案文档里被外部进程加上的 markdown 转义
//
//  背景（见 策划案/106-林靖远·问题清单与裁决.md §5）：
//    工作目录里有一个我控制不到的东西，会周期性地把 策划案\*.md 里的
//    markdown 标点前面加上反斜杠（** → \*\*、--- → \---、_ → \_ …），
//    还会把引号换成方向错误的弯引号。已经复发三次。
//
//  为什么要有这个脚本，而不是"照文档里那句话手动跑一遍正则"：
//    上一轮我把清洗规则写成了一段文字放在 105 §5，
//    下一轮那段文字自己就被清洗规则弄坏了（\\{2,} 变成 \{2,}）。
//    ⇒ 规则必须是可执行的代码，不能是描述。
//
//  用法:
//    node tools/clean-escapes.mjs            干跑：只报告会改什么
//    node tools/clean-escapes.mjs --apply    真的写回
//    node tools/clean-escapes.mjs --all      连 _backup-转义原文/ 一起处理
//
//  规则:
//    1) 连续 >=2 个反斜杠  ->  1 个。
//       本仓库没有任何合法用途需要连续两个反斜杠（没有 UNC 路径）。
//    2) 「反斜杠 + markdown 标点」-> 去掉反斜杠。
//       标点集合: * _ ~ [ ] ( ) ! + # -
//       ⚠️ **`|` 与 `.` 刻意不在这个集合里**：
//          * 反斜杠 + 竖线是**表格单元格里的竖线转义**，是内容不是多余转义：
//                新增「**装备** \| 遗器、圣遗物」行
//            早期版本把 `|` 也算进集合，结果把这两处真内容改坏了（表格列数 4 变 5）。
//          * 反斜杠 + 点是**点开头名字的路径分隔符**：
//                `WaningOath\.godot\mono\temp\bin`、`harness\.desktop-bin`
//          * `\.` 的真实污染形态只有**标题 / 列表编号**（备份里 73 处全是 `## 1\.`），
//            因此单独用 `\\(\d)\.` 精确匹配。⇒ **永远不要做通用的 `\.` 还原。**
//       ⚠️ 例外: `\_backup-转义原文` 必须保留 —— 它是真实的路径分隔符，
//          不是转义。这个例外是硬编码的，因为它是本仓库里唯一一个
//          以标点开头的目录名。新增这类目录时记得加进来。
//    3) 代码块（``` 围栏）之内只做规则 1，不做规则 2。
//       代码块里可能有 shell / bat / JSON，反斜杠是内容不是转义。
//    4) 【第 4 类污染 · 反向】把「反斜杠 + ASCII 字母 / 点」整对删掉，见下方 REPAIRS。
//       规则 1~3 只会"多删"，这一类是"少东西"，方向相反，因此必须单独补。
//       例: tools\env.cmd -> toolsenv.cmd、.nuget\packages -> .nugetpackages、
//           WaningOath\.godot\mono\temp\bin -> WaningOath.godotmonotempbin
//       ⇒ 靠 REPAIRS 表逐个补回。**这一条会复发，所以每次处理文档都跑一遍本脚本。**
// ============================================================

import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = join(fileURLToPath(new URL('.', import.meta.url)), '..');
const DOC_DIR = join(ROOT, '策划案');

const APPLY = process.argv.includes('--apply');
const INCLUDE_BACKUP = process.argv.includes('--all');

/** 会被当作"转义"去掉的 markdown 标点。⚠️ **不含 `|`** —— 见文件头的说明。 */
const PUNCT = '*_~.[]()!+#-';

/** 不允许被清洗掉的反斜杠路径（本仓库里唯一一个以标点开头的目录名） */
const PROTECTED = ['\\_backup-转义原文'];

/**
 * 第 4 类污染的修复表：[被吃掉的写法, 正确写法]。
 *
 * ⚠️ 只列"本仓库里真实存在、且会被外部进程吃掉"的写法。
 * 不要在这里写通用正则去猜 —— 猜错会把正常文字改成路径。
 * 新增检测法见 106 §5：搜 `tools[a-z]` / `nugetpackages` / `monotempbin` / `Toolsnupkgs`。
 */
const REPAIRS = [
  [/\btools([a-z][a-z0-9-]*)\.(cmd|mjs)\b/g, 'tools\\$1.$2'],   // toolsenv.cmd -> tools\env.cmd
  [/\.nugetpackages\b/g, '.nuget\\packages'],
  [/GodotSharp\\Toolsnupkgs/g, 'GodotSharp\\Tools\\nupkgs'],
  [/WaningOath\.godotmonotempbin/g, 'WaningOath\\.godot\\mono\\temp\\bin'],
  [/redpandacppmingw64bin/g, 'redpandacpp\\mingw64\\bin'],
  [/%APPDATA%dsh-desktopharness\.desktop-binnode\.cmd/g,
    '%APPDATA%\\dsh-desktop\\harness\\.desktop-bin\\node.cmd'],
  [/角色设定守约人\.txt/g, '角色设定\\守约人.txt'],
  [/异常体设定CCRF_017_候门\.txt/g, '异常体设定\\CCRF_017_候门.txt'],
];

/** 规则 4：把"少掉的反斜杠"补回来 */
function repair(text) {
  let out = text;
  for (const [re, to] of REPAIRS) out = out.replace(re, to);
  return out;
}

function protect(text) {
  const marks = [];
  let out = text;
  for (const [i, lit] of PROTECTED.entries()) {
    const token = `\u0000PROT${i}\u0000`;
    if (out.includes(lit)) {
      out = out.split(lit).join(token);
      marks.push([token, lit]);
    }
  }
  return { text: out, marks };
}

function restore(text, marks) {
  let out = text;
  for (const [token, lit] of marks) out = out.split(token).join(lit);
  return out;
}

/** 规则 1：连续 >=2 个反斜杠收敛成 1 个 */
function collapse(text) {
  return text.replace(/\\{2,}/g, '\\');
}

/**
 * 规则 2：去掉「反斜杠 + markdown 标点」里的反斜杠。
 *
 * ⚠️ 两条"精确化"，都是踩过坑才加的：
 *   ① **`|` 与 `.` 不在 PUNCT 里** —— 它们会是真实的路径分隔符
 *      （`新增「装备」 \| 遗器…`、`WaningOath\.godot`、`harness\.desktop-bin`）。
 *   ② `\.` 的**唯一**真实污染形态是**标题/列表的编号**（备份里 73 处全是 `## 1\.`），
 *      所以单独用 `/\(\d)\./` 精确匹配，**不做通用的 `\.` 还原**。
 */
function unescape(text) {
  const cls = PUNCT.replace(/[\\\]^-]/g, (c) => '\\' + c).replace('-', '\\-');
  const out = text.replace(new RegExp(`\\\\([${cls}])`, 'g'), '$1');
  return out.replace(/\\(\d)\./g, '$1.');
}

/**
 * 逐行处理，跟踪 ``` 围栏。
 *
 * 规则 3 的**精化**（踩过坑才加的）：
 *   * 普通围栏（``` / ```cmd / ```bash / ```csharp …）里**只做规则 1** ——
 *     那里的反斜杠是内容（路径、转义字符、行继续符）。
 *   * **```json / ```jsonc / ```json5 围栏里连规则 2 一起做**。
 *     理由是可证明的安全：JSON 里合法的反斜杠只有 `\" \\ \/ \b \f \n \r \t \uXXXX`，
 *     **没有一个是「反斜杠 + PUNCT」**。所以 JSON 围栏里出现 `\[` `\*` `\_` 必然是污染。
 *     实测：`05 §3` 的整段 JSONC Schema 曾被污染成方括号转义、星号转义与块注释转义，
 *     而它正是"实现角色前必读"的那段。
 */
function transform(src) {
  // 规则 5：先判"整文件是否被每字插空格"。是 => 先整体求逆，再走常规清洗。
  if (looksInjected(src)) src = deInject(src);
  const { text, marks } = protect(src);
  const lines = text.split('\n');
  let inFence = false;
  let dataFence = false;
  const out = lines.map((line) => {
    const fence = line.match(/^\s*```(\S*)/);
    if (fence) {
      if (!inFence) {
        const info = (fence[1] || '').toLowerCase();
        dataFence = info === 'json' || info === 'jsonc' || info === 'json5';
      }
      inFence = !inFence;
      if (!inFence) dataFence = false;
      return repair(collapse(line));
    }
    if (!inFence) return repair(unescape(collapse(line)));
    return repair(dataFence ? unescape(collapse(line)) : collapse(line));
  });
  return restore(out.join('\n'), marks);
}

function listDocs() {
  return readdirSync(DOC_DIR)
    .filter((n) => n.endsWith('.md'))
    .filter((n) => INCLUDE_BACKUP || !n.includes('_backup-转义原文'))
    .filter((n) => statSync(join(DOC_DIR, n)).isFile())
    .map((n) => join(DOC_DIR, n));
}

function countBackslash(s) {
  return (s.match(/\\/g) || []).length;
}

let changedFiles = 0;
let totalBefore = 0;
let totalAfter = 0;

for (const file of listDocs()) {
  const src = readFileSync(file, 'utf8');
  const dst = transform(src);
  if (src === dst) continue;

  changedFiles++;
  totalBefore += countBackslash(src);
  totalAfter += countBackslash(dst);

  console.log(`${APPLY ? '改写' : '待改'}  ${basename(file)}  ` +
    `反斜杠 ${countBackslash(src)} -> ${countBackslash(dst)}`);

  if (APPLY) writeFileSync(file, dst, 'utf8');
}

console.log('');
if (changedFiles === 0) {
  console.log('全部干净，无需处理。');
} else {
  console.log(`${changedFiles} 个文件${APPLY ? '已改写' : '待改写'}；` +
    `反斜杠合计 ${totalBefore} -> ${totalAfter}。`);
  if (!APPLY) console.log('加 --apply 才会真的写回。');
}

// 提醒：脚本只处理转义，不处理弯引号。
// 弯引号（“…” / ”…“）需要人工判断，因为正文里的正常引号也会被误伤。
console.log('\n注: 本脚本不处理"方向错误的弯引号"（那是另一个问题，见 106 §5）。');

// ============================================================
//  规则 5【第 6 类污染 · 每字插一个空格】
//
//  现象（实测于 策划案/01、05、06）：
//    外部进程会把文件的**每一个字符后面插一个空格**，连换行也被插：
//      "# 05 · 角色"  ->  "#   0 5   ·   角 色"
//      "\r\n"         ->  "\r \n "      （CR 与 LF 之间也被插了空格）
//    副作用：文件体积翻倍、`## ` 与空行 `\n\n` 全都不可能出现、
//            表格列数异常（同一张表里"被插过"与"没被插过"的行宽度不一致）。
//
//  为什么必须单独一条规则：
//    规则 1~4 全都是"删多余的字符"，这一类是"多了一倍字符"，
//    而且它**会破坏 markdown 结构**（标题、表格、加粗全部失效）。
//
//  ★ 判据（决定性）：正常 markdown 必有 `## ` 与空行 `\n\n`；
//    被插空格后**两者都不可能存在**。⇒ 两者皆无 + 求逆后两者皆有 => 判定被插空格。
//  ★ 求逆是**精确无损**的：取偶数下标字符即可（`s[0], s[2], s[4] ...`）。
//    ⚠️ 只对**整文件都被插**的情况成立。若文件是"混合"的
//       （旧内容被插、新写入的段落没有），相位会在交界处翻转，
//       此时整文件求逆会**打烂新写入的那一段** —— 所以必须先过判据。
// ============================================================

/** 规则 5：把"每字后插一个空格"的文本求逆回原样 */
function deInject(text) {
  let out = '';
  for (let i = 0; i < text.length; i += 2) out += text[i];
  return out;
}

/** 规则 5 的判据：两者皆无（且求逆后两者皆有）才认定被插空格 */
function looksInjected(text) {
  const hasH2 = text.includes('## ');
  const hasBlank = text.includes('\n\n');
  if (hasH2 || hasBlank) return false;
  const inv = deInject(text);
  return inv.includes('## ') || inv.includes('\n\n');
}
