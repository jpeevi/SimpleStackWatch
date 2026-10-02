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
