// Estados do processo Scrum do Azure Boards, derivados do estado das tarefas em backlog-data.mjs.
// Task: To Do / In Progress / Done. PBI: New / Committed / Done. Feature e Epic: New / In Progress / Done.
// Referência: https://learn.microsoft.com/azure/devops/boards/work-items/guidance/scrum-process-workflow

/** Estado da tarefa: 'todo', 'doing' ou 'done'. */
export const taskStatus = ([, , state]) => state ?? 'todo';

const combine = (statuses) =>
  statuses.length > 0 && statuses.every((s) => s === 'done') ? 'done'
    : statuses.some((s) => s !== 'todo') ? 'doing'
    : 'todo';

export const pbiStatus = (p) => combine(p.t.map(taskStatus));
export const featureStatus = (f) => combine(f.pbis.map(pbiStatus));
export const epicStatus = (e) => combine(e.features.map(featureStatus));

const states = {
  Task: { todo: 'To Do', doing: 'In Progress', done: 'Done' },
  'Product Backlog Item': { todo: 'New', doing: 'Committed', done: 'Done' },
  Feature: { todo: 'New', doing: 'In Progress', done: 'Done' },
  Epic: { todo: 'New', doing: 'In Progress', done: 'Done' },
};
export const boardState = (type, status) => states[type][status];
