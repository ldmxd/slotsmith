// Minimal, dependency-free booking flow: services -> professional -> time -> details -> confirmed.
// Mirrors the Fresha flow this is meant to replace. Talks to the API defined in Program.cs.

const state = {
  step: 'services',
  categories: [],
  staff: [],
  selectedServiceIds: new Set(),
  selectedStaffId: null, // null = not chosen yet, 0 = "no preference"
  selectedDate: new Date().toISOString().slice(0, 10),
  slots: [],
  selectedSlot: null,
  customer: { name: '', email: '', phone: '', notes: '' },
  lastBooking: null,
};

const app = document.getElementById('app');
const cartBar = document.getElementById('cartBar');
const cartPrice = document.getElementById('cartPrice');
const cartMeta = document.getElementById('cartMeta');
const continueBtn = document.getElementById('continueBtn');
const backBtn = document.getElementById('backBtn');

const STEP_ORDER = ['services', 'professional', 'time', 'details', 'confirmed'];

function fmtMoney(cents) {
  return '$' + (cents / 100).toFixed(2).replace(/\.00$/, '');
}

function initials(name) {
  return name.split(' ').map(w => w[0]).join('').slice(0, 2).toUpperCase();
}

async function api(path, opts) {
  const resp = await fetch(path, opts);
  if (!resp.ok) throw new Error(await resp.text());
  return resp.status === 204 ? null : resp.json();
}

async function loadCatalogue() {
  const [categories, staff] = await Promise.all([
    api('/api/services'),
    api('/api/staff'),
  ]);
  state.categories = categories;
  state.staff = staff;
}

function selectedServices() {
  const flat = state.categories.flatMap(c => c.services ?? c.Services ?? []);
  return flat.filter(s => state.selectedServiceIds.has(s.serviceId ?? s.ServiceId));
}

function cartSummary() {
  const items = selectedServices();
  const totalMinutes = items.reduce((sum, s) => sum + (s.durationMinutes ?? s.DurationMinutes), 0);
  const totalPrice = items.reduce((sum, s) => sum + (s.priceDollars ?? s.PriceDollars) * 100, 0);
  return { count: items.length, totalMinutes, totalPrice };
}

function updateCartBar() {
  const { count, totalMinutes, totalPrice } = cartSummary();
  if (count === 0 && state.step === 'services') {
    cartBar.style.display = 'none';
    return;
  }
  cartBar.style.display = 'flex';
  cartPrice.textContent = 'from ' + fmtMoney(totalPrice);
  cartMeta.textContent = `${count} item${count === 1 ? '' : 's'} • ${totalMinutes} mins`;
  continueBtn.disabled = state.step === 'services' && count === 0;
  continueBtn.textContent = state.step === 'details' ? 'Confirm booking →' : 'Continue →';
}

function goToStep(step) {
  state.step = step;
  render();
}

function goBack() {
  const idx = STEP_ORDER.indexOf(state.step);
  if (idx > 0) goToStep(STEP_ORDER[idx - 1]);
}

backBtn.addEventListener('click', goBack);
document.getElementById('closeBtn').addEventListener('click', () => {
  state.selectedServiceIds.clear();
  state.selectedStaffId = null;
  state.selectedSlot = null;
  goToStep('services');
});

continueBtn.addEventListener('click', async () => {
  if (state.step === 'services') return goToStep('professional');
  if (state.step === 'professional') {
    await loadAvailability();
    return goToStep('time');
  }
  if (state.step === 'time') {
    if (!state.selectedSlot) return;
    return goToStep('details');
  }
  if (state.step === 'details') {
    return submitBooking();
  }
});

// ── Step: select services ───────────────────────────────────────────────

function renderServicesStep() {
  const el = document.createElement('div');
  el.appendChild(h1('Select services'));

  for (const cat of state.categories) {
    const services = cat.services ?? cat.Services ?? [];
    if (services.length === 0) continue;
    el.appendChild(h2(cat.name ?? cat.Name));
    for (const s of services) {
      const id = s.serviceId ?? s.ServiceId;
      const name = s.name ?? s.Name;
      const duration = s.durationMinutes ?? s.DurationMinutes;
      const price = s.priceDollars ?? s.PriceDollars;
      const fromPrefix = (s.priceIsFrom ?? s.PriceIsFrom) ? 'from ' : '';

      const card = document.createElement('div');
      card.className = 'card selectable' + (state.selectedServiceIds.has(id) ? ' selected' : '');
      card.innerHTML = `
        <div class="checkbox-dot ${state.selectedServiceIds.has(id) ? 'checked' : ''}"></div>
        <div class="card-body">
          <div class="card-title">${name}</div>
          <div class="card-sub">${duration} min &middot; ${fromPrefix}${fmtMoney(price * 100)}</div>
        </div>`;
      card.addEventListener('click', () => {
        if (state.selectedServiceIds.has(id)) state.selectedServiceIds.delete(id);
        else state.selectedServiceIds.add(id);
        render();
      });
      el.appendChild(card);
    }
  }
  return el;
}

// ── Step: select professional ───────────────────────────────────────────

function renderProfessionalStep() {
  const el = document.createElement('div');
  el.appendChild(h1('Select professional'));

  const noPref = document.createElement('div');
  noPref.className = 'card selectable' + (state.selectedStaffId === 0 ? ' selected' : '');
  noPref.innerHTML = `
    <div class="avatar">&#8646;</div>
    <div class="card-body">
      <div class="card-title">No preference</div>
      <div class="card-sub">Maximum availability</div>
    </div>`;
  noPref.addEventListener('click', () => { state.selectedStaffId = 0; render(); });
  el.appendChild(noPref);

  el.appendChild(h2('All professionals'));
  for (const s of state.staff) {
    const id = s.staffId ?? s.StaffId;
    const name = s.displayName ?? s.DisplayName;
    const card = document.createElement('div');
    card.className = 'card selectable' + (state.selectedStaffId === id ? ' selected' : '');
    card.innerHTML = `
      <div class="avatar">${initials(name)}</div>
      <div class="card-body">
        <div class="card-title">${name}</div>
      </div>`;
    card.addEventListener('click', () => { state.selectedStaffId = id; render(); });
    el.appendChild(card);
  }
  return el;
}

// ── Step: select time ────────────────────────────────────────────────────

async function loadAvailability() {
  const body = {
    serviceIds: Array.from(state.selectedServiceIds),
    staffId: state.selectedStaffId || null,
    date: state.selectedDate,
  };
  state.slots = await api('/api/availability', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

function renderTimeStep() {
  const el = document.createElement('div');
  el.appendChild(h1('Select a time'));

  const dateInput = document.createElement('input');
  dateInput.type = 'date';
  dateInput.value = state.selectedDate;
  dateInput.style.margin = '0 20px 16px';
  dateInput.addEventListener('change', async () => {
    state.selectedDate = dateInput.value;
    state.selectedSlot = null;
    await loadAvailability();
    render();
  });
  el.appendChild(dateInput);

  if (state.slots.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty-state';
    empty.textContent = 'No availability that day — try another date.';
    el.appendChild(empty);
    return el;
  }

  const grid = document.createElement('div');
  grid.className = 'slots-grid';
  for (const slot of state.slots) {
    const start = new Date(slot.startUtc ?? slot.StartUtc);
    const btn = document.createElement('button');
    btn.className = 'slot-btn' + (state.selectedSlot === slot ? ' selected' : '');
    btn.textContent = start.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    btn.addEventListener('click', () => { state.selectedSlot = slot; render(); });
    grid.appendChild(btn);
  }
  el.appendChild(grid);
  return el;
}

// ── Step: details + confirm ─────────────────────────────────────────────

function renderDetailsStep() {
  const el = document.createElement('div');
  el.appendChild(h1('Your details'));

  const form = document.createElement('form');
  form.className = 'details-form';
  form.innerHTML = `
    <label for="field-name">Full name</label>
    <input id="field-name" name="name" autocomplete="name" required value="${state.customer.name}">

    <label for="field-email">Email</label>
    <input id="field-email" name="email" type="email" autocomplete="email" required value="${state.customer.email}">

    <label for="field-phone">Phone (optional)</label>
    <input id="field-phone" name="phone" type="tel" autocomplete="tel" value="${state.customer.phone}">

    <label for="field-notes">Notes for your stylist (optional)</label>
    <textarea id="field-notes" name="notes" rows="3">${state.customer.notes}</textarea>
  `;
  form.addEventListener('input', () => {
    state.customer.name = form.name.value;
    state.customer.email = form.email.value;
    state.customer.phone = form.phone.value;
    state.customer.notes = form.notes.value;
  });
  form.addEventListener('submit', e => e.preventDefault());
  el.appendChild(form);
  return el;
}

async function submitBooking() {
  const staffIdForSlot = state.selectedSlot.staffId ?? state.selectedSlot.StaffId;
  const body = {
    staffId: staffIdForSlot,
    customerName: state.customer.name,
    customerEmail: state.customer.email,
    customerPhone: state.customer.phone,
    startUtc: state.selectedSlot.startUtc ?? state.selectedSlot.StartUtc,
    items: Array.from(state.selectedServiceIds).map(id => ({ serviceId: id })),
    notes: state.customer.notes,
  };
  try {
    state.lastBooking = await api('/api/bookings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    goToStep('confirmed');
  } catch (err) {
    alert('Something went wrong booking that slot: ' + err.message);
  }
}

function renderConfirmedStep() {
  const el = document.createElement('div');
  const box = document.createElement('div');
  box.className = 'confirm-box';
  const start = new Date(state.lastBooking?.startUtc ?? state.lastBooking?.StartUtc);
  box.innerHTML = `
    <h1>You're booked ✓</h1>
    <p>${start.toLocaleString([], { weekday: 'long', day: 'numeric', month: 'long', hour: 'numeric', minute: '2-digit' })}</p>
    <p>A confirmation has been sent to ${state.customer.email}.</p>
  `;
  el.appendChild(box);
  return el;
}

function h1(text) { const e = document.createElement('h1'); e.className = 'step-title'; e.textContent = text; return e; }
function h2(text) { const e = document.createElement('h2'); e.className = 'section-title'; e.textContent = text; return e; }

function render() {
  app.innerHTML = '';
  backBtn.style.visibility = state.step === 'services' ? 'hidden' : 'visible';

  if (state.step === 'services') app.appendChild(renderServicesStep());
  else if (state.step === 'professional') app.appendChild(renderProfessionalStep());
  else if (state.step === 'time') app.appendChild(renderTimeStep());
  else if (state.step === 'details') app.appendChild(renderDetailsStep());
  else if (state.step === 'confirmed') app.appendChild(renderConfirmedStep());

  if (state.step === 'confirmed') {
    cartBar.style.display = 'none';
  } else {
    updateCartBar();
    continueBtn.disabled =
      (state.step === 'services' && state.selectedServiceIds.size === 0) ||
      (state.step === 'professional' && state.selectedStaffId === null) ||
      (state.step === 'time' && !state.selectedSlot) ||
      (state.step === 'details' && (!state.customer.name || !state.customer.email));
  }
}

loadCatalogue().then(render).catch(err => {
  app.innerHTML = `<div class="empty-state">Couldn't load the booking page: ${err.message}</div>`;
});
