// Gera docs/backlog.md e docs/backlog.csv a partir de backlog-data.mjs.
// Uso: node tools/backlog/generate.mjs
import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { epics } from './backlog-data.mjs';
import { pbiStatus, taskStatus } from './progress.mjs';

const label = { todo: '', doing: ' · **em andamento**', done: ' · **concluído**' };

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

const pbiHours = (p) => p.t.reduce((s, [, h]) => s + h, 0);
const featureHours = (f) => f.pbis.reduce((s, p) => s + pbiHours(p), 0);
const epicHours = (e) => e.features.reduce((s, f) => s + featureHours(f), 0);
const total = epics.reduce((s, e) => s + epicHours(e), 0);

// ---------- Markdown ----------
const md = [];
md.push('# Backlog', '');
md.push('> Gerado por `node tools/backlog/generate.mjs` a partir de `tools/backlog/backlog-data.mjs`. Não editar à mão.', '');
md.push('Hierarquia Scrum do Azure Boards: **Epic → Feature → Product Backlog Item → Task**. Estimativas em horas de desenvolvimento (uma pessoa), sem buffer.', '');
md.push('| Etapa | Horas |', '|---|---:|');
for (const e of epics) md.push(`| ${e.title} | ${epicHours(e)} |`);
md.push(`| **Total** | **${total}** |`, '');
const doneHours = epics.flatMap((e) => e.features.flatMap((f) => f.pbis.flatMap((p) => p.t)))
  .filter((task) => taskStatus(task) === 'done').reduce((s, [, h]) => s + h, 0);
md.push(`Progresso: ${doneHours} h de ${total} h em tarefas concluídas (${Math.round((100 * doneHours) / total)}%).`, '');
md.push(`Referência: 6 h produtivas/dia ≈ ${Math.round(total / 6)} dias úteis ≈ ${(total / 6 / 21).toFixed(1).replace(".", ",")} meses de uma pessoa.`, '');

for (const e of epics) {
  md.push(`## ${e.title} — ${epicHours(e)} h`, '', e.desc, '');
  for (const f of e.features) {
    md.push(`### ${f.title} — ${featureHours(f)} h`, '');
    for (const p of f.pbis) {
      md.push(`#### ${p.title} — ${pbiHours(p)} h  \`${p.rf}\`${label[pbiStatus(p)]}`, '');
      md.push(`**Critérios de aceite:** ${p.ac}`, '');
      for (const task of p.t) {
        const [t, h] = task;
        const status = taskStatus(task);
        md.push(`- [${status === 'done' ? 'x' : ' '}] ${t} — ${h} h${status === 'doing' ? ' *(em andamento)*' : ''}`);
      }
      md.push('');
    }
  }
}
writeFileSync(join(root, 'docs', 'backlog.md'), md.join('\n'));

// ---------- CSV (importação Azure Boards, hierarquia por Title 1..4) ----------
const esc = (v) => {
  const s = String(v ?? '');
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
};
const header = ['Work Item Type', 'Title 1', 'Title 2', 'Title 3', 'Title 4', 'Description', 'Acceptance Criteria', 'Tags', 'Effort', 'Remaining Work'];
const rows = [header];
const row = (type, level, title, o = {}) => {
  const titles = ['', '', '', ''];
  titles[level] = title;
  rows.push([type, ...titles, o.desc ?? '', o.ac ?? '', o.tags ?? '', o.effort ?? '', o.remaining ?? '']);
};
for (const e of epics) {
  row('Epic', 0, e.title, { desc: e.desc, tags: e.tags, effort: epicHours(e) });
  for (const f of e.features) {
    row('Feature', 1, f.title, { tags: e.tags, effort: featureHours(f) });
    for (const p of f.pbis) {
      const tags = [e.tags, ...p.rf.split(',').map((s) => s.trim()).filter((s) => s && s !== '—')].join('; ');
      row('Product Backlog Item', 2, p.title, { desc: `Requisitos: ${p.rf}`, ac: p.ac, tags, effort: pbiHours(p) });
      for (const [t, h] of p.t) row('Task', 3, t, { tags: e.tags, remaining: h });
    }
  }
}
writeFileSync(join(root, 'docs', 'backlog.csv'), '﻿' + rows.map((r) => r.map(esc).join(',')).join('\r\n') + '\r\n');

const count = (type) => rows.filter((r) => r[0] === type).length;
console.log(`Total: ${total} h | Epics ${count('Epic')} | Features ${count('Feature')} | PBIs ${count('Product Backlog Item')} | Tasks ${count('Task')}`);
