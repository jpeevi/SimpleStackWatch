import { number, bytes, date, parseCsv } from './format.js';
const root = document.querySelector('#dashboard');
const tab = root.dataset.tab;
const content = document.querySelector('#content');
const error = document.querySelector('#error');
const csrf = document.querySelector('#csrf input').value;
const databaseSelect = document.querySelector('#postgres-database');
let refreshing = false;
let sampling = false;
function text(tag, value, className = '') {
  const element = document.createElement(tag);
  element.textContent = value == null ? '—' : String(value);
  element.className = className;
  return element;
}
function table(target, headers, rows, cellFactory) {
  target.replaceChildren();
  if (!rows.length) { target.append(text('div', 'No entries to show.', 'card-body text-secondary')); return; }
  const wrapper = document.createElement('div'); wrapper.className = 'table-responsive';
  const table = document.createElement('table'); table.className = 'table table-hover';
  const head = document.createElement('thead');
  const header = document.createElement('tr');
  headers.forEach(h => { const th = text('th', h); th.scope = 'col'; header.append(th); });
  head.append(header); table.append(head);
  const body = document.createElement('tbody');
  rows.forEach(row => {
    const tr = document.createElement('tr');
    cellFactory(row).forEach(value => {
      const td = document.createElement('td');
      if (value instanceof Node) td.append(value); else td.textContent = value == null ? '—' : String(value);
      tr.append(td);
    });
    body.append(tr);
  });
  table.append(body); wrapper.append(table); target.append(wrapper);
}
async function request(path, method = 'GET') {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), tab === 'ec2' ? 90000 : 65000);
  try {
    const response = await fetch(path, { method, credentials: 'same-origin', signal: controller.signal,
      headers: { Accept: 'application/json', ...(method !== 'GET' ? { 'X-CSRF-TOKEN': csrf } : {}) } });
    if (response.status === 401) { location.assign('/account/login'); throw new Error('Please sign in again.'); }
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(problem.detail || `Request failed (${response.status}).`);
    }
    return await response.json();
  } finally { clearTimeout(timeout); }
}
function message(target, value) { target.replaceChildren(text('div', value, 'card-body text-secondary')); }
function showError(value) { error.textContent = value; error.classList.remove('d-none'); }
function metric(point) { return point == null ? '—' : number(point.value); }
async function refresh() {
  if (refreshing || tab === 'logs' || (databaseSelect && (databaseSelect.disabled || !databaseSelect.options.length))) return;
  refreshing = true;
  document.querySelector('#refresh').disabled = true;
  error.classList.add('d-none');
  if (databaseSelect) databaseSelect.disabled = true;
  const selection = databaseSelect?.value;
  const query = tab === 'postgres' ? `?database=${encodeURIComponent(selection)}` : '';
  try {
    const data = await request(`/api/${tab}${query}`);
    if (data.configured === false) message(content, 'This service is not configured. Set its connection details on the server.');
    else if (tab === 'processes') {
      table(content, ['PID', 'Name', 'CPU %', 'Memory', 'Threads', 'Counters'], data, p => {
        const button = text('button', 'Sample', 'btn btn-sm btn-outline-primary');
        button.type = 'button'; button.disabled = sampling;
        button.addEventListener('click', () => sample(p.pid, button));
        return [p.pid, p.name, number(p.cpuPercent), bytes(p.memoryBytes), p.threads, button];
      });
    } else if (tab === 'postgres') {
      const columns = ['PID', 'User', 'Database', 'Application', 'Client', 'State', 'Wait', 'Query age (s)'];
      if (data.queryTextEnabled) columns.push('Query');
      table(content, columns, data.rows, p => {
        const cells = [p.pid, p.user, p.database, p.application, p.client, p.state, [p.waitType, p.wait].filter(Boolean).join(' / '), number(p.queryAgeSeconds)];
        if (data.queryTextEnabled) cells.push(text('div', p.query, 'cell-query'));
        return cells;
      });
    } else if (tab === 'redis') {
      const rows = data.nodes.flatMap(n => Object.entries(n.values).map(([name, value]) => ({ node: n.endpoint, name, value })));
      table(content, ['Node', 'Metric', 'Value'], rows, r => [r.node, r.name, r.value]);
      content.prepend(text('div', `Ping: ${number(data.latencyMs)} ms`, 'card-body border-bottom small text-secondary'));
    } else if (tab === 'ec2') {
      table(content, ['Instance', 'Name', 'State', 'Type', 'Private IP', 'CPU %', 'Network in (bytes)', 'Network out (bytes)', 'Failed checks', 'Datapoint time'], data.instances, i => [i.id, i.name, i.state, i.type, i.address, metric(i.metrics.CPUUtilization), metric(i.metrics.NetworkIn), metric(i.metrics.NetworkOut), metric(i.metrics.StatusCheckFailed), date(i.metrics.CPUUtilization?.at)]);
    }
    document.querySelector('#updated').textContent = `Updated ${date(new Date())}`;
  } catch (e) { showError(e.message); }
  if (tab === 'postgres' || tab === 'redis') {
    try {
      const health = await request(`/api/health/${tab}${query}`);
      const badge = document.querySelector('#health-status');
      badge.textContent = health.configured ? `${health.status} · ${number(health.durationMs)} ms` : '';
      badge.className = `badge ${health.status === 'Healthy' ? 'text-bg-success' : 'text-bg-danger'}`;
    } catch { document.querySelector('#health-status').textContent = 'Health unavailable'; }
  }
  refreshing = false;
  document.querySelector('#refresh').disabled = false;
  if (databaseSelect) databaseSelect.disabled = false;
}
async function sample(pid) {
  if (sampling) return;
  sampling = true;
  const target = document.querySelector('#counter-content');
  message(target, `Sampling PID ${pid}…`);
  try {
    const result = await request(`/api/processes/${pid}/counters`, 'POST');
    const rows = parseCsv(result.csv);
    if (!rows.length) message(target, 'No counters returned. Check runtime diagnostics configuration.');
    else table(target, rows[0], rows.slice(1), row => row.map((value, column) => rows[0][column].trim().toLowerCase() === 'timestamp' ? date(value) : value));
  } catch (e) { message(target, e.message); }
  finally { sampling = false; }
}
if (tab === 'logs') {
  document.querySelector('#log-folder')?.addEventListener('change', event => {
    const url = event.target.value;
    document.querySelector('#file-logs').dataset.src = url;
    document.querySelector('#log-frame').src = url;
  });
  document.querySelectorAll('.log-source').forEach(button => button.addEventListener('click', () => {
    document.querySelector('#log-frame').src = button.dataset.src;
  }));
} else {
  document.querySelector('#refresh').addEventListener('click', refresh);
  setInterval(() => { if (!document.hidden && document.querySelector('#auto-refresh').checked) refresh(); }, Number(root.dataset.refresh) * 1000);
  if (tab === 'postgres') {
    // Populate names first so the initial query uses a valid server-side index.
    request('/api/postgres/databases').then(databases => {
      databaseSelect.replaceChildren();
      for (const database of databases) {
        const option = text('option', database.name);
        option.value = database.index;
        databaseSelect.append(option);
      }
      if (!databases.length) {
        databaseSelect.append(text('option', 'No databases configured'));
        message(content, 'Add PostgreSQL database names in appsettings and connection strings in environment variables.');
        document.querySelector('#refresh').disabled = true;
        document.querySelector('#auto-refresh').checked = false;
        return;
      }
      databaseSelect.disabled = false;
      databaseSelect.addEventListener('change', () => {
        message(content, 'Loading…');
        document.querySelector('#health-status').textContent = '';
        refresh();
      });
      refresh();
    }).catch(e => showError(e.message));
  } else refresh();
}
for (const endpoint of ['processes', 'metrics']) {
  document.querySelector(`#monitor-${endpoint}`)?.addEventListener('click', async event => {
    const button = event.currentTarget; button.disabled = true;
    const output = document.querySelector('#monitor-output'); output.textContent = 'Loading…';
    try { output.textContent = (await request(`/api/monitor/${endpoint}`)).text; }
    catch (e) { output.textContent = e.message; }
    finally { button.disabled = false; }
  });
}
