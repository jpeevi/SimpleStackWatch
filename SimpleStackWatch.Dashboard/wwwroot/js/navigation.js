const toggle = document.querySelector('#menu-toggle');
const menu = document.querySelector('#sidebar-menu');
const close = document.querySelector('#menu-close');
const backdrop = document.querySelector('#menu-backdrop');
const shell = document.querySelector('#app-shell');

if (toggle && menu && close && backdrop && shell) {
  const setOpen = open => {
    menu.hidden = !open;
    backdrop.hidden = !open;
    toggle.setAttribute('aria-expanded', String(open));
    shell.inert = open;
    document.body.classList.toggle('menu-open', open);
    (open ? close : toggle).focus();
  };
  toggle.addEventListener('click', () => setOpen(menu.hidden));
  close.addEventListener('click', () => setOpen(false));
  backdrop.addEventListener('click', () => setOpen(false));
  document.addEventListener('keydown', event => {
    if (menu.hidden) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      setOpen(false);
    } else if (event.key === 'Tab') {
      const items = Array.from(menu.querySelectorAll('button, a[href]'));
      const first = items[0], last = items[items.length - 1];
      if (event.shiftKey && document.activeElement === first) {
        event.preventDefault(); last.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault(); first.focus();
      }
    }
  });
}
