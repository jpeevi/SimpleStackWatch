// Appended to Serilog.Viewer 1.3.2's module by ViewerFrontendAssets.
// Ot and d are that module's existing React and JSX imports.
function stackwatchDateFromIso(value) {
  if (!value) return '';
  const parts = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/.exec(value);
  return parts ? `${parts[3]}/${parts[2]}/${parts[1]} ${parts[4]}:${parts[5]}:${parts[6] || '00'}` : '';
}

function stackwatchDateToIso(value) {
  if (!value.trim()) return '';
  const parts = /^(\d{2})\/(\d{2})\/(\d{4}) (\d{2}):(\d{2}):(\d{2})$/.exec(value.trim());
  if (!parts) return null;
  const [, day, month, year, hour, minute, second] = parts;
  const y = Number(year), m = Number(month), n = Number(day);
  if (y < 1000 || m < 1 || m > 12 || n < 1 ||
      n > new Date(Date.UTC(y, m, 0)).getUTCDate() ||
      Number(hour) > 23 || Number(minute) > 59 || Number(second) > 59) return null;
  return `${year}-${month}-${day}T${hour}:${minute}:${second}`;
}

function StackWatchDateInput(props) {
  const [text, setText] = Ot.useState(stackwatchDateFromIso(props.value));
  Ot.useEffect(() => setText(stackwatchDateFromIso(props.value)), [props.value]);
  return d.jsx('input', {
    ...props,
    type: 'text',
    placeholder: 'dd/MM/yyyy HH:mm:ss',
    'aria-label': `${props.title || 'Date and time'} (dd/MM/yyyy HH:mm:ss)`,
    value: text,
    onChange: event => {
      setText(event.target.value);
      const iso = stackwatchDateToIso(event.target.value);
      event.target.setCustomValidity(iso === null ? 'Use dd/MM/yyyy HH:mm:ss with a valid date and time.' : '');
      if (iso !== null) props.onChange({ target: { value: iso } });
    },
    onBlur: event => event.target.reportValidity()
  });
}
