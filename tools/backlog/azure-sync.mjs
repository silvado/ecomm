// Cria e mantém o backlog no Azure Boards (processo Scrum) via REST API.
// Uso:
//   $env:AZURE_DEVOPS_EXT_PAT = '<PAT com escopo Work Items: Read & Write>'
//   node tools/backlog/azure-sync.mjs --dry-run   # mostra o que faria
//   node tools/backlog/azure-sync.mjs             # cria os itens que faltam e atualiza os existentes
// Idempotente: ids criados ficam em tools/backlog/azure-ids.<projeto>.json; itens já criados não são recriados.
// Itens existentes recebem estado (derivado das tarefas, ver progress.mjs), critérios de aceite, esforço e trabalho restante
// quando diferem do backlog-data.mjs. Nada é apagado no board.
// Referências: https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/create
//              https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/list
//              https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/update
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { epics } from './backlog-data.mjs';
import { boardState, epicStatus, featureStatus, pbiStatus, taskStatus } from './progress.mjs';

const ORG = process.env.AZDO_ORG ?? 'silvado';
const PROJECT = process.env.AZDO_PROJECT ?? 'Ecomm';
const dryRun = process.argv.includes('--dry-run');
const pat = process.env.AZURE_DEVOPS_EXT_PAT;
if (!dryRun && !pat) {
  console.error('Defina AZURE_DEVOPS_EXT_PAT (nunca grave o PAT em arquivo).');
  process.exit(1);
}

const idsFile = join(dirname(fileURLToPath(import.meta.url)), `azure-ids.${PROJECT}.json`);
const ids = existsSync(idsFile) ? JSON.parse(readFileSync(idsFile, 'utf8')) : {};
const save = () => writeFileSync(idsFile, JSON.stringify(ids, null, 2));
const base = `https://dev.azure.com/${ORG}/${encodeURIComponent(PROJECT)}/_apis/wit/workitems`;
const auth = 'Basic ' + Buffer.from(':' + (pat ?? '')).toString('base64');
const escHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// Campos conferidos em itens já existentes (título e tags ficam como estão no board).
const SYNCED = ['System.State', 'Microsoft.VSTS.Common.AcceptanceCriteria', 'Microsoft.VSTS.Scheduling.Effort', 'Microsoft.VSTS.Scheduling.RemainingWork'];
const existing = []; // { id, type, title, fields }

async function create(key, type, fields, parentId) {
  if (ids[key]) {
    existing.push({ id: ids[key], type, title: fields['System.Title'], fields });
    return ids[key];
  }
  const ops = Object.entries(fields)
    .filter(([, v]) => v !== undefined && v !== '')
    .map(([f, v]) => ({ op: 'add', path: `/fields/${f}`, value: v }));
  if (parentId) {
    ops.push({
      op: 'add',
      path: '/relations/-',
      value: { rel: 'System.LinkTypes.Hierarchy-Reverse', url: `https://dev.azure.com/${ORG}/_apis/wit/workItems/${parentId}` },
    });
  }
  if (dryRun) {
    console.log(`[dry-run] criar ${type}: ${fields['System.Title']} (${fields['System.State']})`);
    return `dry-${key}`;
  }
  const res = await fetch(`${base}/$${encodeURIComponent(type)}?api-version=7.1`, {
    method: 'POST',
    headers: { Authorization: auth, 'Content-Type': 'application/json-patch+json' },
    body: JSON.stringify(ops),
  });
  if (!res.ok) throw new Error(`${type} "${fields['System.Title']}": HTTP ${res.status} ${await res.text()}`);
  const item = await res.json();
  ids[key] = item.id;
  save();
  console.log(`#${item.id} ${type}: ${fields['System.Title']}`);
  return item.id;
}

async function currentFields(itemIds) {
  const result = new Map();
  for (let i = 0; i < itemIds.length; i += 200) {
    const chunk = itemIds.slice(i, i + 200);
    const res = await fetch(`${base}?ids=${chunk.join(',')}&fields=${SYNCED.join(',')}&errorPolicy=omit&api-version=7.1`, {
      headers: { Authorization: auth },
    });
    if (!res.ok) throw new Error(`Leitura dos itens: HTTP ${res.status} ${await res.text()}`);
    for (const item of (await res.json()).value) if (item) result.set(item.id, item.fields);
  }
  return result;
}

const same = (a, b) => (a ?? '') === (b ?? '') || (typeof b === 'number' && Number(a) === b);

async function updateExisting() {
  if (existing.length === 0) return;
  if (dryRun && !pat) {
    console.log(`[dry-run] ${existing.length} itens existentes não conferidos (sem PAT).`);
    return;
  }
  const current = await currentFields(existing.map((e) => e.id));
  let updated = 0;
  for (const item of existing) {
    const now = current.get(item.id);
    if (!now) {
      console.warn(`#${item.id} ${item.type} "${item.title}" não existe mais no board — ignorado.`);
      continue;
    }
    const ops = SYNCED.filter((f) => item.fields[f] !== undefined && !same(now[f], item.fields[f]))
      .map((f) => ({ op: 'add', path: `/fields/${f}`, value: item.fields[f] }));
    if (ops.length === 0) continue;
    const changes = ops.map((o) => `${o.path.split('.').pop()}=${String(o.value).slice(0, 40)}`).join(', ');
    if (dryRun) {
      console.log(`[dry-run] atualizar #${item.id} ${item.type}: ${item.title} — ${changes}`);
      continue;
    }
    const res = await fetch(`${base}/${item.id}?api-version=7.1`, {
      method: 'PATCH',
      headers: { Authorization: auth, 'Content-Type': 'application/json-patch+json' },
      body: JSON.stringify(ops),
    });
    if (!res.ok) throw new Error(`#${item.id} "${item.title}": HTTP ${res.status} ${await res.text()}`);
    updated++;
    console.log(`#${item.id} ${item.type}: ${item.title} — ${changes}`);
  }
  console.log(`${updated} itens atualizados.`);
}

const sum = (arr, fn) => arr.reduce((s, x) => s + fn(x), 0);
const pbiH = (p) => sum(p.t, ([, h]) => h);
const featH = (f) => sum(f.pbis, pbiH);

for (const e of epics) {
  const eId = await create(e.title, 'Epic', {
    'System.Title': e.title,
    'System.Description': escHtml(e.desc),
    'System.Tags': e.tags,
    'System.State': boardState('Epic', epicStatus(e)),
    'Microsoft.VSTS.Scheduling.Effort': sum(e.features, featH),
  });
  for (const f of e.features) {
    const fKey = `${e.title} / ${f.title}`;
    const fId = await create(fKey, 'Feature', {
      'System.Title': f.title,
      'System.Tags': e.tags,
      'System.State': boardState('Feature', featureStatus(f)),
      'Microsoft.VSTS.Scheduling.Effort': featH(f),
    }, eId);
    for (const p of f.pbis) {
      const pKey = `${fKey} / ${p.title}`;
      const tags = [e.tags, ...p.rf.split(',').map((s) => s.trim()).filter((s) => s && s !== '—')].join('; ');
      const pId = await create(pKey, 'Product Backlog Item', {
        'System.Title': p.title,
        'System.Description': escHtml(`Requisitos: ${p.rf}`),
        'Microsoft.VSTS.Common.AcceptanceCriteria': escHtml(p.ac),
        'System.Tags': tags,
        'System.State': boardState('Product Backlog Item', pbiStatus(p)),
        'Microsoft.VSTS.Scheduling.Effort': pbiH(p),
      }, fId);
      for (const task of p.t) {
        const [t, h] = task;
        const status = taskStatus(task);
        await create(`${pKey} / ${t}`, 'Task', {
          'System.Title': t,
          'System.Tags': e.tags,
          'System.State': boardState('Task', status),
          'Microsoft.VSTS.Scheduling.RemainingWork': status === 'done' ? 0 : h,
        }, pId);
      }
    }
  }
}
await updateExisting();
console.log(dryRun ? 'Dry-run concluído.' : `Concluído. Ids em ${idsFile}`);
