/* 家庭消息控制台 —— 原生 JS，零构建
 *
 * 本文件分两部分：
 *   A. 表现层（配色 / 明暗模式 / Snackbar / Dialog / 图标 / 渲染）
 *   B. 业务逻辑（API / WebSocket / 设备 / 消息 / 截图 / 米家 / 昵称）
 *
 * 本次 UI 重构只动 A 部分；B 部分与原实现保持一致，协议与数据结构未做任何修改。
 * 设计令牌见 tokens.css（由 tools/gen_tokens.py 生成）。
 */

const STATE_ORDER = ['created', 'server_received', 'device_received', 'popup_displayed', 'read'];
const STATE_LABEL = {
  created: '已创建',
  server_received: '服务器已接收',
  device_received: 'PC 已收到',
  popup_displayed: '弹窗已显示',
  read: '已读',
};
const QUICK = ['下来吃饭了', '该睡觉了', '有人找你', '快出来一下', '开会中，勿扰'];

/* 裸端口访问时 BASE=""；走飞牛网关时 BASE="/app/family-message"。 */
const BASE = (() => {
  let p = location.pathname.replace(/\/index\.html$/, '');
  if (p.endsWith('/')) p = p.slice(0, -1);
  return p;
})();

const $ = (id) => document.getElementById(id);
const api = async (path, opts = {}) => {
  const res = await fetch(BASE + path, {
    headers: { 'Content-Type': 'application/json' },
    credentials: 'same-origin',
    ...opts,
  });
  if (res.status === 401) { askPassword(); throw new Error('需要访问口令'); }
  if (!res.ok) {
    let detail = res.statusText;
    try { detail = (await res.json()).detail || detail; } catch (_) {}
    throw new Error(detail);
  }
  return res.json();
};

let state = { config: null, devices: [], messages: [], selected: new Set(), limit: 30 };

/* ══════════════════════════════════════════════════════════════
   A. 表现层
   ══════════════════════════════════════════════════════════════ */

/* ── A1. 配色方案（8 套，只存本地）───────────────────────────── */
const SCHEMES = [
  { id: 'indigo', name: '靛蓝' },
  { id: 'violet', name: '紫罗' },
  { id: 'teal',   name: '青碧' },
  { id: 'green',  name: '松绿' },
  { id: 'amber',  name: '琥珀' },
  { id: 'coral',  name: '珊瑚' },
  { id: 'pink',   name: '品红' },
  { id: 'cyan',   name: '天青' },
];
const SCHEME_KEY = 'fm.scheme';
const LEGACY_THEME_KEY = 'fm.theme';   // 旧版本的键名，做一次迁移

/* ── A2. 明暗模式：light / dark / system（规范 §5，默认跟随系统） */
const MODE_KEY = 'fm.mode';
const MODES = [
  { id: 'system', name: '跟随系统', icon: 'i-auto' },
  { id: 'light',  name: '浅色',     icon: 'i-light' },
  { id: 'dark',   name: '深色',     icon: 'i-dark' },
];
const mqDark = window.matchMedia('(prefers-color-scheme: dark)');

function loadMode() {
  try {
    const v = localStorage.getItem(MODE_KEY);
    if (MODES.some((m) => m.id === v)) return v;
  } catch (_) {}
  return 'system';
}

/** 把 system 解析成实际的 light/dark，并写进 data-mode（CSS 只需处理两种确定状态） */
function resolvedMode(pref) {
  if (pref === 'light' || pref === 'dark') return pref;
  return mqDark.matches ? 'dark' : 'light';
}

function applyMode(pref, persist) {
  const real = resolvedMode(pref);
  const root = document.documentElement;
  root.setAttribute('data-mode', real);
  root.setAttribute('data-mode-pref', pref);       // 供设置面板显示当前选择
  // 移动端浏览器地址栏配色跟着主题走
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) {
    const bg = getComputedStyle(document.body).backgroundColor;
    if (bg) meta.setAttribute('content', bg);
  }
  if (persist) { try { localStorage.setItem(MODE_KEY, pref); } catch (_) {} }
}

// 系统主题变化时，只有「跟随系统」需要跟着变
mqDark.addEventListener('change', () => {
  if (loadMode() === 'system') { applyMode('system', false); renderModeRow(); }
});

/* ── A3. 配色应用 ────────────────────────────────────────────── */
function loadScheme() {
  try {
    const v = localStorage.getItem(SCHEME_KEY) || localStorage.getItem(LEGACY_THEME_KEY);
    if (v && SCHEMES.some((s) => s.id === v)) return v;
  } catch (_) {}
  return 'indigo';
}

function applyScheme(id, persist) {
  document.documentElement.setAttribute('data-scheme', id);
  if (persist) { try { localStorage.setItem(SCHEME_KEY, id); } catch (_) {} }
}

/** 取某套配色的预览色（临时挂一个探针读 CSS 变量，保证与 tokens.css 永远一致） */
function schemeColor(id, role = 'primary') {
  const probe = document.createElement('div');
  probe.setAttribute('data-scheme', id);
  // 带上当前模式，色块才会跟着明暗变化
  const mode = document.documentElement.getAttribute('data-mode');
  if (mode) probe.setAttribute('data-mode', mode);
  probe.style.display = 'none';
  document.body.appendChild(probe);
  const c = getComputedStyle(probe).getPropertyValue('--md-' + role).trim();
  probe.remove();
  return c || 'var(--md-outline)';
}

/* ── A4. 图标 ────────────────────────────────────────────────── */
function icon(name, cls = 'md-icon') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', '#i-' + name);
  svg.appendChild(use);
  return svg;
}

/* ── A5. Snackbar（规范 §20：不再用 alert / 小灰字）──────────── */
let snackSeq = 0;
function snack(text, opts = {}) {
  const host = $('snackbar-host');
  const el = document.createElement('div');
  el.className = 'md-snackbar' + (opts.error ? ' md-snackbar--error' : '');
  el.dataset.seq = String(++snackSeq);

  const t = document.createElement('span');
  t.className = 'md-snackbar__text';
  t.textContent = text;
  el.appendChild(t);

  if (opts.action) {
    const a = document.createElement('button');
    a.className = 'md-snackbar__action';
    a.textContent = opts.action;
    a.onclick = () => { opts.onAction && opts.onAction(); dismiss(); };
    el.appendChild(a);
  }

  // 同时最多两条，多的从上面挤掉
  while (host.children.length >= 2) host.removeChild(host.firstChild);
  host.appendChild(el);

  const life = opts.duration || (opts.action ? 7000 : 4000);
  let timer = setTimeout(dismiss, life);
  el.addEventListener('mouseenter', () => clearTimeout(timer));
  el.addEventListener('mouseleave', () => { timer = setTimeout(dismiss, 1500); });

  function dismiss() {
    clearTimeout(timer);
    if (!el.isConnected) return;
    el.classList.add('out');
    setTimeout(() => el.remove(), 200);
  }
  return dismiss;
}

/* ── A6. 对话框（替代 confirm / alert / prompt，规范 §20）────── */
let dialogStack = [];

function openDialog(el, focusEl) {
  el.classList.add('show');
  requestAnimationFrame(() => el.classList.add('visible'));
  dialogStack.push(el);
  if (focusEl) setTimeout(() => focusEl.focus(), 120);
}

function closeDialog(el) {
  el.classList.remove('visible');
  dialogStack = dialogStack.filter((d) => d !== el);
  setTimeout(() => el.classList.remove('show'), 200);
}

// Esc 关掉最上层的对话框
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape' || !dialogStack.length) return;
  const top = dialogStack[dialogStack.length - 1];
  // 有输入焦点时不抢 Esc（避免打断输入法）
  const t = document.activeElement;
  if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA') && t.id !== 'conv-text') return;
  const btn = top.querySelector('.md-dialog__head .md-icon-btn');
  if (btn) btn.click();
});

/** MD3 确认框。返回 Promise<boolean>。danger=true 时确认键用 error 色 */
function confirmDialog({ title = '确认', body = '', okText = '确定', cancelText = '取消', danger = false, iconName = 'i-warning' }) {
  return new Promise((resolve) => {
    const dlg = $('dlg-confirm');
    $('confirm-title').textContent = title;
    $('confirm-body').textContent = body;
    $('confirm-input-wrap').hidden = true;
    const ico = $('confirm-icon').querySelector('use');
    ico.setAttribute('href', '#' + iconName);

    const ok = $('confirm-ok');
    const cancel = $('confirm-cancel');
    ok.textContent = okText;
    cancel.textContent = cancelText;
    ok.className = 'md-btn ' + (danger ? 'md-btn--danger' : 'md-btn--filled');

    const done = (v) => { cleanup(); closeDialog(dlg); resolve(v); };
    const onOk = () => done(true);
    const onCancel = () => done(false);
    const onScrim = (e) => { if (e.target === dlg) done(false); };

    function cleanup() {
      ok.onclick = null; cancel.onclick = null; dlg.onclick = null;
    }
    ok.onclick = onOk;
    cancel.onclick = onCancel;
    dlg.onclick = onScrim;
    openDialog(dlg, ok);
  });
}

/** MD3 输入框对话框。返回 Promise<string|null> */
function promptDialog({ title = '请输入', label = '', body = '', okText = '确定', value = '', type = 'text' }) {
  return new Promise((resolve) => {
    const dlg = $('dlg-confirm');
    $('confirm-title').textContent = title;
    $('confirm-body').textContent = body;
    $('confirm-icon').querySelector('use').setAttribute('href', '#i-person');

    const wrap = $('confirm-input-wrap');
    wrap.hidden = false;
    $('confirm-input-label').textContent = label;
    const input = $('confirm-input');
    input.type = type;
    input.value = value || '';

    const ok = $('confirm-ok');
    const cancel = $('confirm-cancel');
    ok.textContent = okText;
    cancel.textContent = '取消';
    ok.className = 'md-btn md-btn--filled';

    const onOk = () => done(input.value);
    const onCancel = () => done(null);
    const onKey = (e) => { if (e.key === 'Enter') { e.preventDefault(); done(input.value); } };
    const onScrim = (e) => { if (e.target === dlg) done(null); };

    function cleanup() {
      ok.onclick = null; cancel.onclick = null; input.removeEventListener('keydown', onKey);
      dlg.onclick = null; $('confirm-input-wrap').hidden = true;
    }
    function done(v) { cleanup(); closeDialog(dlg); resolve(v); }

    ok.onclick = onOk;
    cancel.onclick = onCancel;
    input.addEventListener('keydown', onKey);
    dlg.onclick = onScrim;
    openDialog(dlg, input);
  });
}

/* ══════════════════════════════════════════════════════════════
   B. 业务逻辑（与原实现一致）
   ══════════════════════════════════════════════════════════════ */

/* ── B1. 昵称色：同一昵称在任何一端都是同一个颜色。
       算法必须与 PC 端 / Android 端完全一致（见 docs/DESIGN-TOKENS.md §3） */
const NICK_COLORS = [
  '#90CAF9', '#CE93D8', '#80CBC4', '#A5D6A7', '#FFE082', '#FFCC80',
  '#EF9A9A', '#F48FB1', '#9FA8DA', '#80DEEA', '#C5E1A5', '#FFAB91',
];

function nickColor(name) {
  const s = (name || '').trim();
  if (!s) return NICK_COLORS[0];
  let h = 0;
  for (let i = 0; i < s.length; i++) {
    h = (Math.imul(h, 31) + s.charCodeAt(i)) >>> 0;   // 32 位无符号回绕
  }
  return NICK_COLORS[h % NICK_COLORS.length];
}

/* ── B2. 口令 ────────────────────────────────────────────────── */
async function askPassword() {
  const pwd = await promptDialog({
    title: '需要访问口令',
    label: '家庭控制台访问口令',
    body: '这台 NAS 上的控制台需要口令才能访问。',
    okText: '进入',
    type: 'password',
  });
  if (pwd === null) return;
  try {
    const r = await fetch(BASE + '/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      body: JSON.stringify({ password: pwd }),
    });
    if (r.ok) location.reload();
    else snack('口令不对，再试一次', { error: true });
  } catch (_) {
    snack('登录请求失败', { error: true });
  }
}

/* ── B3. MD 暴露式下拉（原生 select 浮层无法定制，只能自绘）──── */
function buildSelect(host, items, value, onChange) {
  host.innerHTML = '';
  const chosen = items.find((i) => i.value === value) || items[0];
  host._value = chosen ? chosen.value : '';

  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'md-select__btn';
  btn.setAttribute('aria-haspopup', 'listbox');
  btn.setAttribute('aria-expanded', 'false');

  const label = document.createElement('span');
  label.textContent = chosen ? chosen.label : '（没有可选项）';
  if (chosen && chosen.color) label.style.color = chosen.color;

  btn.append(label, icon('expand'));
  const chev = btn.querySelector('svg');
  chev.setAttribute('class', 'md-icon');

  const menu = document.createElement('div');
  menu.className = 'md-select__menu';
  menu.setAttribute('role', 'listbox');
  items.forEach((it) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'md-select__item';
    el.setAttribute('role', 'option');
    el.setAttribute('aria-selected', it.value === host._value ? 'true' : 'false');
    if (it.color) {
      const sw = document.createElement('span');
      sw.className = 'md-select__swatch';
      sw.style.background = it.color;
      el.appendChild(sw);
    }
    const t = document.createElement('span');
    t.textContent = it.label;
    el.appendChild(t);
    el.onclick = (ev) => {
      ev.stopPropagation();
      host.classList.remove('open');
      btn.setAttribute('aria-expanded', 'false');
      buildSelect(host, items, it.value, onChange);
      if (onChange) onChange(it.value);
    };
    menu.appendChild(el);
  });

  btn.onclick = (ev) => {
    ev.stopPropagation();
    document.querySelectorAll('.md-select.open').forEach((o) => {
      if (o !== host) {
        o.classList.remove('open');
        const b = o.querySelector('.md-select__btn');
        if (b) b.setAttribute('aria-expanded', 'false');
      }
    });
    const open = host.classList.toggle('open');
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  };

  host.append(btn, menu);
}

document.addEventListener('click', () => {
  document.querySelectorAll('.md-select.open').forEach((o) => {
    o.classList.remove('open');
    const b = o.querySelector('.md-select__btn');
    if (b) b.setAttribute('aria-expanded', 'false');
  });
});

/* ── B4. 初始化 ──────────────────────────────────────────────── */
async function boot() {
  applyScheme(loadScheme(), false);
  applyMode(loadMode(), false);

  state.config = await api('/api/config');
  renderNameSelectors();

  $('quick').innerHTML = '';
  QUICK.forEach((q) => {
    const b = document.createElement('button');
    b.className = 'md-chip md-chip--action';
    b.type = 'button';
    b.textContent = q;
    b.onclick = () => { $('content').value = q; $('content').focus(); };
    $('quick').appendChild(b);
  });

  await Promise.all([loadDevices(), loadMessages(), loadVersion(), loadXiaomi()]);
  connectWS();
  setInterval(refreshTimes, 1000);
  refreshTimes();
}

/* 显示当前运行的服务端版本 —— 升级后如果这个号没变，说明旧进程还在跑 */
async function loadVersion() {
  try {
    const r = await fetch(`${BASE}/healthz`, { cache: 'no-store' });
    const d = await r.json();
    if (d && d.version) $('server-ver').textContent = `v${d.version}`;
  } catch (_) { /* 拿不到就不显示 */ }
}

/* ── B5. 设备 ────────────────────────────────────────────────── */
async function loadDevices() {
  state.devices = await api('/api/devices');
  if (state.selected.size === 0) {
    state.devices.filter((d) => d.online).forEach((d) => state.selected.add(d.device_id));
  } else {
    const ids = new Set(state.devices.map((d) => d.device_id));
    [...state.selected].forEach((id) => { if (!ids.has(id)) state.selected.delete(id); });
  }
  renderDevices();
  renderTargets();
  // 消息里的投递标签要显示设备「名字」，设备列表晚于消息到达时
  // 之前渲染出来的会一直是 fallback 的 device_id，这里补一次。
  renderMessages();
}

function renderDevices() {
  const box = $('devices');
  box.innerHTML = '';
  $('dev-empty').hidden = state.devices.length > 0;

  const online = state.devices.filter((d) => d.online).length;
  $('dev-count').textContent = state.devices.length
    ? `${online} 台在线 · 共 ${state.devices.length} 台` : '';

  state.devices.forEach((d) => {
    const el = document.createElement('article');
    el.className = 'dev' + (d.online ? ' online' : '');

    // 头部：图标 + 名称 + 状态（小圆点 + 文字，不用 emoji）
    const top = document.createElement('div');
    top.className = 'dev__top';

    const ic = document.createElement('span');
    ic.className = 'dev__icon';
    ic.appendChild(icon('computer'));

    const nm = document.createElement('span');
    nm.className = 'dev__name';
    nm.textContent = d.name;
    nm.title = d.name;

    const st = document.createElement('span');
    st.className = 'dev__state';
    const dot = document.createElement('span');
    dot.className = 'md-dot ' + (d.online ? 'md-dot--online' : 'md-dot--offline');
    const stx = document.createElement('span');
    stx.textContent = d.online ? '在线' : '离线';
    st.append(dot, stx);

    top.append(ic, nm, st);

    const meta = document.createElement('div');
    meta.className = 'dev__meta';
    meta.textContent =
      (d.type === 'pc' ? 'Windows PC' : d.type) +
      (d.platform ? ` · ${d.platform.split(/\s+/)[0]}` : '') +
      (d.last_seen ? ` · 最后在线 ${d.last_seen}` : ' · 从未上线');

    const acts = document.createElement('div');
    acts.className = 'dev__acts';

    // 对话 / 查看桌面：次要操作 → Outlined
    acts.appendChild(mkBtn('对话', 'chat', 'md-btn--outlined', () => deviceAction(d, 'conv')));
    const shotBtn = mkBtn('桌面', 'screenshot', 'md-btn--outlined', () => deviceAction(d, 'shot'));
    if (!d.online) shotBtn.disabled = true;
    acts.appendChild(shotBtn);

    // 开机：只在有米家绑定时出现；已在线则禁用（不去动插座）
    if (d.xiaomi) {
      const verb = d.xiaomi.power_action === 'off' ? '关闭' : '开启';
      const wake = mkBtn('开机', 'power', 'md-btn--tonal', () => deviceAction(d, 'wake'));
      wake.disabled = !!d.online;
      wake.title = `执行米家「${d.xiaomi.name}」的${verb}动作`
        + (d.online ? '（设备已在线，无需开机）' : '');
      acts.appendChild(wake);
    }

    // 关机：危险操作 → error 色
    const off = mkBtn('关机', 'power-off', 'md-btn--danger-text', () => deviceAction(d, 'shutdown'));
    if (!d.online) off.disabled = true;
    acts.appendChild(off);

    el.append(top, meta, acts);
    box.appendChild(el);
  });
}

function mkBtn(text, iconName, variant, onclick) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'md-btn ' + variant;
  b.append(icon(iconName, 'md-btn__icon'), Object.assign(document.createElement('span'), { textContent: text }));
  b.onclick = onclick;
  return b;
}

function renderTargets() {
  const box = $('targets');
  box.innerHTML = '';
  state.devices.forEach((d) => {
    const on = state.selected.has(d.device_id);
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'md-chip';
    el.setAttribute('aria-pressed', on ? 'true' : 'false');
    if (!d.online) el.setAttribute('aria-disabled', 'true');

    const dot = document.createElement('span');
    dot.className = 'md-dot ' + (d.online ? 'md-dot--online' : 'md-dot--offline');
    const t = document.createElement('span');
    t.textContent = d.name;
    el.append(dot, t);
    el.onclick = () => {
      if (state.selected.has(d.device_id)) state.selected.delete(d.device_id);
      else state.selected.add(d.device_id);
      renderTargets();
    };
    box.appendChild(el);
  });
  $('btn-send').disabled = state.selected.size === 0;
}

/* ── B6. 设备动作 ────────────────────────────────────────────── */
async function deviceAction(d, act) {
  if (act === 'conv') {
    openConversation(d);

  } else if (act === 'shot') {
    openShot(d);

  } else if (act === 'wake') {
    const plug = d.xiaomi ? `「${d.xiaomi.name}」` : '绑定的米家设备';
    const verb = (d.xiaomi && d.xiaomi.power_action === 'off') ? '关闭' : '开启';
    const ok = await confirmDialog({
      title: `要${verb}米家设备吗？`,
      body: `将执行 ${plug} 的「${verb}」动作。` +
            (verb === '开启' ? '这会通电，如果 PC 已经开机不会有影响。' : '这会断电，正在运行的 PC 会直接掉电。'),
      okText: verb,
      danger: verb === '关闭',
      iconName: 'i-power',
    });
    if (!ok) return;
    const dismiss = snack(`正在${verb}米家设备……`);
    try {
      const r = await api(`/api/devices/${d.device_id}/wake`, { method: 'POST' });
      dismiss();
      snack(r.already_online ? '设备已经在线，没有动插座' : (r.message || '已发出指令'));
    } catch (e) {
      dismiss();
      snack((e.message || '').includes('未绑定')
        ? '这台 PC 还没绑定米家开关，去「设置 → 米家」里配一下'
        : ('操作失败：' + e.message), { error: true });
    }

  } else if (act === 'shutdown') {
    const ok = await confirmDialog({
      title: `让「${d.name}」关机？`,
      body: 'PC 端会立刻执行关机（有几秒缓冲，可在电脑上运行 shutdown /a 取消）。',
      okText: '关机',
      danger: true,
      iconName: 'i-power-off',
    });
    if (!ok) return;
    const dismiss = snack('正在下发关机指令……');
    try {
      const r = await api(`/api/devices/${d.device_id}/shutdown`, { method: 'POST' });
      dismiss();
      snack(r.message || '关机指令已下发');
    } catch (e) {
      dismiss();
      snack('关机失败：' + e.message, { error: true });
    }
  }
}

/* ── B7. 消息 ────────────────────────────────────────────────── */
async function sendMessage() {
  const content = $('content').value.trim();
  if (!content) { snack('先写点什么再发吧', { error: true }); return; }
  if (state.selected.size === 0) { snack('还没有选接收的设备', { error: true }); return; }

  const btn = $('btn-send');
  btn.disabled = true;
  $('send-hint').textContent = '发送中……';
  try {
    const r = await api('/api/messages', {
      method: 'POST',
      body: JSON.stringify({
        sender_name: currentSender(),
        content,
        targets: [...state.selected],
      }),
    });
    $('content').value = '';
    const off = r.offline || [];
    $('send-hint').textContent = '';
    snack(off.length
      ? `已发送；${off.length} 个设备离线，会在上线后补投`
      : '已送达设备', { action: '知道了' });
    upsertMessage(r.message);
  } catch (e) {
    $('send-hint').textContent = '';
    snack('发送失败：' + e.message, { error: true });
  } finally {
    btn.disabled = false;
  }
}

async function loadMessages() {
  state.messages = await api(`/api/messages?limit=${state.limit}`);
  renderMessages();
}

function upsertMessage(msg) {
  const i = state.messages.findIndex((m) => m.id === msg.id);
  if (i >= 0) state.messages[i] = msg; else state.messages.unshift(msg);
  renderMessages();
}

function renderMessages() {
  const box = $('log');
  box.innerHTML = '';
  $('log-empty').hidden = state.messages.length > 0;

  state.messages.forEach((m) => {
    const el = document.createElement('article');
    el.className = 'msg';
    // 颜色只看昵称，不看这条是从网页还是从设备来的
    el.style.setProperty('--msg-nick', nickColor(m.sender_name));

    const head = document.createElement('div');
    head.className = 'msg__head';

    const who = document.createElement('span');
    who.className = 'msg__sender';
    who.textContent = m.sender_name;
    head.appendChild(who);

    if (m.sender_kind === 'device') {
      const badge = document.createElement('span');
      badge.className = 'msg__badge';
      badge.textContent = 'PC 回复';
      head.appendChild(badge);
    }

    const time = document.createElement('span');
    time.className = 'msg__time';
    time.textContent = m.created_at;
    head.appendChild(time);

    const body = document.createElement('div');
    body.className = 'msg__body';
    body.textContent = m.content;

    el.append(head, body);

    // 设备回复的接收方是「Web Sender」统一入口，没有逐设备投递状态
    if (m.sender_kind !== 'device' && (m.targets || []).length) {
      const tags = document.createElement('div');
      tags.className = 'msg__tags';
      m.targets.forEach((t) => {
        const dev = state.devices.find((d) => d.device_id === t.device_id);
        const tag = document.createElement('span');
        const rank = STATE_ORDER.indexOf(t.status);
        const label = STATE_LABEL[t.status] || t.status;
        tag.className = 'msg__tag' + (rank >= 3 ? ' msg__tag--done' : (rank < 2 ? ' msg__tag--fail' : ''));
        tag.append(icon(rank >= 3 ? 'check' : 'expand', 'md-chip__icon'),
                   Object.assign(document.createElement('span'), {
                     textContent: `${dev ? dev.name : t.device_id} · ${label}`,
                   }));
        tags.appendChild(tag);
      });
      el.appendChild(tags);
    }

    box.appendChild(el);
  });
}

/* ── B8. 截图对话框 ──────────────────────────────────────────── */
let shotDevice = null;

async function openShot(d) {
  shotDevice = d;
  $('shot-title').textContent = `${d.name} · 桌面`;
  $('shot-meta').textContent = '';
  openDialog($('dlg-shot'));
  await fetchShot();
}

async function fetchShot() {
  if (!shotDevice) return;
  const d = shotDevice;
  const img = $('shot-img');
  img.hidden = true;
  img.removeAttribute('src');
  $('shot-loading').hidden = false;
  $('shot-loading-text').textContent = '正在获取桌面截图……';
  $('shot-meta').textContent = d.online ? '' : '设备当前离线，可能拿不到截图。';

  try {
    const r = await api(`/api/devices/${d.device_id}/screenshot`, { method: 'POST' });
    img.src = r.data_url;
    img.alt = `${d.name} 的桌面截图，${r.taken_at}`;
    img.hidden = false;
    $('shot-loading').hidden = true;
    const bits = [`${r.width}×${r.height}`, `${(r.bytes / 1024).toFixed(0)} KB`, r.taken_at];
    $('shot-meta').textContent = bits.join(' · ')
      + (r.screen_locked ? ' · 屏幕可能处于锁屏状态' : '');
  } catch (e) {
    $('shot-loading-text').textContent = '截图失败：' + e.message;
    $('shot-meta').textContent = '';
  }
}

/* ── B9. 对话视图（Web Sender ⇄ 某台设备）────────────────────── */
let convDevice = null;

async function openConversation(device) {
  convDevice = device;
  $('conv-title').textContent = device.name;
  $('conv-state').textContent = device.online ? '在线' : '离线（消息会在它上线后补投）';
  renderNameSelectors();
  openDialog($('dlg-conv'), $('conv-text'));
  await refreshConversation();
}

async function refreshConversation() {
  if (!convDevice) return;
  try {
    const list = await api(`/api/conversations/${convDevice.device_id}?limit=100`);
    const box = $('conv-body');
    box.innerHTML = '';
    if (list.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'md-empty';
      empty.appendChild(icon('chat', 'md-icon md-icon--lg'));
      empty.append(
        Object.assign(document.createElement('span'), { className: 'md-empty__title', textContent: '还没有往来消息' }),
        Object.assign(document.createElement('span'), { className: 'md-empty__hint', textContent: '在下面输入一条，PC 上会立刻弹窗。' }),
      );
      box.appendChild(empty);
    }
    list.forEach((m) => {
      const b = document.createElement('div');
      b.className = 'bubble ' + (m.sender_kind === 'device' ? 'bubble--out' : '');
      b.style.setProperty('--msg-nick', nickColor(m.sender_name));
      const meta = document.createElement('span');
      meta.className = 'bubble__meta';
      meta.textContent = `${m.sender_name} · ${m.created_at}`;
      const text = document.createElement('span');
      text.textContent = m.content;
      b.append(meta, text);
      box.appendChild(b);
    });
    box.scrollTop = box.scrollHeight;
  } catch (e) {
    $('conv-body').textContent = '加载失败：' + e.message;
  }
}

async function sendFromConversation() {
  if (!convDevice) return;
  const text = $('conv-text').value.trim();
  if (!text) return;
  const who = $('conv-sender-sel')._value || names[0];
  try {
    await api('/api/messages', {
      method: 'POST',
      body: JSON.stringify({
        sender_name: who,
        content: text,
        targets: [convDevice.device_id],
      }),
    });
    $('conv-text').value = '';
    await refreshConversation();
  } catch (e) {
    $('conv-state').textContent = '发送失败：' + e.message;
  }
}

/* ── B10. WebSocket 实时事件 ─────────────────────────────────── */
function setConn(on) {
  $('ws-dot').className = 'md-dot ' + (on ? 'md-dot--online' : 'md-dot--offline');
  $('ws-text').textContent = on ? '已连接' : '连接断开，重连中…';
}

function connectWS() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(`${proto}://${location.host}${BASE}/ws/web`);
  ws.onopen = () => setConn(true);
  ws.onclose = () => { setConn(false); setTimeout(connectWS, 3000); };
  ws.onerror = () => ws.close();
  ws.onmessage = (ev) => {
    let d; try { d = JSON.parse(ev.data); } catch (_) { return; }
    if (d.type === 'device_status' || d.type === 'device_updated' || d.type === 'device_deleted') {
      loadDevices();
    } else if (d.type === 'message') {
      upsertMessage(d.message);
      if (convDevice) {
        const m = d.message;
        const mine = m.sender_device_id === convDevice.device_id ||
          (m.targets || []).some((t) => t.device_id === convDevice.device_id);
        if (mine) refreshConversation();
      }
    } else if (d.type === 'message_status') {
      const m = state.messages.find((x) => x.id === d.message_id);
      if (m) {
        const t = (m.targets || []).find((x) => x.device_id === d.device_id);
        if (t && d.target) Object.assign(t, d.target);
        renderMessages();
      }
    } else if (d.type === 'wake') {
      snack(`${d.result.plug || '米家设备'} 已开启，等待 PC 上线……`);
    } else if (d.type === 'xiaomi') {
      loadXmBound();
    } else if (d.type === 'shutdown_sent') {
      const dev = state.devices.find((x) => x.device_id === d.device_id);
      snack(`「${dev ? dev.name : d.device_id}」关机指令已下发`);
    }
  };
}

/* ── B11. 发送昵称（纯本地；服务端不参与）────────────────────── */
const NAMES_KEY = 'fm.names';
const LAST_SENDER_KEY = 'fm.lastSender';
let names = loadNames();

function loadNames() {
  try {
    const raw = localStorage.getItem(NAMES_KEY);
    if (raw) {
      const arr = JSON.parse(raw);
      if (Array.isArray(arr)) {
        const clean = arr.filter((n) => typeof n === 'string' && n.trim());
        if (clean.length) return clean;
      }
    }
  } catch (_) { /* 解析失败就回默认 */ }
  return ['我'];
}

function persistNames() {
  try { localStorage.setItem(NAMES_KEY, JSON.stringify(names)); } catch (_) {}
}

function rememberSender(value) {
  try { localStorage.setItem(LAST_SENDER_KEY, value || ''); } catch (_) {}
}

/** 当前选中的发送人（下拉 → localStorage → 第一个） */
function currentSender() {
  const v = $('sender-sel')._value;
  if (v && names.includes(v)) return v;
  let last = '';
  try { last = localStorage.getItem(LAST_SENDER_KEY) || ''; } catch (_) {}
  return names.includes(last) ? last : names[0];
}

/** 把本地昵称同步到两个下拉框（主发送区 + 对话窗） */
function renderNameSelectors() {
  const items = names.map((n) => ({ value: n, label: n, color: nickColor(n) }));

  buildSelect($('sender-sel'), items, currentSender(), (v) => rememberSender(v));

  const convKeep = $('conv-sender-sel')._value;
  const convVal = names.includes(convKeep) ? convKeep : names[0];
  buildSelect($('conv-sender-sel'), items, convVal, null);

  renderNamesList();
}

function renderNamesList() {
  const box = $('names-list');
  box.innerHTML = '';
  if (names.length === 0) {
    const p = document.createElement('p');
    p.className = 'md-body-small md-muted';
    p.textContent = '还没有昵称，先添加一个吧。';
    box.appendChild(p);
    return;
  }

  names.forEach((name, idx) => {
    const row = document.createElement('div');
    row.className = 'name-row';

    const sw = document.createElement('span');
    sw.className = 'md-dot';
    sw.style.background = nickColor(name);

    const input = document.createElement('input');
    input.className = 'name-row__input';
    input.value = name;
    input.maxLength = 16;
    input.setAttribute('aria-label', `昵称 ${name}`);
    input.onchange = () => {
      const v = input.value.trim();
      if (!v || (names.includes(v) && names[idx] !== v)) {
        input.value = names[idx];
        if (v && names.includes(v)) snack('这个昵称已经有了', { error: true });
        return;
      }
      names[idx] = v;
      persistNames();
      renderNameSelectors();
    };

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'md-icon-btn';
    del.setAttribute('aria-label', `删除昵称 ${name}`);
    del.appendChild(icon('delete', 'md-icon md-icon--sm'));
    del.style.color = 'var(--md-error)';
    del.onclick = () => {
      names.splice(idx, 1);
      if (names.length === 0) names = ['我'];
      persistNames();
      renderNameSelectors();
    };

    row.append(sw, input, del);
    box.appendChild(row);
  });
}

function addName() {
  const v = $('name-new').value.trim();
  if (!v) return;
  if (!names.includes(v)) {
    names.push(v);
    persistNames();
  } else {
    snack('这个昵称已经有了');
  }
  $('name-new').value = '';
  rememberSender(v);
  renderNameSelectors();
}

/* ── B12. 米家开关绑定（官方 OAuth2 + MIoT 云端）
       模型：米家设备 + 动作(开/关) → 某台 PC
       这个绑定只用于「设备清单里那台 PC 上的『开机』按钮」。 */
let xmStatus = null;
let xmDevices = [];      // 从米家云端发现的设备
let xmBoundList = [];    // 已建立的绑定规则

const XM_ACTIONS = [
  { value: 'on', label: '开（通电）' },
  { value: 'off', label: '关（断电）' },
];

async function loadXiaomi() {
  try {
    xmStatus = await api('/api/xiaomi/status');
  } catch (_) {
    xmStatus = { logged_in: false };
  }

  const on = !!(xmStatus && xmStatus.logged_in);
  $('xm-login').hidden = on;
  $('xm-authed').hidden = !on;
  $('xm-state-line').textContent = on
    ? '已授权米家。绑定规则：米家设备 + 动作 → 某台 PC（只影响那台 PC 上的「开机」按钮）'
    : '尚未授权米家。授权后可以绑定智能插座，让网页端能远程开机。';
  $('xm-redirect').textContent = xmStatus.redirect_url || '';

  if (on) {
    const days = Math.round((xmStatus.expires_in_seconds || 0) / 86400);
    $('xm-login-info').textContent = days > 0
      ? `token 还剩约 ${days} 天（到期前自动续期）`
      : 'token 即将到期，下次调用会自动续期';
    await loadXmBound();
    renderXmForm();
    if (!xmDevices.length) xmDiscover();     // 首次打开自动拉一次设备列表
  }
}

async function xmGetUrl() {
  $('xm-url-hint').textContent = '正在获取……';
  try {
    const d = await api('/api/xiaomi/auth-url');
    const a = $('xm-auth-link');
    a.href = d.auth_url;
    a.textContent = d.auth_url;
    $('xm-url-box').hidden = false;
    $('xm-url-hint').textContent = '在浏览器打开下面的链接';
    $('xm-redirect').textContent = d.redirect_url;
  } catch (e) {
    $('xm-url-hint').textContent = '获取失败：' + e.message;
  }
}

async function xmExchange() {
  const code = $('xm-code').value.trim();
  if (!code) return;
  $('xm-exchange-hint').textContent = '正在换取 token……';
  try {
    await api('/api/xiaomi/exchange', { method: 'POST', body: JSON.stringify({ code }) });
    $('xm-code').value = '';
    $('xm-url-box').hidden = true;
    $('xm-exchange-hint').textContent = '';
    await loadXiaomi();
    snack('米家授权成功');
  } catch (e) {
    $('xm-exchange-hint').textContent = '授权失败：' + e.message;
  }
}

async function xmLogout() {
  const ok = await confirmDialog({
    title: '退出米家授权？',
    body: '退出后要重新走一遍浏览器授权流程，已建立的绑定不会丢。',
    okText: '退出',
    danger: true,
    iconName: 'i-logout',
  });
  if (!ok) return;
  try {
    await api('/api/xiaomi/logout', { method: 'POST' });
    xmDevices = [];
    xmBoundList = [];
    await loadXiaomi();
    snack('已退出米家');
  } catch (e) {
    snack('退出失败：' + e.message, { error: true });
  }
}

async function xmDiscover() {
  $('xm-login-info').textContent = '正在向米家云端拉取设备列表……';
  try {
    xmDevices = await api('/api/xiaomi/discover', { method: 'POST' });
  } catch (e) {
    $('xm-login-info').textContent = '拉取设备失败：' + e.message;
    return;
  }
  await loadXiaomi();
  $('xm-login-info').textContent = xmDevices.length
    ? `已获取 ${xmDevices.length} 个米家设备`
    : '这个账号下没有找到设备';
}

/* 新增绑定表单：选择开关设备 + 动作 + 关联 PC */
function renderXmForm() {
  const devItems = xmDevices.length
    ? xmDevices.map((d) => ({
        value: d.miot_device_id,
        label: (d.name || d.miot_device_id) + (d.is_online === false ? '（离线）' : ''),
      }))
    : [{ value: '', label: '（先点「刷新米家设备」）' }];
  buildSelect($('xm-dev-pick'), devItems, devItems[0].value, null);
  buildSelect($('xm-act-pick'), XM_ACTIONS, 'on', null);

  const pcItems = [{ value: '', label: '不指定 PC' }].concat(
    state.devices.map((d) => ({ value: d.device_id, label: d.name })));
  buildSelect($('xm-pc-pick'), pcItems, pcItems.length > 1 ? pcItems[1].value : '', null);
}

async function xmAdd() {
  const mid = $('xm-dev-pick')._value;
  const action = $('xm-act-pick')._value || 'on';
  const target = $('xm-pc-pick')._value || '';
  if (!mid) { snack('先选一个米家设备（点「刷新米家设备」）', { error: true }); return; }
  const dev = xmDevices.find((d) => d.miot_device_id === mid) || {};
  try {
    await api('/api/xiaomi/devices', {
      method: 'POST',
      body: JSON.stringify({
        name: dev.name || mid,
        miot_device_id: mid,
        urn: dev.model || '',
        device_type: 'plug',
        power_siid: 2,
        power_piid: 1,
        power_action: action,
        target_device_id: target,
      }),
    });
    snack('已添加绑定');
    await loadXmBound();
    await loadDevices();          // 设备卡片上的「开机」按钮要跟着出现
  } catch (e) {
    snack('添加失败：' + e.message, { error: true });
  }
}

async function loadXmBound() {
  try {
    xmBoundList = await api('/api/xiaomi/devices');
  } catch (_) {
    xmBoundList = [];
  }
  renderXmBound();
}

function renderXmBound() {
  const box = $('xm-bound');
  box.innerHTML = '';
  if (!xmBoundList.length) {
    const p = document.createElement('p');
    p.className = 'md-body-small md-muted';
    p.textContent = '还没有绑定。上面选好设备、动作和目标 PC 后点「添加绑定」。';
    box.appendChild(p);
    return;
  }

  const sec = document.createElement('span');
  sec.className = 'md-section-label';
  sec.textContent = '已建立的绑定（改完即时生效）';
  box.appendChild(sec);

  xmBoundList.forEach((row) => {
    const el = document.createElement('div');
    el.className = 'xm-row';

    const meta = document.createElement('div');
    meta.className = 'xm-row__meta';
    const n = document.createElement('div');
    n.className = 'xm-row__name';
    n.textContent = row.name;
    const dd = document.createElement('div');
    dd.className = 'xm-row__desc';
    dd.textContent = `${row.urn || '未知型号'} · 电源 siid=${row.power_siid} piid=${row.power_piid}`;
    meta.append(n, dd);

    const acts = document.createElement('div');
    acts.className = 'xm-row__acts';

    const actPick = document.createElement('div');
    actPick.className = 'md-select';
    buildSelect(actPick, XM_ACTIONS, row.power_action || 'on',
      (v) => xmPatch(row, { power_action: v }));

    const pcPick = document.createElement('div');
    pcPick.className = 'md-select';
    const pcItems = [{ value: '', label: '不指定 PC' }].concat(
      state.devices.map((d) => ({ value: d.device_id, label: d.name })));
    buildSelect(pcPick, pcItems, row.target_device_id || '',
      (v) => xmPatch(row, { target_device_id: v }));

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'md-btn md-btn--danger-text';
    del.style.height = '36px';
    del.append(icon('delete', 'md-btn__icon'),
               Object.assign(document.createElement('span'), { textContent: '删除' }));
    del.onclick = () => xmUnbind(row);

    acts.append(actPick, pcPick, del);
    el.append(meta, acts);
    box.appendChild(el);
  });
}

async function xmPatch(row, fields) {
  try {
    await api(`/api/xiaomi/devices/${row.id}`, { method: 'PATCH', body: JSON.stringify(fields) });
    Object.assign(row, fields);
    snack('已更新');
    await loadDevices();
  } catch (e) {
    snack('更新失败：' + e.message, { error: true });
    await loadXmBound();
  }
}

async function xmUnbind(row) {
  const ok = await confirmDialog({
    title: `删除绑定「${row.name}」？`,
    body: '删除后，被关联的那台 PC 上不会再显示「开机」按钮。',
    okText: '删除',
    danger: true,
    iconName: 'i-delete',
  });
  if (!ok) return;
  try {
    await api(`/api/xiaomi/devices/${row.id}`, { method: 'DELETE' });
    await loadXmBound();
    await loadDevices();
    snack('已删除');
  } catch (e) {
    snack('删除失败：' + e.message, { error: true });
  }
}

/* ── B13. 设置面板 ───────────────────────────────────────────── */
function openSettings() {
  renderModeRow();
  renderSchemeGrid();
  renderNamesList();
  loadXiaomi();          // 米家那块也要刷新（设备列表可能变了）
  openDialog($('dlg-settings'));
}

function renderModeRow() {
  const box = $('mode-row');
  box.innerHTML = '';
  const cur = loadMode();
  MODES.forEach((m) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'md-btn ' + (m.id === cur ? 'md-btn--tonal' : 'md-btn--outlined');
    b.setAttribute('role', 'radio');
    b.setAttribute('aria-checked', m.id === cur ? 'true' : 'false');
    b.append(icon(m.icon, 'md-btn__icon'),
             Object.assign(document.createElement('span'), { textContent: m.name }));
    b.onclick = () => { applyMode(m.id, true); renderModeRow(); };
    box.appendChild(b);
  });
}

function renderSchemeGrid() {
  const box = $('scheme-grid');
  box.innerHTML = '';
  const cur = loadScheme();
  SCHEMES.forEach((s) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'scheme-item';
    el.setAttribute('role', 'radio');
    el.setAttribute('aria-checked', s.id === cur ? 'true' : 'false');
    el.setAttribute('aria-label', `配色 ${s.name}`);

    const sw = document.createElement('span');
    sw.className = 'scheme-item__sw';
    sw.style.background = schemeColor(s.id, 'primary');

    const lb = document.createElement('span');
    lb.className = 'scheme-item__t';
    lb.textContent = s.name;

    el.append(sw, lb);
    el.onclick = () => { applyScheme(s.id, true); renderSchemeGrid(); };
    box.appendChild(el);
  });
}

/* ── B14. 工具 ───────────────────────────────────────────────── */
function refreshTimes() {
  const d = new Date();
  $('server-time').textContent = d.toLocaleTimeString('zh-CN', { hour12: false });
}

/* ── B15. 事件绑定 ───────────────────────────────────────────── */
$('btn-send').onclick = sendMessage;
$('btn-refresh').onclick = () => {
  Promise.all([loadDevices(), loadMessages()]).then(() => snack('已刷新'));
};
$('btn-history').onclick = () => { state.limit += 30; loadMessages(); };

$('shot-close').onclick = () => closeDialog($('dlg-shot'));
$('shot-done').onclick = () => closeDialog($('dlg-shot'));
$('shot-again').onclick = fetchShot;
$('dlg-shot').onclick = (e) => { if (e.target.id === 'dlg-shot') closeDialog($('dlg-shot')); };

$('conv-close').onclick = () => { closeDialog($('dlg-conv')); convDevice = null; };
$('dlg-conv').onclick = (e) => { if (e.target.id === 'dlg-conv') { closeDialog($('dlg-conv')); convDevice = null; } };
$('conv-send').onclick = sendFromConversation;
$('conv-text').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); sendFromConversation(); }
});
$('content').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) sendMessage();
});

$('btn-settings').onclick = openSettings;
$('settings-close').onclick = () => closeDialog($('dlg-settings'));
$('dlg-settings').onclick = (e) => { if (e.target.id === 'dlg-settings') closeDialog($('dlg-settings')); };
$('name-add').onclick = addName;
$('name-new').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); addName(); }
});

/* 米家 */
$('xm-get-url').onclick = xmGetUrl;
$('xm-exchange').onclick = xmExchange;
$('xm-logout').onclick = xmLogout;
$('xm-discover').onclick = xmDiscover;
$('xm-add').onclick = xmAdd;
$('xm-code').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); xmExchange(); }
});

boot().catch((e) => {
  console.error(e);
  snack('初始化失败：' + e.message, { error: true, duration: 12000 });
});
