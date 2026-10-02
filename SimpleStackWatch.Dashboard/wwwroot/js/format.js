export const number = value => value == null ? '—' : Number(value).toLocaleString(undefined, { maximumFractionDigits: 2 });
export const bytes = value => value == null ? '—' : `${number(Number(value) / 1024 / 1024)} MiB`;
export function date(value) {
  if (value == null || value === '') return '—';
  const instant = new Date(value);
  if (Number.isNaN(instant.getTime())) return '—';
  const pad = part => String(part).padStart(2, '0');
  return `${pad(instant.getDate())}/${pad(instant.getMonth() + 1)}/${instant.getFullYear()} ${pad(instant.getHours())}:${pad(instant.getMinutes())}:${pad(instant.getSeconds())}`;
}
export function parseCsv(text) {
  const rows = []; let row = []; let cell = ''; let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (c === '"') {
      if (quoted && text[i + 1] === '"') { cell += '"'; i++; }
      else quoted = !quoted;
    } else if (c === ',' && !quoted) { row.push(cell); cell = ''; }
    else if (c === '\n' && !quoted) { row.push(cell.replace(/\r$/, '')); if (row.some(Boolean)) rows.push(row); row = []; cell = ''; }
    else cell += c;
  }
  if (cell || row.length) { row.push(cell.replace(/\r$/, '')); rows.push(row); }
  return rows;
}
