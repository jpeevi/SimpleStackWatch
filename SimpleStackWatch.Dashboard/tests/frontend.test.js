import test from 'node:test';
import assert from 'node:assert/strict';
import { parseCsv, bytes, number, date } from '../wwwroot/js/format.js';
test('counter CSV handles quoted metric names, escaped quotes and CRLF', () => {
 assert.deepEqual(parseCsv('Timestamp,Name,Value\r\n"2026-10-02","Heap, Gen 0",12\r\n"t","a ""quoted"" name",5'), [['Timestamp','Name','Value'], ['2026-10-02','Heap, Gen 0','12'], ['t','a "quoted" name','5']]);
});
test('counter CSV preserves embedded newlines and ignores blank trailing rows', () => {
 assert.deepEqual(parseCsv('Name,Value\n"two\nlines",3\n\n'), [['Name','Value'], ['two\nlines','3']]);
});
test('missing measurements stay distinct from zero', () => {
 assert.equal(number(null), '—'); assert.equal(number(0), '0');
 assert.equal(bytes(null), '—'); assert.match(bytes(1048576), /^1 MiB$/);
});

test('timestamps display day first with padded seconds', () => {
 assert.equal(date(new Date(2026, 1, 3, 4, 5, 6)), '03/02/2026 04:05:06');
 assert.equal(date(null), '—');
 assert.equal(date('not a timestamp'), '—');
});

test('PostgreSQL selection uses the same index for sessions and health', async () => {
 const saved = { document: globalThis.document, fetch: globalThis.fetch, setInterval: globalThis.setInterval };
 class Element {
  children = []; disabled = false; checked = true; dataset = {}; listeners = {};
  classList = { add() {}, remove() {} };
  value = ''; textContent = '';
  get options() { return this.children; }
  append(child) { this.children.push(child); if (this.children.length === 1) this.value = String(child.value); }
  replaceChildren(...children) { this.children = []; children.forEach(child => this.append(child)); }
  addEventListener(name, handler) { this.listeners[name] = handler; }
 }
 const ids = ['dashboard', 'content', 'error', 'postgres-database', 'refresh', 'auto-refresh', 'health-status', 'updated'];
 const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
 elements.dashboard.dataset = { tab: 'postgres', refresh: '10' };
 elements['postgres-database'].disabled = true;
 const paths = [];
 let releaseSessions;
 try {
  globalThis.document = { hidden: false, createElement: () => new Element(),
   querySelector: selector => selector === '#csrf input' ? { value: 'csrf' } : elements[selector.slice(1)] ?? null };
  globalThis.setInterval = () => 0;
  globalThis.fetch = async path => {
   paths.push(path);
   if (path === '/api/postgres?database=1') await new Promise(resolve => { releaseSessions = resolve; });
   const body = path.endsWith('/databases') ? [{ index: 0, name: 'Application' }, { index: 1, name: 'Reporting' }]
    : path.includes('/health/') ? { configured: true, status: 'Healthy', durationMs: 1 } : { configured: false };
   return { status: 200, ok: true, json: async () => body };
  };
  const settle = async () => { for (let n = 0; n < 4; n++) await new Promise(resolve => setImmediate(resolve)); };
  await import('../wwwroot/js/dashboard.js?postgres-selection-test');
  await settle();
  assert.deepEqual(paths, ['/api/postgres/databases', '/api/postgres?database=0', '/api/health/postgres?database=0']);
  const select = elements['postgres-database'];
  assert.deepEqual(select.options.map(option => option.textContent), ['Application', 'Reporting']);
  select.value = '1'; select.listeners.change();
  await settle();
  assert.equal(select.disabled, true, 'selection is locked until sessions and health complete');
  releaseSessions(); await settle();
  assert.deepEqual(paths.slice(-2), ['/api/postgres?database=1', '/api/health/postgres?database=1']);
  assert.equal(select.disabled, false);
 } finally {
  for (const [key, value] of Object.entries(saved)) {
   if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
  }
 }
});

test('overview summary and health classes distinguish unknown from failing', async () => {
 const { statusClass, summarizeTargets } = await import('../wwwroot/js/overview.js');
 const counts = summarizeTargets([
  { status: 'Healthy' }, { status: 'Healthy' }, { status: 'Degraded' },
  { status: 'Down' }, { status: 'Unknown' }
 ]);
 assert.deepEqual(counts, { total: 5, healthy: 2, degraded: 1, down: 1, unknown: 1 });
 assert.equal(statusClass('Healthy'), 'status-tag status-healthy');
 assert.equal(statusClass('Degraded'), 'status-tag status-degraded');
 assert.equal(statusClass('Down'), 'status-tag status-down');
 assert.equal(statusClass('Unknown'), 'status-tag status-unknown');
});

test('overview polls authenticated data and loads per-target history', async () => {
 const saved = { document: globalThis.document, fetch: globalThis.fetch,
  setInterval: globalThis.setInterval, Node: globalThis.Node };
 class Element {
  children = []; disabled = false; checked = true; hidden = false;
  dataset = {}; listeners = {}; classList = { add() {}, remove() {} };
  textContent = ''; value = '';
  get options() { return this.children; }
  append(...items) { this.children.push(...items); }
  prepend(item) { this.children.unshift(item); }
  replaceChildren(...items) { this.children = items; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
 }
 const ids = ['dashboard', 'content', 'error', 'refresh', 'auto-refresh', 'health-status',
  'updated', 'overview-environment', 'overview-summary', 'overview-history-section',
  'overview-history-title', 'overview-history-close', 'overview-history', 'overview-search'];
 const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
 elements.dashboard.dataset = { tab: 'overview', refresh: '10' };
 const paths = [];
 try {
  globalThis.Node = Element;
  globalThis.document = { hidden: false, createElement: () => new Element(),
   querySelector: sel => sel === '#csrf input' ? { value: 'csrf' } : elements[sel.slice(1)] ?? null };
  globalThis.setInterval = () => 0;
  globalThis.fetch = async path => {
   paths.push(path);
   const result = path.includes('/history')
    ? [{ checkedAt: '2026-11-22T10:00:00Z', status: 'Healthy', durationMs: 12, detail: 'HTTP 200.' }]
    : { targets: [{ id: 'example', name: 'Example API', environment: 'UAT',
       group: 'Applications', kind: 'http', status: 'Healthy', durationMs: 12,
       checkedAt: '2026-11-22T10:00:00Z', detail: 'HTTP 200.' }] };
   return { ok: true, status: 200, json: async () => result };
  };
  const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };
  await import('../wwwroot/js/dashboard.js?overview-test');
  await settle();
  assert.deepEqual(paths, ['/api/overview']);
  const table = elements.content.children[0].children[0];
  const row = table.children[1].children[0];
  const historyButton = row.children[8].children[0];
  historyButton.listeners.click();
  await settle();
  assert.equal(paths[1], '/api/overview/history?targetId=example&limit=50');
  assert.equal(elements['overview-history-section'].hidden, false);
  elements['overview-history-close'].listeners.click();
  assert.equal(elements['overview-history-section'].hidden, true);
  elements['overview-search'].value = 'missing';
  elements['overview-search'].listeners.input();
  assert.equal(elements.content.children[0].textContent, 'No targets match your search.');
  elements['overview-search'].value = 'example';
  elements['overview-search'].listeners.input();
  assert.equal(elements.content.children[0].children[0].children[1].children.length, 1);
 } finally {
  for (const [name, value] of Object.entries(saved)) {
   if (value === undefined) delete globalThis[name]; else globalThis[name] = value;
  }
 }
});


test('mobile navigation locks the page, traps focus, and closes with Escape', async () => {
 const saved = { document: globalThis.document, window: globalThis.window };
 const elements = {};
 class Element {
  listeners = {}; attrs = {}; inert = false; hidden = true;
  tokens = new Set();
  classList = { toggle: (name, enabled) => enabled ? this.tokens.add(name) : this.tokens.delete(name),
   contains: name => this.tokens.has(name) };
  addEventListener(name, fn) { this.listeners[name] = fn; }
  setAttribute(name, value) { this.attrs[name] = value; }
  removeAttribute(name) { delete this.attrs[name]; }
  focus() { globalThis.document.activeElement = this; }
  querySelectorAll() { return [elements.first, elements.last]; }
 }
 for (const key of ['menu-toggle', 'sidebar-menu', 'menu-close', 'menu-backdrop', 'app-shell', 'first', 'last']) {
  elements[key] = new Element();
 }
 const breakpoint = { matches: true, listeners: {}, addEventListener(name, fn) { this.listeners[name] = fn; } };
 const bodyClasses = new Set();
 try {
  globalThis.window = { matchMedia: () => breakpoint };
  globalThis.document = { body: { classList: { toggle: (name, enabled) => enabled ? bodyClasses.add(name) : bodyClasses.delete(name) } },
   querySelector: selector => elements[selector.slice(1)] || null, addEventListener(name, fn) { this[name] = fn; },
   activeElement: null };
  await import('../wwwroot/js/navigation.js?responsive-test');
  assert.equal(elements['sidebar-menu'].inert, false, 'desktop sidebar stays keyboard accessible');
  elements['menu-toggle'].listeners.click();
  assert.equal(elements['app-shell'].inert, false, 'desktop menu does not cover content');
  breakpoint.matches = false;
  breakpoint.listeners.change();
  assert.equal(elements['sidebar-menu'].inert, true, 'closed mobile drawer is not focusable');
  elements['menu-toggle'].listeners.click();
  assert.equal(elements['sidebar-menu'].classList.contains('is-open'), true);
  assert.equal(elements['app-shell'].inert, true);
  assert.equal(elements['sidebar-menu'].attrs['aria-modal'], 'true');
  assert.equal(globalThis.document.activeElement, elements['menu-close']);
  elements.last.focus();
  let prevented = false;
  globalThis.document.keydown({ key: 'Tab', shiftKey: false, preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(globalThis.document.activeElement, elements.first);
  globalThis.document.keydown({ key: 'Escape', preventDefault() {} });
  assert.equal(elements['sidebar-menu'].classList.contains('is-open'), false);
  assert.equal(elements['app-shell'].inert, false);
  assert.equal(globalThis.document.activeElement, elements['menu-toggle']);
  assert.equal(bodyClasses.has('menu-open'), false);
 } finally {
  for (const [key, value] of Object.entries(saved)) {
   if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
  }
 }
});

test('theme switch uses the same mono interface in light and dark modes', async () => {
 const saved = { document: globalThis.document, window: globalThis.window, localStorage: globalThis.localStorage };
 const button = { hidden: true, attrs: {}, listeners: {}, setAttribute(name, value) { this.attrs[name] = value; },
  addEventListener(name, fn) { this.listeners[name] = fn; } };
 const root = { attrs: {}, style: {}, setAttribute(name, value) { this.attrs[name] = value; },
  getAttribute(name) { return this.attrs[name]; } };
 const system = { matches: false, addEventListener() {} };
 const data = new Map();
 try {
  globalThis.localStorage = { getItem: key => data.get(key), setItem: (key, value) => data.set(key, value) };
  globalThis.window = { matchMedia: () => system, addEventListener() {} };
  globalThis.document = { documentElement: root, listeners: {}, addEventListener(name, fn) { this.listeners[name] = fn; },
   querySelector: selector => selector === '#theme-toggle' ? button : null };
  await import('../wwwroot/js/theme.js?theme-control-test');
  globalThis.document.listeners.DOMContentLoaded();
  assert.equal(root.attrs['data-bs-theme'], 'light');
  assert.equal(button.textContent, 'Dark mode');
  button.listeners.click();
  assert.equal(root.attrs['data-bs-theme'], 'dark');
  assert.equal(button.textContent, 'Light mode');
  assert.equal(button.attrs['aria-pressed'], 'true');
  assert.equal(data.get('simplestackwatch.theme'), 'dark');
 } finally {
  for (const [key, value] of Object.entries(saved)) {
   if (value === undefined) delete globalThis[key]; else globalThis[key] = value;
  }
 }
});

const endpointFixtures = [
 { id: 'db', name: 'Database', kind: 'postgres', environment: 'DB', status: 'Down' },
 { id: 'a', name: 'Alpha', kind: 'http', environment: 'Local', status: 'Healthy', durationMs: 100, checkedAt: '2026-11-23T10:00:00Z' },
 { id: 'b', name: 'Beta', kind: 'http', environment: 'UAT', status: 'Down', durationMs: null, checkedAt: null },
 { id: 's3', name: 'AWS S3', kind: 'http', environment: 'Local', status: 'Healthy', durationMs: 9, checkedAt: '2026-11-23T11:00:00Z' },
 { id: 'c', name: 'Charlie', kind: 'http', environment: 'UAT', status: 'Degraded', durationMs: 0, checkedAt: '2026-11-23T09:00:00Z' },
 { id: 'd', name: 'Delta', kind: 'http', environment: 'UAT', status: 'Unknown', durationMs: null, checkedAt: null }
];

test('endpoint sorting is health-first, numeric and chronological; unknown measurements stay last', async () => {
 const { endpointRows } = await import('../wwwroot/js/overview.js');
 const ids = options => endpointRows(endpointFixtures, options).map(row => row.id);
 assert.deepEqual(ids(), ['b', 'c', 'd', 'a', 's3']);
 assert.deepEqual(ids({ sort: 'name', direction: 'desc' }), ['d', 'c', 'b', 's3', 'a']);
 assert.deepEqual(ids({ sort: 'durationMs' }), ['c', 's3', 'a', 'b', 'd']);
 assert.deepEqual(ids({ sort: 'durationMs', direction: 'desc' }), ['a', 's3', 'c', 'b', 'd']);
 assert.deepEqual(ids({ sort: 'checkedAt', direction: 'desc' }), ['s3', 'a', 'c', 'b', 'd']);
 assert.deepEqual(ids({ sort: 'checkedAt' }), ['c', 'a', 's3', 'b', 'd']);
 assert.deepEqual(ids({ environment: 'Local', status: 'Healthy', search: ' s3 ' }), ['s3']);
 assert.deepEqual(ids({ search: 'database' }), []);
 assert.equal(endpointFixtures[0].id, 'db');
});

test('endpoints page filters HTTP targets, sorts headers, keeps selections on refresh and opens history', async () => {
 const saved = { document: globalThis.document, fetch: globalThis.fetch, setInterval: globalThis.setInterval, Node: globalThis.Node };
 class Element {
  children = []; disabled = false; checked = true; hidden = false; dataset = {}; listeners = {}; attrs = {};
  classList = { add() {}, remove() {} }; textContent = ''; value = '';
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.children = items; }
  addEventListener(name, fn) { this.listeners[name] = fn; }
  setAttribute(name, value) { this.attrs[name] = value; }
  focus() { globalThis.document.activeElement = this; }
 }
 const ids = ['dashboard', 'content', 'error', 'refresh', 'auto-refresh', 'health-status', 'updated',
  'overview-environment', 'overview-summary', 'overview-history-section', 'overview-history-title',
  'overview-history-close', 'overview-history', 'overview-search', 'endpoint-status'];
 const elements = Object.fromEntries(ids.map(id => [id, new Element()]));
 elements.dashboard.dataset = { tab: 'endpoints', refresh: '10' };
 const paths = [];
 try {
  globalThis.Node = Element;
  globalThis.document = { hidden: false, activeElement: null, createElement: () => new Element(),
   querySelector: sel => sel === '#csrf input' ? { value: 'csrf' } : elements[sel.slice(1)] ?? null };
  globalThis.setInterval = () => 0;
  globalThis.fetch = async path => {
   paths.push(path);
   return { ok: true, status: 200, json: async () => path.includes('/history') ? [] : { targets: endpointFixtures } };
  };
  const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };
  await import('../wwwroot/js/dashboard.js?endpoints-test');
  await settle();
  const table = () => elements.content.children[0].children[0];
  const headers = () => table().children[0].children[0].children;
  const rows = () => table().children[1].children;
  const names = () => rows().map(row => row.children[0].textContent);
  assert.deepEqual(paths, ['/api/overview']);
  assert.deepEqual(names(), ['Beta', 'Charlie', 'Delta', 'Alpha', 'AWS S3']);
  assert.deepEqual(elements['overview-environment'].children.map(option => option.value), ['', 'Local', 'UAT']);
  assert.equal(headers()[3].attrs['aria-sort'], 'ascending');
  headers()[4].children[0].focus();
  headers()[4].children[0].listeners.click();
  assert.deepEqual(names(), ['Charlie', 'AWS S3', 'Alpha', 'Beta', 'Delta']);
  assert.equal(headers()[4].attrs['aria-sort'], 'ascending');
  assert.equal(globalThis.document.activeElement, headers()[4].children[0]);
  headers()[4].children[0].listeners.click();
  assert.deepEqual(names(), ['Alpha', 'AWS S3', 'Charlie', 'Beta', 'Delta']);
  assert.equal(headers()[4].attrs['aria-sort'], 'descending');
  elements['overview-environment'].value = 'Local';
  elements['overview-environment'].listeners.change();
  elements['endpoint-status'].value = 'Healthy';
  elements['endpoint-status'].listeners.change();
  assert.deepEqual(names(), ['Alpha', 'AWS S3']);
  elements.refresh.listeners.click(); await settle();
  assert.deepEqual(names(), ['Alpha', 'AWS S3']);
  assert.equal(elements['overview-environment'].value, 'Local');
  assert.equal(elements['endpoint-status'].value, 'Healthy');
  elements['overview-search'].value = 'S3'; elements['overview-search'].listeners.input();
  assert.deepEqual(names(), ['AWS S3']);
  rows()[0].children[7].children[0].listeners.click(); await settle();
  assert.equal(paths.at(-1), '/api/overview/history?targetId=s3&limit=50');
  assert.equal(elements['overview-history-section'].hidden, false);
  elements['overview-history-close'].listeners.click();
  assert.equal(elements['overview-history-section'].hidden, true);
  elements['overview-search'].value = 'missing'; elements['overview-search'].listeners.input();
  assert.equal(elements.content.children[0].textContent, 'No endpoints match your filters.');
 } finally {
  for (const [name, value] of Object.entries(saved)) {
   if (value === undefined) delete globalThis[name]; else globalThis[name] = value;
  }
 }
});
