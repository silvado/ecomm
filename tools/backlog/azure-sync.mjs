// Cria o backlog no Azure Boards (processo Scrum) via REST API.
// Uso:
//   $env:AZURE_DEVOPS_EXT_PAT = '<PAT com escopo Work Items: Read & Write>'
//   node tools/backlog/azure-sync.mjs --dry-run   # mostra o que faria
//   node tools/backlog/azure-sync.mjs             # cria os itens
// Idempotente: ids criados ficam em tools/backlog/azure-ids.json; itens já criados são pulados.
// Referência: https://learn.microsoft.com/rest/api/azure/devops/wit/work-items/create
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { epics } from './backlog-data.mjs';

const ORG = process.env.AZDO_ORG ?? 'silvado';
const PROJECT = process.env.AZDO_PROJECT ?? 'Ecommerce';
const dryRun = process.argv.includes('--dry-run');
const pat = process.env.AZURE_DEVOPS_EXT_PAT;
if (!dryRun && !pat) {
  console.error('Defina AZURE_DEVOPS_EXT_PAT (nunca grave o PAT em arquivo).');
  process.exit(1);
}

const idsFile = join(dirname(fileURLToPath(import.meta.url)), 'azure-ids.json');
const ids = existsSync(idsFile) ? JSON.parse(readFileSync(idsFile, 'utf8')) : {};
const save = () => writeFileSync(idsFile, JSON.stringify(ids, null, 2));
const base = `https://dev.azure.com/${ORG}/${encodeURIComponent(PROJECT)}/_apis/wit/workitems`;
const auth = 'Basic ' + Buffer.from(':' + (pat ?? '')).toString('base64');
const escHtml = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

async function create(key, type, fields, parentId) {
  if (ids[key]) return ids[key];
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
    console.log(`[dry-run] ${type}: ${fields['System.Title']}`);
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

const sum = (arr, fn) => arr.reduce((s, x) => s + fn(x), 0);
const pbiH = (p) => sum(p.t, ([, h]) => h);
const featH = (f) => sum(f.pbis, pbiH);

for (const e of epics) {
  const eId = await create(e.title, 'Epic', {
    'System.Title': e.title,
    'System.Description': escHtml(e.desc),
    'System.Tags': e.tags,
    'Microsoft.VSTS.Scheduling.Effort': sum(e.features, featH),
  });
  for (const f of e.features) {
    const fKey = `${e.title} / ${f.title}`;
    const fId = await create(fKey, 'Feature', {
      'System.Title': f.title,
      'System.Tags': e.tags,
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
        'Microsoft.VSTS.Scheduling.Effort': pbiH(p),
      }, fId);
      for (const [t, h] of p.t) {
        await create(`${pKey} / ${t}`, 'Task', {
          'System.Title': t,
          'System.Tags': e.tags,
          'Microsoft.VSTS.Scheduling.RemainingWork': h,
        }, pId);
      }
    }
  }
}
console.log(dryRun ? 'Dry-run concluído.' : `Concluído. Ids em ${idsFile}`);
