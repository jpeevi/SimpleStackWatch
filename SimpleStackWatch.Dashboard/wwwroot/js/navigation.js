const toggle = document.querySelector('#menu-toggle');
const menu = document.querySelector('#sidebar-menu');
const close = document.querySelector('#menu-close');
const backdrop = document.querySelector('#menu-backdrop');
const shell = document.querySelector('#app-shell');

if (toggle && menu && close && backdrop && shell) {
  const desktop = window.matchMedia('(min-width: 992px)');
  let open = false;

  const sync = () => {
    const drawerOpen = !desktop.matches && open;
    menu.classList.toggle('is-open', drawerOpen);
    menu.inert = !desktop.matches && !drawerOpen;
    backdrop.hidden = !drawerOpen;
    shell.inert = drawerOpen;
    document.body.classList.toggle('menu-open', drawerOpen);
    toggle.setAttribute('aria-expanded', String(drawerOpen));
    if (drawerOpen) {
      menu.setAttribute('role', 'dialog');
      menu.setAttribute('aria-modal', 'true');
    } else {
      menu.removeAttribute('role');
      menu.removeAttribute('aria-modal');
    }
  };

  const setOpen = value => {
    open = value && !desktop.matches;
    sync();
    if (open) close.focus();
    else if (!desktop.matches) toggle.focus();
  };

  toggle.addEventListener('click', () => setOpen(!open));
  close.addEventListener('click', () => setOpen(false));
  backdrop.addEventListener('click', () => setOpen(false));
  desktop.addEventListener('change', () => { open = false; sync(); });

  document.addEventListener('keydown', event => {
    if (!open || desktop.matches) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      setOpen(false);
    } else if (event.key === 'Tab') {
      const items = Array.from(menu.querySelectorAll('a[href], button:not([disabled])'));
      const first = items[0];
      const last = items[items.length - 1];
      if (first && last && event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
      } else if (first && last && !event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }
  });

  sync();
}
