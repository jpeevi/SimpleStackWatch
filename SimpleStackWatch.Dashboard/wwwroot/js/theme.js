(() => {
  const key = 'simplestackwatch.theme';
  const root = document.documentElement;
  const system = window.matchMedia('(prefers-color-scheme: dark)');
  const valid = value => value === 'light' || value === 'dark';
  let preference = null;
  try {
    const stored = localStorage.getItem(key);
    if (valid(stored)) preference = stored;
  } catch { /* Use the system preference when storage is unavailable. */ }

  const apply = () => {
    const theme = preference || (system.matches ? 'dark' : 'light');
    root.setAttribute('data-bs-theme', theme);
    root.style.colorScheme = theme;
    const button = document.querySelector('#theme-toggle');
    if (button) {
      button.hidden = false;
      button.textContent = 'Dark mode';
      button.setAttribute('aria-pressed', String(theme === 'dark'));
    }
  };

  // Apply before styles load to avoid flashing the wrong theme.
  apply();
  document.addEventListener('DOMContentLoaded', () => {
    apply();
    document.querySelector('#theme-toggle')?.addEventListener('click', () => {
      preference = root.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem(key, preference); } catch { /* Keep the preference for this page. */ }
      apply();
    });
  });
  system.addEventListener('change', () => { if (!preference) apply(); });
  window.addEventListener('storage', event => {
    if (event.key !== key && event.key !== null) return;
    preference = valid(event.newValue) ? event.newValue : null;
    apply();
  });
})();
