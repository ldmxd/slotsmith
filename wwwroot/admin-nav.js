// Shared nav + logout for the admin pages (bookings-admin.html, admin.html,
// services-admin.html, staff-admin.html) — one place to keep the link list and logout behaviour
// in sync instead of duplicating both across every page. Injects into <div id="adminNav"></div>
// in the topbar.
(function () {
  const pages = [
    { href: '/bookings-admin.html', label: 'Bookings' },
    { href: '/staff-admin.html', label: 'Staff' },
    { href: '/services-admin.html', label: 'Prices' },
    { href: '/admin.html', label: 'Calendars' },
  ];

  const target = document.getElementById('adminNav');
  if (!target) return;

  const nav = document.createElement('nav');
  nav.className = 'admin-nav';
  nav.innerHTML = pages
    .map(p => `<a href="${p.href}" class="${location.pathname === p.href ? 'active' : ''}">${p.label}</a>`)
    .join('') + `<a href="#" class="logout-link" id="sharedLogoutLink">Log out</a>`;

  target.appendChild(nav);

  document.getElementById('sharedLogoutLink').onclick = async (e) => {
    e.preventDefault();
    await fetch('/api/admin/logout', { method: 'POST' });
    location.href = '/admin-login.html';
  };
})();
