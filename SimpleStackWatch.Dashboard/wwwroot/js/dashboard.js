import { number, bytes, date, parseCsv } from './format.js';

const root = document.querySelector('#dashboard');
const tab = root.dataset.tab;
const content = document.querySelector('#content');
const error = document.querySelector('#error');
const csrf = document.querySelector('#csrf input').value;
const databaseSelect = document.querySelector('#postgres-database');
const processView = document.querySelector('#process-view');
const counterProcess = document.querySelector('#counter-process');
const sampleButton = document.querySelector('#counter-sample');
const monitorEndpoint = document.querySelector('#monitor-endpoint');

let processRows = [];
let refreshing = false;
let sampling = false;
let monitorSampling = false;

function text(tag, value, className = '') {
  const element = document.createElement(tag);
  element.textContent = value == null ? '—' : String(value);
  element.className = className;
  return element;
}

function table(target, headers, rows, cellFactory) {
  target.replaceChildren();

  if (!rows.length) {
    target.append(text(
        'div',
        'No entries to show.',
        'card-body text-secondary'
    ));
    return;
  }

  const wrapper = document.createElement('div');
  wrapper.className = 'table-responsive';

  const grid = document.createElement('table');
  grid.className = 'table table-hover';

  const head = document.createElement('thead');
  const header = document.createElement('tr');

  headers.forEach(label => {
    const th = text('th', label);
    th.scope = 'col';
    header.append(th);
  });

  head.append(header);
  grid.append(head);

  const body = document.createElement('tbody');

  rows.forEach(row => {
    const tr = document.createElement('tr');

    cellFactory(row).forEach(value => {
      const td = document.createElement('td');

      if (value instanceof Node) {
        td.append(value);
      } else {
        td.textContent = value == null ? '—' : String(value);
      }

      tr.append(td);
    });

    body.append(tr);
  });

  grid.append(body);
  wrapper.append(grid);
  target.append(wrapper);
}

async function request(path, method = 'GET') {
  const controller = new AbortController();
  const timeout = setTimeout(
      () => controller.abort(),
      tab === 'ec2' ? 90000 : 65000
  );

  try {
    const response = await fetch(path, {
      method,
      credentials: 'same-origin',
      signal: controller.signal,
      headers: {
        Accept: 'application/json',
        ...(method !== 'GET' ? { 'X-CSRF-TOKEN': csrf } : {})
      }
    });

    if (response.status === 401) {
      location.assign('/account/login');
      throw new Error('Please sign in again.');
    }

    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new Error(
          problem.detail || `Request failed (${response.status}).`
      );
    }

    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

function message(target, value) {
  target.replaceChildren(text(
      'div',
      value,
      'card-body text-secondary'
  ));
}

function showError(value) {
  error.textContent = value;
  error.classList.remove('d-none');
}

function metric(point) {
  return point == null ? '—' : number(point.value);
}

function populateCounterProcesses() {
  const selected = counterProcess.value;
  counterProcess.replaceChildren();

  if (!processRows.length) {
    counterProcess.append(text('option', 'No accessible processes'));
  } else {
    for (const process of processRows) {
      const option = text(
          'option',
          `${process.name} (PID ${process.pid})`
      );

      option.value = String(process.pid);
      counterProcess.append(option);
    }

    if (processRows.some(process => String(process.pid) === selected)) {
      counterProcess.value = selected;
    }
  }

  counterProcess.disabled = sampling || !processRows.length;
  sampleButton.disabled = sampling || !processRows.length;
}

async function loadMonitor() {
  if (monitorSampling) return;

  const target = document.querySelector('#monitor-content');
  const controls = document.querySelector('#monitor-metric-controls');
  const select = document.querySelector('#monitor-application');
  const button = document.querySelector('#monitor-sample');
  const metrics = monitorEndpoint.value === 'metrics';

  controls.hidden = !metrics;

  if (!metrics) {
    message(target, 'Loading…');
  }

  try {
    // Selecting Metrics loads applications, never starts a metrics sample.
    const result = await request('/api/monitor/processes');
    const rows = result.text?.trim() ? JSON.parse(result.text) : [];

    if (!Array.isArray(rows)) {
      throw new Error('Unexpected process response from dotnet-monitor.');
    }

    if (!metrics) {
      table(
          target,
          ['PID', 'Application', 'Default', 'Runtime ID'],
          rows,
          process => [
            process.pid,
            process.name,
            process.isDefault ? 'Yes' : 'No',
            process.uid
          ]
      );

      return;
    }

    const previous = select.value;
    select.replaceChildren();

    for (const process of rows.filter(process => process.uid)) {
      const option = text(
          'option',
          `${process.name} (PID ${process.pid})`
      );

      option.value = process.uid;
      select.append(option);
    }

    if ([...select.options].some(option => option.value === previous)) {
      select.value = previous;
    }

    select.disabled = !select.options.length;
    button.disabled = !select.options.length;

    if (!select.options.length) {
      message(target, 'No applications are accessible to dotnet-monitor.');
    } else if (select.value !== previous) {
      message(target, 'Choose an application, then select Sample metrics.');
    }
  } catch (e) {
    select.disabled = true;
    button.disabled = true;
    message(target, e.message);
    throw e;
  }
}

function parseMonitorMetrics(raw) {
  if (!raw.trim()) return [];

  // JSON sequences may contain formatted, multiline JSON records.
  if (raw.includes('\u001e')) {
    return raw
        .split('\u001e')
        .filter(part => part.trim())
        .map(part => JSON.parse(part));
  }

  try {
    const value = JSON.parse(raw);
    return Array.isArray(value) ? value : [value];
  } catch {
    return raw
        .split(/\r?\n/)
        .filter(line => line.trim())
        .map(line => JSON.parse(line));
  }
}

async function sampleMonitorMetrics() {
  if (monitorSampling || refreshing) return;

  const select = document.querySelector('#monitor-application');

  if (!select.value || select.disabled) return;

  const button = document.querySelector('#monitor-sample');
  const target = document.querySelector('#monitor-content');
  const uid = select.value;
  const application = select.selectedOptions[0].textContent;

  const controls = [
    select,
    button,
    processView,
    monitorEndpoint,
    document.querySelector('#refresh')
  ];

  monitorSampling = true;
  controls.forEach(control => { control.disabled = true; });
  error.classList.add('d-none');

  message(target, `Sampling ${application} for 10 seconds…`);

  try {
    // The dashboard controller accepts POST with a selected runtime ID.
    const result = await request(
        `/api/monitor/metrics?uid=${encodeURIComponent(uid)}`,
        'POST'
    );

    const samples = parseMonitorMetrics(result.text ?? '');
    const latest = new Map();

    for (const metric of samples) {
      const key = JSON.stringify([
        metric.provider,
        metric.name,
        metric.tags ?? ''
      ]);

      const previous = latest.get(key);

      if (
          !previous ||
          new Date(metric.timestamp) >= new Date(previous.timestamp)
      ) {
        latest.set(key, metric);
      }
    }

    if (!latest.size) {
      message(
          target,
          'No metrics were collected for this application. Check its runtime diagnostics and monitor provider settings.'
      );
    } else {
      table(
          target,
          ['Metric', 'Value', 'Unit', 'Provider', 'Tags', 'Time'],
          [...latest.values()],
          metric => [
            metric.displayName || metric.name,
            number(metric.value),
            metric.unit || '—',
            metric.provider,
            metric.tags || '—',
            date(metric.timestamp)
          ]
      );

      target.prepend(text(
          'div',
          `${application} · Latest values from a 10-second sample`,
          'card-body border-bottom small text-secondary'
      ));
    }

    document.querySelector('#updated').textContent =
        `Updated ${date(new Date())}`;
  } catch (e) {
    message(target, e.message);
  } finally {
    monitorSampling = false;
    controls.forEach(control => { control.disabled = false; });
  }
}

async function refresh() {
  // Avoid changing applications or fetching lists during a live sample.
  if (tab === 'processes' && monitorSampling) return;

  if (
      refreshing ||
      tab === 'logs' ||
      (databaseSelect && (
          databaseSelect.disabled || !databaseSelect.options.length
      ))
  ) {
    return;
  }

  refreshing = true;
  document.querySelector('#refresh').disabled = true;

  if (processView) processView.disabled = true;
  if (monitorEndpoint) monitorEndpoint.disabled = true;
  if (databaseSelect) databaseSelect.disabled = true;

  error.classList.add('d-none');

  const selection = databaseSelect?.value;
  const query = tab === 'postgres'
      ? `?database=${encodeURIComponent(selection)}`
      : '';

  try {
    if (tab === 'processes' && processView.value === 'monitor') {
      await loadMonitor();

      document.querySelector('#updated').textContent =
          `Updated ${date(new Date())}`;

      return;
    }

    const data = await request(`/api/${tab}${query}`);

    if (data.configured === false) {
      message(
          content,
          'This service is not configured. Set its connection details on the server.'
      );
    } else if (tab === 'processes') {
      processRows = data;

      table(
          content,
          ['PID', 'Application', 'CPU %', 'Memory', 'Threads'],
          data,
          process => [
            process.pid,
            process.name,
            number(process.cpuPercent),
            bytes(process.memoryBytes),
            process.threads
          ]
      );

      populateCounterProcesses();
    } else if (tab === 'postgres') {
      const columns = [
        'PID', 'User', 'Database', 'Application',
        'Client', 'State', 'Wait', 'Query age (s)'
      ];

      if (data.queryTextEnabled) columns.push('Query');

      table(content, columns, data.rows, process => {
        const cells = [
          process.pid,
          process.user,
          process.database,
          process.application,
          process.client,
          process.state,
          [process.waitType, process.wait].filter(Boolean).join(' / '),
          number(process.queryAgeSeconds)
        ];

        if (data.queryTextEnabled) {
          cells.push(text('div', process.query, 'cell-query'));
        }

        return cells;
      });
    } else if (tab === 'redis') {
      const rows = data.nodes.flatMap(node =>
          Object.entries(node.values).map(([name, value]) => ({
            node: node.endpoint,
            name,
            value
          }))
      );

      table(
          content,
          ['Node', 'Metric', 'Value'],
          rows,
          row => [row.node, row.name, row.value]
      );

      content.prepend(text(
          'div',
          `Ping: ${number(data.latencyMs)} ms`,
          'card-body border-bottom small text-secondary'
      ));
    } else if (tab === 'ec2') {
      table(
          content,
          [
            'Instance', 'Name', 'State', 'Type', 'Private IP', 'CPU %',
            'Network in (bytes)', 'Network out (bytes)',
            'Failed checks', 'Datapoint time'
          ],
          data.instances,
          instance => [
            instance.id,
            instance.name,
            instance.state,
            instance.type,
            instance.address,
            metric(instance.metrics.CPUUtilization),
            metric(instance.metrics.NetworkIn),
            metric(instance.metrics.NetworkOut),
            metric(instance.metrics.StatusCheckFailed),
            date(instance.metrics.CPUUtilization?.at)
          ]
      );
    }

    document.querySelector('#updated').textContent =
        `Updated ${date(new Date())}`;
  } catch (e) {
    showError(e.message);
  } finally {
    if (tab !== 'postgres' && tab !== 'redis') {
      refreshing = false;
      document.querySelector('#refresh').disabled = false;

      if (processView) processView.disabled = false;
      if (monitorEndpoint) monitorEndpoint.disabled = false;
    }
  }

  if (tab === 'postgres' || tab === 'redis') {
    try {
      const health = await request(`/api/health/${tab}${query}`);
      const badge = document.querySelector('#health-status');

      badge.textContent = health.configured
          ? `${health.status} · ${number(health.durationMs)} ms`
          : '';

      badge.className =
          `badge ${health.status === 'Healthy' ? 'text-bg-success' : 'text-bg-danger'}`;
    } catch {
      document.querySelector('#health-status').textContent =
          'Health unavailable';
    }
  }

  refreshing = false;
  document.querySelector('#refresh').disabled = false;

  if (databaseSelect) databaseSelect.disabled = false;
}

async function sample(pid) {
  if (sampling || !pid) return;

  sampling = true;
  sampleButton.disabled = true;
  counterProcess.disabled = true;

  const target = document.querySelector('#counter-content');
  message(target, `Sampling PID ${pid}…`);

  try {
    const result = await request(`/api/processes/${pid}/counters`, 'POST');
    const rows = parseCsv(result.csv);

    if (!rows.length) {
      message(
          target,
          'No counters returned. Check runtime diagnostics configuration.'
      );
    } else {
      table(
          target,
          rows[0],
          rows.slice(1),
          row => row.map((value, column) =>
              rows[0][column].trim().toLowerCase() === 'timestamp'
                  ? date(value)
                  : value
          )
      );
    }
  } catch (e) {
    message(target, e.message);
  } finally {
    sampling = false;
    populateCounterProcesses();
  }
}

if (tab === 'processes') {
  processView.addEventListener('change', () => {
    document.querySelector('#process-panel').hidden =
        processView.value !== 'processes';

    document.querySelector('#counter-panel').hidden =
        processView.value !== 'counters';

    const monitorPanel = document.querySelector('#monitor-panel');

    if (monitorPanel) {
      monitorPanel.hidden = processView.value !== 'monitor';
    }

    document.querySelector('#health-status').textContent = '';
    document.querySelector('#updated').textContent = '';

    refresh();
  });

  monitorEndpoint?.addEventListener('change', () => {
    const select = document.querySelector('#monitor-application');

    if (monitorEndpoint.value === 'metrics') {
      select.value = '';
    }

    message(document.querySelector('#monitor-content'), 'Loading…');
    refresh();
  });

  document.querySelector('#monitor-application')
      ?.addEventListener('change', () => {
        message(
            document.querySelector('#monitor-content'),
            'Choose Sample metrics to collect data for this application.'
        );
      });

  document.querySelector('#monitor-sample')
      ?.addEventListener('click', sampleMonitorMetrics);

  sampleButton.addEventListener('click', () =>
      sample(Number(counterProcess.value))
  );
}

if (tab === 'logs') {
  document.querySelector('#log-folder')?.addEventListener('change', event => {
    const url = event.target.value;
    document.querySelector('#file-logs').dataset.src = url;
    document.querySelector('#log-frame').src = url;
  });

  document.querySelectorAll('.log-source').forEach(button =>
      button.addEventListener('click', () => {
        document.querySelector('#log-frame').src = button.dataset.src;
      })
  );
} else {
  document.querySelector('#refresh').addEventListener('click', refresh);

  setInterval(() => {
    if (!document.hidden && document.querySelector('#auto-refresh').checked) {
      refresh();
    }
  }, Number(root.dataset.refresh) * 1000);

  if (tab === 'postgres') {
    // Load configured names before querying a database index.
    request('/api/postgres/databases').then(databases => {
      databaseSelect.replaceChildren();

      for (const database of databases) {
        const option = text('option', database.name);
        option.value = database.index;
        databaseSelect.append(option);
      }

      if (!databases.length) {
        databaseSelect.append(text('option', 'No databases configured'));

        message(
            content,
            'Add PostgreSQL database names and connection strings in environment variables.'
        );

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
  } else {
    refresh();
  }
}