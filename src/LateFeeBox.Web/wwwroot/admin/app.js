'use strict';

const state = {
  csrfToken: null,
  username: null,
  unit: 'تومان',
  page: 'dashboard',
  groupId: null,
  groups: [],
  members: [],
  payments: []
};

const $ = (id) => document.getElementById(id);
const pageMeta = {
  dashboard: ['داشبورد', 'وضعیت لحظه‌ای اعضا، بدهی‌ها و پرداخت‌ها'],
  members: ['اعضا و بدهی‌ها', 'مدیریت اعضا، بدهی و جریمه‌ها'],
  observed: ['کاربران دیده‌شده', 'اتصال کاربران گروه بله به اعضای صندوق'],
  fund: ['گردش صندوق', 'تنظیم موجودی و ثبت ورودی یا هزینه'],
  payments: ['پرداخت‌ها', 'درخواست‌ها و تراکنش‌های کیف پول بله'],
  settings: ['تنظیمات', 'ارتباط بات و امنیت حساب مدیر']
};

async function api(path, options = {}) {
  const method = (options.method || 'GET').toUpperCase();
  const headers = { ...(options.headers || {}) };
  const request = { credentials: 'same-origin', ...options, method, headers };

  if (request.body && typeof request.body !== 'string') {
    headers['Content-Type'] = 'application/json';
    request.body = JSON.stringify(request.body);
  }
  if (!['GET', 'HEAD'].includes(method) && state.csrfToken) {
    headers['X-CSRF-TOKEN'] = state.csrfToken;
  }

  let response;
  try {
    response = await fetch(path, request);
  } catch {
    throw new Error('ارتباط با سامانه برقرار نشد. وضعیت سرویس را بررسی کنید.');
  }

  if (response.status === 401) {
    showLogin();
    throw new Error('نشست شما پایان یافته است. دوباره وارد شوید.');
  }

  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) {
    throw new Error(data?.message || data?.detail || data?.title || 'انجام عملیات ناموفق بود.');
  }
  return data;
}

function showToast(message, type = 'success') {
  const toast = $('toast');
  toast.textContent = message;
  toast.className = `toast ${type}`;
  toast.hidden = false;
  clearTimeout(showToast.timer);
  showToast.timer = setTimeout(() => { toast.hidden = true; }, 4200);
}

function showLogin() {
  $('loginView').hidden = false;
  $('appView').hidden = true;
  state.csrfToken = null;
  closeSidebar();
}

function showApp() {
  $('loginView').hidden = true;
  $('appView').hidden = false;
  $('currentAdmin').textContent = state.username || 'مدیر';
}

function formatNumber(value) {
  return new Intl.NumberFormat('fa-IR').format(Number(value || 0));
}

function formatMoney(value) {
  return `${formatNumber(value)} ${state.unit}`;
}

function formatDate(value) {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('fa-IR', { dateStyle: 'medium', timeStyle: 'short' }).format(date);
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, (char) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[char]);
}

function firstLetter(name) {
  const value = String(name || '').trim();
  return escapeHtml(value ? value[0] : 'ک');
}

function statusBadge(status) {
  const map = {
    Paid: ['پرداخت‌شده', 'success'],
    Pending: ['در انتظار پرداخت', 'warning'],
    Approved: ['در حال پرداخت', 'info'],
    Rejected: ['ناموفق', 'danger'],
    Expired: ['منقضی', 'danger']
  };
  const [label, cls] = map[status] || [status || 'نامشخص', ''];
  return `<span class="badge ${cls}">${escapeHtml(label)}</span>`;
}

function kindLabel(kind) {
  return ({
    OpeningBalance: 'موجودی اولیه',
    ManualAdjustment: 'اصلاح دستی',
    Payment: 'پرداخت جریمه',
    Expense: 'هزینه',
    Refund: 'بازپرداخت'
  })[kind] || kind;
}

function emptyRow(colspan, title, description = '') {
  return `<tr><td colspan="${colspan}" class="empty-state"><strong>${escapeHtml(title)}</strong>${description ? escapeHtml(description) : ''}</td></tr>`;
}

function memberRow(member) {
  const secondary = member.baleUsername ? `@${escapeHtml(member.baleUsername)}` : (member.baleUserId || 'بدون اتصال بله');
  return `<tr>
    <td><div class="member-cell"><span class="member-avatar">${firstLetter(member.displayName)}</span><div><strong>${escapeHtml(member.displayName)}</strong><small>${secondary}</small></div></div></td>
    <td>${member.baleUserId ?? '—'}</td>
    <td>${member.baleUsername ? `@${escapeHtml(member.baleUsername)}` : '—'}</td>
    <td class="${member.debt > 0 ? 'amount-negative' : 'amount-positive'}">${formatMoney(member.debt)}</td>
    <td><span class="badge ${member.isActive ? 'success' : 'danger'}">${member.isActive ? 'فعال' : 'غیرفعال'}</span></td>
    <td class="actions">
      <button class="mini primary-action" data-action="penalty" data-id="${member.id}">ثبت جریمه</button>
      <button class="mini" data-action="set-debt" data-id="${member.id}">اصلاح بدهی</button>
      <button class="mini" data-action="edit" data-id="${member.id}">ویرایش</button>
      <button class="mini ${member.isActive ? 'danger' : 'warning'}" data-action="toggle" data-id="${member.id}">${member.isActive ? 'غیرفعال' : 'فعال'}</button>
    </td>
  </tr>`;
}

function groupQuery() {
  return state.groupId != null ? `?groupId=${state.groupId}` : '';
}

async function loadGroups() {
  state.groups = await api('/api/admin/groups');
  const selector = $('groupSelector');
  selector.innerHTML = state.groups.length
    ? state.groups.map((group) => `<option value="${group.id}" ${group.id === state.groupId ? 'selected' : ''}>${escapeHtml(group.title)}${group.isActive ? '' : ' (غیرفعال)'}</option>`).join('')
    : '<option value="">هنوز گروهی ثبت نشده</option>';
  const current = state.groups.find((group) => group.id === state.groupId) || state.groups[0];
  state.groupId = current ? current.id : null;
}

async function init() {
  try {
    const session = await api('/api/admin/session');
    state.csrfToken = session.csrfToken;
    state.username = session.username;
    showApp();
    await loadGroups();
    await navigate('dashboard');
  } catch {
    showLogin();
  }
}

$('groupSelector').addEventListener('change', async () => {
  state.groupId = Number($('groupSelector').value);
  await loadPage(state.page);
});

$('loginForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  const submit = event.submitter;
  if (submit) submit.disabled = true;
  try {
    const result = await api('/api/admin/login', {
      method: 'POST',
      body: { username: $('loginUsername').value.trim(), password: $('loginPassword').value }
    });
    state.csrfToken = result.csrfToken;
    state.username = result.username;
    $('loginPassword').value = '';
    showApp();
    await navigate('dashboard');
  } catch (error) {
    showToast(error.message === 'انجام عملیات ناموفق بود.' ? 'نام کاربری یا رمز عبور صحیح نیست.' : error.message, 'error');
  } finally {
    if (submit) submit.disabled = false;
  }
});

$('logoutButton').addEventListener('click', async () => {
  try { await api('/api/admin/logout', { method: 'POST' }); }
  catch { /* local logout must still complete */ }
  finally { showLogin(); }
});

$('refreshButton').addEventListener('click', async () => {
  const button = $('refreshButton');
  button.disabled = true;
  try {
    await loadPage(state.page);
    showToast('اطلاعات به‌روز شد.');
  } finally {
    button.disabled = false;
  }
});

$('nav').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-page]');
  if (button) navigate(button.dataset.page);
});

document.addEventListener('click', (event) => {
  const quick = event.target.closest('.quick-nav[data-target]');
  if (quick) navigate(quick.dataset.target);
});

$('menuButton').addEventListener('click', openSidebar);
$('sidebarBackdrop').addEventListener('click', closeSidebar);
window.addEventListener('resize', () => { if (window.innerWidth > 920) closeSidebar(); });

function openSidebar() {
  $('sidebar').classList.add('open');
  $('sidebarBackdrop').hidden = false;
}
function closeSidebar() {
  $('sidebar')?.classList.remove('open');
  if ($('sidebarBackdrop')) $('sidebarBackdrop').hidden = true;
}

async function navigate(page) {
  if (!pageMeta[page]) return;
  state.page = page;
  document.querySelectorAll('.page').forEach((item) => item.classList.toggle('active', item.id === `page-${page}`));
  document.querySelectorAll('#nav button').forEach((item) => item.classList.toggle('active', item.dataset.page === page));
  $('pageTitle').textContent = pageMeta[page][0];
  $('pageSubtitle').textContent = pageMeta[page][1];
  closeSidebar();
  await loadPage(page);
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

async function loadPage(page) {
  try {
    if (page === 'dashboard') await loadDashboard();
    if (page === 'members') await loadMembers();
    if (page === 'observed') await loadObserved();
    if (page === 'fund') await loadFund();
    if (page === 'payments') await loadPayments();
  } catch (error) {
    showToast(error.message, 'error');
  }
}

async function loadDashboard() {
  const [dashboard, members, payments] = await Promise.all([
    api('/api/admin/dashboard' + groupQuery()),
    api('/api/admin/members' + groupQuery()),
    api('/api/admin/payments' + groupQuery())
  ]);
  state.unit = dashboard.unit;
  state.members = members;
  state.payments = payments;

  const cards = [
    { label: 'موجودی صندوق', value: formatMoney(dashboard.fundBalance), note: 'موجودی قابل استفاده', icon: '◈', cls: '' },
    { label: 'کل بدهی باز', value: formatMoney(dashboard.totalDebt), note: `${formatNumber(dashboard.debtorCount)} عضو بدهکار`, icon: '↗', cls: 'accent' },
    { label: 'اعضای فعال', value: formatNumber(dashboard.activeMemberCount), note: 'عضو ثبت‌شده در صندوق', icon: '👥', cls: 'sage' },
    { label: 'پرداخت موفق', value: formatNumber(dashboard.paidCount), note: `${formatMoney(dashboard.collected)} وصول‌شده`, icon: '✓', cls: 'gold' }
  ];

  $('dashboardCards').innerHTML = cards.map((card) => `<article class="card ${card.cls}">
    <div class="card-top"><span class="card-label">${escapeHtml(card.label)}</span><span class="card-icon">${card.icon}</span></div>
    <strong>${card.value}</strong><small>${card.note}</small>
  </article>`).join('');

  const debtors = members.filter((member) => member.isActive && member.debt > 0).sort((a, b) => b.debt - a.debt).slice(0, 6);
  $('dashboardDebtorsBody').innerHTML = debtors.length ? debtors.map((member) => `<tr>
    <td><div class="member-cell"><span class="member-avatar">${firstLetter(member.displayName)}</span><strong>${escapeHtml(member.displayName)}</strong></div></td>
    <td>${member.baleUserId ?? '—'}</td>
    <td class="amount-negative">${formatMoney(member.debt)}</td>
    <td><span class="badge warning">در انتظار تسویه</span></td>
  </tr>`).join('') : emptyRow(4, 'همه حساب‌ها تسویه است', 'در حال حاضر بدهی بازی وجود ندارد.');

  $('recentPaymentsBody').innerHTML = payments.length ? payments.slice(0, 6).map((payment) => `<tr>
    <td>${escapeHtml(payment.memberName)}</td><td>${formatMoney(payment.amount)}</td><td>${statusBadge(payment.status)}</td><td>${formatDate(payment.createdAt)}</td>
  </tr>`).join('') : emptyRow(4, 'هنوز پرداختی ثبت نشده است', 'اولین فاکتور پس از درخواست کاربر اینجا نمایش داده می‌شود.');
}

async function loadMembers() {
  state.members = await api('/api/admin/members' + groupQuery());
  renderMembers();
}

function renderMembers() {
  const query = $('memberSearch').value.trim().toLowerCase();
  const filtered = state.members.filter((member) => {
    if (!query) return true;
    return [member.displayName, member.baleUsername, member.baleUserId].some((value) => String(value ?? '').toLowerCase().includes(query));
  });
  $('memberCount').textContent = `${formatNumber(filtered.length)} عضو`;
  $('membersBody').innerHTML = filtered.length
    ? filtered.map(memberRow).join('')
    : emptyRow(6, query ? 'عضوی با این عبارت پیدا نشد' : 'هنوز عضوی ثبت نشده است', query ? 'عبارت جست‌وجو را تغییر دهید.' : 'از دکمه «افزودن عضو» استفاده کنید.');
}

$('memberSearch').addEventListener('input', renderMembers);

$('membersBody').addEventListener('click', async (event) => {
  const button = event.target.closest('button[data-action]');
  if (!button) return;
  const member = state.members.find((item) => item.id === button.dataset.id);
  if (!member) return;

  try {
    if (button.dataset.action === 'edit') return openMemberDialog(member);
    if (button.dataset.action === 'penalty') return openAmountDialog(member, 'add');
    if (button.dataset.action === 'set-debt') return openAmountDialog(member, 'set');
    if (button.dataset.action === 'toggle') {
      await api(`/api/admin/members/${member.id}/toggle`, { method: 'POST' });
      showToast('وضعیت عضو تغییر کرد.');
      await loadMembers();
    }
  } catch (error) {
    showToast(error.message, 'error');
  }
});

$('addMemberButton').addEventListener('click', () => openMemberDialog());

function openMemberDialog(member = null) {
  openDialog({
    title: member ? 'ویرایش عضو' : 'افزودن عضو جدید',
    fields: [
      { name: 'displayName', label: 'نام و نام خانوادگی', value: member?.displayName || '', required: true, maxlength: 120 },
      { name: 'baleUserId', label: 'شناسه عددی بله', type: 'number', value: member?.baleUserId || '', hint: 'شناسه را می‌توانید از بخش کاربران دیده‌شده انتخاب کنید.' },
      { name: 'baleUsername', label: 'نام کاربری بله', value: member?.baleUsername || '', maxlength: 64 }
    ],
    onSubmit: async (values) => {
      const body = {
        displayName: values.displayName,
        baleUserId: values.baleUserId ? Number(values.baleUserId) : null,
        baleUsername: values.baleUsername || null
      };
      await api(member ? `/api/admin/members/${member.id}` : '/api/admin/members', {
        method: member ? 'PUT' : 'POST', body: { ...body, groupId: state.groupId }
      });
      showToast(member ? 'اطلاعات عضو ویرایش شد.' : 'عضو جدید اضافه شد.');
      await loadMembers();
    }
  });
}

function openAmountDialog(member, mode) {
  const isSet = mode === 'set';
  openDialog({
    title: `${isSet ? 'اصلاح بدهی' : 'ثبت جریمه'} — ${member.displayName}`,
    fields: [
      { name: 'amount', label: `مبلغ (${state.unit})`, type: 'number', value: isSet ? member.debt : '', required: true, min: isSet ? 0 : 1 },
      { name: 'description', label: 'شرح عملیات', value: isSet ? 'اصلاح بدهی از پنل' : 'جریمه دیرکرد', required: true, maxlength: 300 }
    ],
    onSubmit: async (values) => {
      await api(`/api/admin/members/${member.id}/debt/${isSet ? 'set' : 'add'}`, {
        method: 'POST', body: { amount: Number(values.amount), description: values.description, groupId: state.groupId }
      });
      showToast(isSet ? 'بدهی عضو اصلاح شد.' : 'جریمه با موفقیت ثبت شد.');
      await loadMembers();
    }
  });
}

async function loadObserved() {
  const users = await api('/api/admin/observed-users');
  $('observedBody').innerHTML = users.length ? users.map((user) => `<tr>
    <td><div class="member-cell"><span class="member-avatar">${firstLetter(user.displayName)}</span><strong>${escapeHtml(user.displayName)}</strong></div></td>
    <td>${user.baleUserId}</td>
    <td>${user.username ? `@${escapeHtml(user.username)}` : '—'}</td>
    <td>${formatDate(user.lastSeenAt)}</td>
    <td><button class="mini primary-action" data-user-id="${user.baleUserId}" data-user-name="${escapeHtml(user.displayName)}">افزودن به اعضا</button></td>
  </tr>`).join('') : emptyRow(5, 'کاربر جدیدی مشاهده نشده است', 'پس از ارسال پیام در گروه، کاربران اینجا نمایش داده می‌شوند.');
}

$('observedBody').addEventListener('click', (event) => {
  const button = event.target.closest('button[data-user-id]');
  if (!button) return;
  const baleUserId = Number(button.dataset.userId);
  const displayName = button.dataset.userName || '';
  openDialog({
    title: 'افزودن کاربر دیده‌شده',
    fields: [{ name: 'displayName', label: 'نام عضو', value: displayName, required: true, maxlength: 120 }],
    onSubmit: async (values) => {
      await api(`/api/admin/observed-users/${baleUserId}/create-member`, { method: 'POST', body: { displayName: values.displayName } });
      showToast('کاربر به اعضای صندوق اضافه شد.');
      await loadObserved();
    }
  });
});

async function loadFund() {
  const [dashboard, entries] = await Promise.all([api('/api/admin/dashboard' + groupQuery()), api('/api/admin/fund/entries' + groupQuery())]);
  state.unit = dashboard.unit;
  $('fundBalanceInput').value = dashboard.fundBalance;
  $('fundEntriesBody').innerHTML = entries.length ? entries.map((entry) => `<tr>
    <td>${formatDate(entry.createdAt)}</td><td>${escapeHtml(kindLabel(entry.kind))}</td><td>${escapeHtml(entry.description)}</td>
    <td class="${entry.amount >= 0 ? 'amount-positive' : 'amount-negative'}">${entry.amount >= 0 ? '+' : ''}${formatMoney(entry.amount)}</td>
  </tr>`).join('') : emptyRow(4, 'گردشی ثبت نشده است', 'با ثبت موجودی یا پرداخت، گردش‌ها اینجا نمایش داده می‌شوند.');
}

$('setFundForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    await api('/api/admin/fund/set-balance', {
      method: 'POST',
      body: { amount: Number($('fundBalanceInput').value), description: $('fundBalanceDescription').value, groupId: state.groupId }
    });
    showToast('موجودی صندوق تنظیم شد.');
    await loadFund();
  } catch (error) { showToast(error.message, 'error'); }
});

$('adjustFundForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    await api('/api/admin/fund/adjust', {
      method: 'POST',
      body: { amount: Number($('fundAdjustInput').value), description: $('fundAdjustDescription').value, groupId: state.groupId }
    });
    event.target.reset();
    showToast('گردش صندوق ثبت شد.');
    await loadFund();
  } catch (error) { showToast(error.message, 'error'); }
});

async function loadPayments() {
  state.payments = await api('/api/admin/payments' + groupQuery());
  $('paymentsBody').innerHTML = state.payments.length ? state.payments.map((payment) => `<tr>
    <td><div class="member-cell"><span class="member-avatar">${firstLetter(payment.memberName)}</span><strong>${escapeHtml(payment.memberName)}</strong></div></td>
    <td>${formatMoney(payment.amount)}</td><td>${statusBadge(payment.status)}</td><td>${formatDate(payment.createdAt)}</td>
    <td>${formatDate(payment.paidAt)}</td><td>${escapeHtml(payment.providerPaymentChargeId || '—')}</td>
  </tr>`).join('') : emptyRow(6, 'پرداختی ثبت نشده است', 'درخواست‌های صورت‌حساب کاربران اینجا نمایش داده می‌شود.');
}

$('passwordForm').addEventListener('submit', async (event) => {
  event.preventDefault();
  try {
    await api('/api/admin/change-password', {
      method: 'POST',
      body: { currentPassword: $('currentPassword').value, newPassword: $('newPassword').value }
    });
    event.target.reset();
    showToast('رمز عبور مدیر تغییر کرد.');
  } catch (error) { showToast(error.message, 'error'); }
});

function openDialog({ title, fields, onSubmit }) {
  $('dialogTitle').textContent = title;
  $('dialogFields').innerHTML = fields.map((field) => `<label>
    <span>${escapeHtml(field.label)}</span>
    <input name="${escapeHtml(field.name)}" type="${field.type || 'text'}" value="${escapeHtml(field.value ?? '')}"
      ${field.required ? 'required' : ''} ${field.min !== undefined ? `min="${field.min}"` : ''}
      ${field.maxlength ? `maxlength="${field.maxlength}"` : ''}>
    ${field.hint ? `<small>${escapeHtml(field.hint)}</small>` : ''}
  </label>`).join('');

  const dialog = $('formDialog');
  const form = $('dialogForm');
  form.onsubmit = async (event) => {
    event.preventDefault();
    if (event.submitter?.value === 'cancel') {
      dialog.close();
      return;
    }
    const submit = $('dialogSubmit');
    submit.disabled = true;
    const values = Object.fromEntries(new FormData(form).entries());
    try {
      await onSubmit(values);
      dialog.close();
    } catch (error) {
      showToast(error.message, 'error');
    } finally {
      submit.disabled = false;
    }
  };
  dialog.showModal();
  setTimeout(() => dialog.querySelector('input')?.focus(), 50);
}

init();
