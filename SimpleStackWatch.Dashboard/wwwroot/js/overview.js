export function statusClass(status) {
  switch (status) {
    case 'Healthy': return 'status-tag status-healthy';
    case 'Degraded': return 'status-tag status-degraded';
    case 'Down': return 'status-tag status-down';
    default: return 'status-tag status-unknown';
  }
}

export function summarizeTargets(targets) {
  return {
    total: targets.length,
    healthy: targets.filter(target => target.status === 'Healthy').length,
    degraded: targets.filter(target => target.status === 'Degraded').length,
    down: targets.filter(target => target.status === 'Down').length,
    unknown: targets.filter(target => target.status === 'Unknown').length
  };
}

// The dedicated endpoint view includes application health and reachability checks.
const statusOrder = { Down: 0, Degraded: 1, Unknown: 2, Healthy: 3 };

export function endpointRows(targets, { environment = '', status = '', search = '',
  sort = 'status', direction = 'asc' } = {}) {
  const query = search.trim().toLocaleLowerCase();
  const compareText = (a, b) => String(a ?? '').localeCompare(String(b ?? ''), undefined, { sensitivity: 'base' });
  const multiplier = direction === 'desc' ? -1 : 1;
  const numericValue = row => sort === 'checkedAt'
    ? (row.checkedAt == null || row.checkedAt === '' ? NaN : Date.parse(row.checkedAt))
    : (row.durationMs == null ? NaN : row.durationMs);
  return targets.filter(row => row.kind === 'http')
      .filter(row => !environment || row.environment === environment)
      .filter(row => !status || row.status === status)
      .filter(row => !query || String(row.name ?? '').toLocaleLowerCase().includes(query))
      .sort((a, b) => {
        let comparison = 0;
        if (sort === 'durationMs' || sort === 'checkedAt') {
          const left = numericValue(a), right = numericValue(b);
          // Missing measurements stay at the bottom in both directions.
          if (Number.isFinite(left) !== Number.isFinite(right)) return Number.isFinite(left) ? -1 : 1;
          if (Number.isFinite(left)) comparison = left - right;
        } else if (sort === 'name') {
          comparison = compareText(a.name, b.name);
        } else {
          comparison = (statusOrder[a.status] ?? 2) - (statusOrder[b.status] ?? 2);
        }
        return comparison * multiplier || compareText(a.name, b.name) || compareText(a.environment, b.environment)
          || String(a.id).localeCompare(String(b.id));
      });
}
