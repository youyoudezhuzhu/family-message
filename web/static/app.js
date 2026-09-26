/* 家庭消息控制台 —— 原生 JS，零构建
 *
 * 本文件分两部分：
 *   A. 表现层（主题 / 导航 / Toast / Dialog / 图标 / 渲染）
 *   B. 业务逻辑（API / WebSocket / 设备 / 消息 / 截图 / 米家 / 昵称）
 *
 * 本次是 Fluent 2（Windows 11）UI 重构：只动 A 部分的表现形式；
 * B 部分的 API 路径、请求/响应结构、WebSocket 消息格式、
 * 路由与交互流程全部与原实现一致，未做任何修改。
 * 设计令牌见 tokens.css（Fluent 2 令牌）。
 *
 * 追加（远程解锁 Phase 1，同样是纯表现层）：
 *   设备卡片上多一行 Windows 会话状态徽标（已登录 / 已锁屏 / 登录界面），
 *   以及一个「远程解锁」按钮；解锁请求用现有 Web 会话下发（不弹第二个密码框），
 *   结果由 /ws/web 的 unlock_result 帧推回。见 B5b / B6b。
 *
 * 追加（群聊模型，见 docs/GROUP-CHAT-MODEL.md）：家庭留言**不是设备间投递，是群聊**。
 *   一条消息发进一个共享空间，所有已注册设备都能看到，谁说的完全靠昵称区分。
 *   这次改动（只在表现层与交互层，API 路径 / 请求体结构 / WS 帧格式一律没动）：
 *     · 发送区去掉「发送给」设备选择与快捷设备网格 —— 只留 昵称 + 内容 + 发送，
 *       请求体里不再带 targets（字段本身保留，服务端自动广播给所有设备）
 *     · 消息记录是一条统一的群聊流，每条只有 昵称 + 内容 + 时间 + 「已发送」
 *       （已送达 / 已显示 / popup_displayed 这类逐设备状态展示全部移除）
 *     · 设备页完整保留（截图 / 远程关机 / 远程解锁 / 在线状态一律没动）
 *     · 「与某台设备的对话」弹窗随设备间投递语义一起去掉：消息只有一个共同空间
 *
 * 追加（WebView2 壳模式，纯分支，不动上面任何东西）：
 *   PC 端改成「WebView2 壳加载这个页面」，同一个网页既能当浏览器控制台，
 *   也能当原生应用界面（含全屏消息弹窗）。壳的判定、桥协议、弹窗视图全在
 *   static/shell.js；本文件只加了三处极小的钩子（都在壳模式下才生效）：
 *     · boot()        壳里先走 FM_SHELL.start()：加载态 → web.ready → 等 host.hello
 *     · connectWS()   壳里不再连 /ws/web（宿主持有 WebSocket），实时帧由桥喂给
 *                     handleServerFrame()（原 onmessage 的处理逻辑，帧格式完全一致）
 *     · renderNameSelectors()  壳里顺带同步弹窗里的「以谁的名义回复」
 *   浏览器模式下这三处都不触发，行为与改动前逐字节等价。
 */

/* 消息状态：群聊模型下只有一种 —— 已发送。
   逐设备状态表（created / server_received / device_received / popup_displayed / read）
   及其颜色映射已随「设备间投递」模型一起删除。 */
const STATUS_SENT = { cls: 'status--sent', icon: 'check', label: '已发送' };
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

let state = { config: null, devices: [], messages: [], limit: 30 };

/* ══════════════════════════════════════════════════════════════
   A. 表现层
   ══════════════════════════════════════════════════════════════ */

/* ── A1. 明暗模式：light / dark / system（默认跟随系统）─────────
       只有一套 Fluent 蓝品牌色，配色方案选择器已移除。 */
const MODE_KEY = 'fm.mode';
const MODES = [
  { id: 'system', name: '跟随系统', icon: 'system' },
  { id: 'light',  name: '浅色',     icon: 'sun' },
  { id: 'dark',   name: '深色',     icon: 'moon' },
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

/* ── A2. NavigationView 路由（纯前端切页，不涉及任何后端路由）── */
const PAGES = ['home', 'messages', 'devices', 'shot', 'settings'];
const PAGE_KEY = 'fm.page';
let currentPage = 'home';

function go(page) {
  if (!PAGES.includes(page)) page = 'home';
  currentPage = page;

  PAGES.forEach((p) => {
    const el = $('page-' + p);
    if (el) el.hidden = (p !== page);
  });

  document.querySelectorAll('[data-page]').forEach((b) => {
    const on = b.dataset.page === page;
    b.classList.toggle('is-selected', on);
    if (b.classList.contains('nav-item')) {
      b.setAttribute('aria-current', on ? 'page' : 'false');
    }
  });

  closeNavDrawer();
  try { localStorage.setItem(PAGE_KEY, page); } catch (_) {}

  // 进入页面时刷新该页自己的内容
  if (page === 'settings') enterSettings();
  else if (page === 'shot') renderShotPicks();

  window.scrollTo({ top: 0 });
}

function loadPage() {
  try {
    const v = localStorage.getItem(PAGE_KEY);
    if (PAGES.includes(v)) return v;
  } catch (_) {}
  return 'home';
}

function openNavDrawer() { $('app-shell').classList.add('nav-open'); }
function closeNavDrawer() { $('app-shell').classList.remove('nav-open'); }

/* ── A3. 首页问候语（按时间给一句话，家庭应用不用 KPI）─────── */
function greetingText() {
  const h = new Date().getHours();
  if (h < 5) return '夜深了';
  if (h < 9) return '早上好';
  if (h < 12) return '上午好';
  if (h < 14) return '中午好';
  if (h < 18) return '下午好';
  if (h < 23) return '晚上好';
  return '夜深了';
}

function renderGreeting() {
  $('home-greeting').textContent = greetingText();
}

/* ── A4. 图标（Fluent System Icons 内联 SVG 精灵）────────────── */
function icon(name, cls = 'icon') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', '#i-' + name);
  svg.appendChild(use);
  return svg;
}

/* ── A5. Toast（Fluent MessageBar 风格，替代 alert / 小灰字）─── */
let snackSeq = 0;
function snack(text, opts = {}) {
  const host = $('snackbar-host');
  const el = document.createElement('div');
  el.className = 'toast' + (opts.error ? ' toast--error' : '');
  el.dataset.seq = String(++snackSeq);

  el.appendChild(icon(opts.error ? 'error' : 'info', 'icon icon--sm toast__icon'));

  const t = document.createElement('span');
  t.className = 'toast__text';
  t.textContent = text;
  el.appendChild(t);

  if (opts.action) {
    const a = document.createElement('button');
    a.className = 'toast__action';
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
    el.classList.add('is-out');
    setTimeout(() => el.remove(), 200);
  }
  return dismiss;
}

/* ── A6. Dialog（Fluent ContentDialog，替代 confirm / alert / prompt） */
let dialogStack = [];

function openDialog(el, focusEl) {
  el.classList.add('is-open');
  requestAnimationFrame(() => el.classList.add('is-visible'));
  dialogStack.push(el);
  if (focusEl) setTimeout(() => focusEl.focus(), 120);
}

function closeDialog(el) {
  el.classList.remove('is-visible');
  dialogStack = dialogStack.filter((d) => d !== el);
  setTimeout(() => el.classList.remove('is-open'), 200);
}

// Esc 关掉最上层的对话框
document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape' || !dialogStack.length) return;
  const top = dialogStack[dialogStack.length - 1];
  // 有输入焦点时不抢 Esc（避免打断输入法）
  const t = document.activeElement;
  if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
  const btn = top.querySelector('.dialog__head .icon-btn');
  if (btn) btn.click();
});

/** Fluent 确认框。返回 Promise<boolean>。danger=true 时确认键用 error 色 */
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
    ok.className = 'btn ' + (danger ? 'btn--danger' : 'btn--primary');

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

/** Fluent 输入框对话框。返回 Promise<string|null> */
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
    ok.className = 'btn btn--primary';

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

/* ── B3. Fluent 下拉 / 组合框（原生 select 浮层无法定制，只能自绘） */
function buildSelect(host, items, value, onChange) {
  host.innerHTML = '';
  const chosen = items.find((i) => i.value === value) || items[0];
  host._value = chosen ? chosen.value : '';

  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'combo__btn';
  btn.setAttribute('aria-haspopup', 'listbox');
  btn.setAttribute('aria-expanded', 'false');

  const label = document.createElement('span');
  label.textContent = chosen ? chosen.label : '（没有可选项）';
  if (chosen && chosen.color) label.style.color = chosen.color;

  btn.append(label, icon('chevron-down'));
  const chev = btn.querySelector('svg');
  chev.setAttribute('class', 'icon');

  const menu = document.createElement('div');
  menu.className = 'combo__menu';
  menu.setAttribute('role', 'listbox');
  items.forEach((it) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'combo__item';
    el.setAttribute('role', 'option');
    el.setAttribute('aria-selected', it.value === host._value ? 'true' : 'false');
    if (it.color) {
      const sw = document.createElement('span');
      sw.className = 'combo__swatch';
      sw.style.background = it.color;
      el.appendChild(sw);
    }
    const t = document.createElement('span');
    t.textContent = it.label;
    el.appendChild(t);
    el.onclick = (ev) => {
      ev.stopPropagation();
      host.classList.remove('is-open');
      btn.setAttribute('aria-expanded', 'false');
      buildSelect(host, items, it.value, onChange);
      if (onChange) onChange(it.value);
    };
    menu.appendChild(el);
  });

  btn.onclick = (ev) => {
    ev.stopPropagation();
    document.querySelectorAll('.combo.is-open').forEach((o) => {
      if (o !== host) {
        o.classList.remove('is-open');
        const b = o.querySelector('.combo__btn');
        if (b) b.setAttribute('aria-expanded', 'false');
      }
    });
    const open = host.classList.toggle('is-open');
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  };

  host.append(btn, menu);
}

document.addEventListener('click', () => {
  document.querySelectorAll('.combo.is-open').forEach((o) => {
    o.classList.remove('is-open');
    const b = o.querySelector('.combo__btn');
    if (b) b.setAttribute('aria-expanded', 'false');
  });
});

/* ── B4. 初始化 ──────────────────────────────────────────────── */
/** 控制台本体：浏览器模式与壳模式（壳里的 console 形态）走的是同一段代码，
    唯一差别是实时事件源 —— 由 connectWS() 自己判断，见下面的守卫。 */
async function bootConsole() {
  state.config = await api('/api/config');
  renderNameSelectors();
  renderGreeting();

  $('quick').innerHTML = '';
  QUICK.forEach((q) => {
    const b = document.createElement('button');
    b.className = 'chip chip--suggestion';
    b.type = 'button';
    b.textContent = q;
    b.onclick = () => { $('content').value = q; $('content').focus(); };
    $('quick').appendChild(b);
  });

  await Promise.all([loadDevices(), loadMessages(), loadVersion(), loadXiaomi()]);
  connectWS();
  setInterval(refreshTimes, 1000);
  refreshTimes();

  go(loadPage());
}

async function boot() {
  applyMode(loadMode(), false);

  // 壳模式（PC 端 WebView2 壳加载这个页面）：先给中性的加载态、发 web.ready，
  // 等宿主 host.hello 说清是「全屏弹窗」还是「控制台」再决定渲染哪套界面。
  // 浏览器模式完全不走这条分支 —— 见 static/shell.js。
  if (window.FM_SHELL && window.FM_SHELL.enabled) {
    return window.FM_SHELL.start(bootConsole);
  }

  await bootConsole();
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
  renderDevices();
  renderHomeDevices();
  renderXmPcPick();
  renderShotPicks();
}

/** 首页顶部的一排在线设备（只读展示；点一下去设备页） */
function renderHomeDevices() {
  const box = $('home-devices');
  box.innerHTML = '';

  const online = state.devices.filter((d) => d.online);
  const list = online.length ? online : state.devices;

  $('home-dev-empty').hidden = state.devices.length > 0;
  $('home-dev-count').textContent = state.devices.length
    ? `${online.length} 台在线 · 共 ${state.devices.length} 台` : '';

  $('home-sub').textContent = state.devices.length === 0
    ? '还没有设备接入。在 Windows PC 上跑起 Family Agent 就会出现在这里。'
    : (online.length
        ? `${online.length} 台电脑在线，发一条消息它们都会看到。`
        : '电脑都不在线，消息会在它们上线后送到。');

  list.forEach((d) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'tile';
    el.onclick = () => go('devices');

    const ic = document.createElement('span');
    ic.className = 'dev__icon';
    ic.appendChild(icon('desktop'));

    const mid = document.createElement('span');
    mid.className = 'grow';
    const nm = document.createElement('span');
    nm.className = 'tile__name';
    nm.textContent = d.name;
    const mt = document.createElement('span');
    mt.className = 'tile__meta';
    mt.style.display = 'block';
    mt.textContent = d.last_seen ? `最后在线 ${d.last_seen}` : '从未上线';
    mid.append(nm, mt);

    const st = document.createElement('span');
    st.className = 'tile__state';
    const dot = document.createElement('span');
    dot.className = 'presence ' + (d.online ? 'presence--online' : 'presence--offline');
    const stx = document.createElement('span');
    stx.textContent = d.online ? '在线' : '离线';
    st.append(dot, stx);

    el.append(ic, mid, st);
    box.appendChild(el);
  });
}

/* ── B5b. Windows 会话状态徽标 + 远程解锁（Phase 1）─────────────
   后端契约（已冻结，前端照此对接；不改任何路径、请求体与帧格式）：
     · 设备对象多 windows_state：logon_screen | locked | unlocked | unknown
       （另有 capabilities 数组；本轮不拿它做判断，免得老 Agent 没上报就点不动）
     · GET /api/config 多返回 permissions: [..., "device.unlock"]
     · POST /api/devices/{id}/unlock → {ok, request_id, expires_at}
       403 无权限 / 404 设备不存在 / 409 离线或状态不允许 / 429 限流（detail 是中文原因）
     · 结果不走 HTTP：/ws/web 推 unlock_result{device_id, request_id, status, reason}
       请求 30 秒过期，前端 35 秒兜底清掉 loading。

   权限：没有 device.unlock 就整个不显示解锁入口 —— 不做「永远点不动的按钮」。
   旧服务端（/api/config 里连 permissions 字段都没有）自动落在同一条分支上，界面不回归。
*/
const UNLOCK_EXPIRE_MS = 35000;      // 30s 过期 + 5s 冗余 → 兜底清 loading

/** Windows 会话状态 → 徽标。图标 + 文字双通道，颜色只是辅助 */
const WIN_STATE = {
  unlocked:     { label: '已登录',          icon: 'lock-open',   cls: 'winstate--unlocked' },
  locked:       { label: '已锁屏',          icon: 'lock-closed', cls: 'winstate--attention' },
  logon_screen: { label: 'Windows 登录界面', icon: 'person',     cls: 'winstate--attention' },
  unknown:      { label: '状态未知',        icon: 'info',        cls: 'winstate--unknown' },
};

/** unlock_result.reason 是机器码，界面必须说人话 */
const UNLOCK_REASON = {
  no_credential: 'PC 尚未配置 Windows 解锁凭据',
  expired:       '解锁请求已过期，请重试',
  replay:        '该请求已被使用过',
  not_mine:      '设备不匹配',
  bad_action:    '请求类型不合法',
  cp_error:      'PC 端解锁组件出错',
  timeout:       'PC 响应超时',
  ok:            'PC 已完成解锁',
};

/* 进行中的解锁请求：device_id → { phase, note, kind, requestId, timer }
   状态放这里而不是留在 DOM 上 —— WebSocket 每次设备变动都会重渲染整张卡片。 */
const unlockState = new Map();

/** 后端是否给了 device.unlock 权限（字段缺失 = 老服务端，不显示入口） */
function unlockUIAvailable() {
  const perms = state.config && state.config.permissions;
  return Array.isArray(perms) && perms.includes('device.unlock');
}

function unlockPending(id) {
  const st = unlockState.get(id);
  return !!st && (st.phase === 'sending' || st.phase === 'waiting');
}

/** 不能解锁的原因（空串 = 可以解锁） */
function unlockBlockReason(d) {
  if (!d.online) return '设备离线，无法远程解锁';
  const s = d.windows_state || 'unknown';
  if (s === 'unlocked') return '已登录，无需解锁';
  if (s === 'unknown') return 'Windows 会话状态未知，暂时不能解锁';
  return '';                                     // locked / logon_screen 才允许解锁
}

function buildWinStateBadge(raw) {
  const key = WIN_STATE[raw] ? raw : 'unknown';
  const meta = WIN_STATE[key];
  const el = document.createElement('span');
  el.className = 'winstate ' + meta.cls;
  el.dataset.winState = key;
  el.append(
    icon(meta.icon, 'icon icon--xs'),
    Object.assign(document.createElement('span'), { textContent: meta.label }),
  );
  el.title = 'Windows 会话状态：' + meta.label;
  return el;
}

/** 卡片上的说明行：进行中的文案优先，其次是「为什么点不了」 */
function unlockNoteFor(d) {
  const st = unlockState.get(d.device_id);
  if (st && st.note) return { kind: st.kind, text: st.note };
  if (!d.online) return null;                    // 「离线」状态行已经说了，不重复
  const s = d.windows_state || 'unknown';
  if (s === 'unknown') return { kind: 'info', text: 'Windows 会话状态未知，无法远程解锁' };
  return null;                                   // 已登录 / 可解锁：会话徽标已经说清楚
}

function fillDevNote(node, info) {
  node.innerHTML = '';
  if (!info || !info.text) { node.hidden = true; return; }
  node.hidden = false;
  node.classList.toggle('dev__note--busy', info.kind === 'pending');
  if (info.kind === 'pending') {
    node.appendChild(Object.assign(document.createElement('span'),
      { className: 'progress-ring progress-ring--sm' }));
  } else {
    node.appendChild(icon('info', 'icon icon--xs'));
  }
  node.appendChild(Object.assign(document.createElement('span'),
    { className: 'dev__note-text', textContent: info.text }));
}

function renderDevices() {
  const box = $('devices');
  box.innerHTML = '';
  $('dev-empty').hidden = state.devices.length > 0;

  const online = state.devices.filter((d) => d.online).length;
  $('dev-count').textContent = state.devices.length
    ? `${online} 台在线 · 共 ${state.devices.length} 台` : '还没有设备注册';

  state.devices.forEach((d) => {
    const el = document.createElement('article');
    el.className = 'dev' + (d.online ? ' online' : '');

    // 头部：图标 + 名称 + 状态（小型 presence + 文字，不用 emoji）
    const top = document.createElement('div');
    top.className = 'dev__top';

    const ic = document.createElement('span');
    ic.className = 'dev__icon';
    ic.appendChild(icon('desktop'));

    const nm = document.createElement('span');
    nm.className = 'dev__name';
    nm.textContent = d.name;
    nm.title = d.name;

    const st = document.createElement('span');
    st.className = 'dev__state';
    const dot = document.createElement('span');
    dot.className = 'presence ' + (d.online ? 'presence--online' : 'presence--offline');
    const stx = document.createElement('span');
    stx.textContent = d.online ? '在线' : '离线';
    st.append(dot, stx);

    top.append(ic, nm, st);

    // Windows 会话状态：和「在线」是两件事（在线也可能锁着屏），所以单独一行。
    // 设备离线时不猜会话状态 —— 卡片上只有「离线」。
    const session = document.createElement('div');
    session.className = 'dev__session';
    if (d.online) session.appendChild(buildWinStateBadge(d.windows_state));
    session.hidden = !d.online;

    const meta = document.createElement('div');
    meta.className = 'dev__meta';
    meta.textContent =
      (d.type === 'pc' ? 'Windows PC' : d.type) +
      (d.platform ? ` · ${d.platform.split(/\s+/)[0]}` : '') +
      (d.last_seen ? ` · 最后在线 ${d.last_seen}` : ' · 从未上线');

    // 解锁的进度/原因文案：紧挨着按钮上方一行（Fluent Caption，不抢层级）
    const note = document.createElement('div');
    note.className = 'dev__note';
    note.dataset.unlockNote = d.device_id;

    const acts = document.createElement('div');
    acts.className = 'dev__acts';

    // 查看桌面：次要操作 → Secondary
    // （「对话」入口已随设备间投递语义移除：消息只有一个共同的群聊空间）
    const shotBtn = mkBtn('桌面', 'camera', 'btn--secondary', () => deviceAction(d, 'shot'));
    if (!d.online) shotBtn.disabled = true;
    acts.appendChild(shotBtn);

    // 远程解锁：高风险操作（会真的把电脑解锁到桌面），一律次要/描边样式，
    // 不用和「发送」一样的实心主按钮。没有 device.unlock 权限时整个入口不出现。
    if (unlockUIAvailable()) {
      const why = unlockBlockReason(d);
      const pending = unlockPending(d.device_id);
      const ub = mkBtn('远程解锁', 'lock-open', 'btn--secondary', () => doUnlock(d));
      ub.dataset.unlockBtn = d.device_id;
      ub.disabled = !!why || pending;
      ub.title = why || `向「${d.name}」下发一次性解锁请求（PC 用本机 Windows 凭据解锁）`;
      if (pending) {
        // 请求进行中：图标换成小进度环，按钮锁住防连点
        ub.querySelector('.btn__icon').replaceWith(
          Object.assign(document.createElement('span'),
            { className: 'progress-ring progress-ring--sm' }));
      }
      acts.appendChild(ub);

      // 禁用原因 / 进行中的文案：能一眼看出「为什么点不了」和「走到哪一步了」
      fillDevNote(note, unlockNoteFor(d));
    }

    // 开机：只在有米家绑定时出现；已在线则禁用（不去动插座）
    if (d.xiaomi) {
      const verb = d.xiaomi.power_action === 'off' ? '关闭' : '开启';
      const wake = mkBtn('开机', 'power', 'btn--primary', () => deviceAction(d, 'wake'));
      wake.disabled = !!d.online;
      wake.title = `执行米家「${d.xiaomi.name}」的${verb}动作`
        + (d.online ? '（设备已在线，无需开机）' : '');
      acts.appendChild(wake);
    }

    // 关机：危险操作 → error 色
    const off = mkBtn('关机', 'power', 'btn--danger', () => deviceAction(d, 'shutdown'));
    if (!d.online) off.disabled = true;
    acts.appendChild(off);

    el.append(top, session, meta, note, acts);
    box.appendChild(el);
  });
}

function mkBtn(text, iconName, variant, onclick) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn ' + variant;
  b.append(icon(iconName, 'btn__icon'), Object.assign(document.createElement('span'), { textContent: text }));
  b.onclick = onclick;
  return b;
}

/* ── B6. 设备动作 ────────────────────────────────────────────── */
async function deviceAction(d, act) {
  if (act === 'shot') {
    openShot(d);

  } else if (act === 'wake') {
    // 不弹确认：这套绑定常见用法是「用插座断电来触发 BIOS 上电开机」，
    // 断电后插座会自动恢复供电，提醒「电脑会掉电」既误导又多余。
    const dismiss = snack('正在执行米家动作……');
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
      iconName: 'i-power',
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

/* ── B6b. 远程解锁流程 ────────────────────────────────────────
   点按钮 → POST 一次 → 结果完全由 WebSocket 推回。全程不弹第二个密码框。 */
function setUnlockPhase(id, phase, note, opts = {}) {
  const prev = unlockState.get(id);
  if (prev && prev.timer) clearTimeout(prev.timer);
  if (phase === 'idle') { unlockState.delete(id); renderDevices(); return; }

  const st = {
    phase,
    note: note || '',
    kind: opts.kind || 'info',
    requestId: opts.requestId || (prev && prev.requestId) || '',
    timer: null,
  };
  if (opts.timeout) st.timer = setTimeout(() => onUnlockTimeout(id, st.requestId), opts.timeout);
  unlockState.set(id, st);
  renderDevices();
}

/** 35 秒还没等到结果 → 撤掉 loading，别让界面永远停在中间态 */
function onUnlockTimeout(id, requestId) {
  const st = unlockState.get(id);
  if (!st || st.requestId !== requestId || !unlockPending(id)) return;
  setUnlockPhase(id, 'timeout', '解锁请求已超时，可以重试');
  snack('解锁请求超时：PC 一直没回应（请求 30 秒过期）', { error: true, action: '知道了' });
  // 超时文案留一会儿就撤掉，不长期挂在卡片上
  setTimeout(() => {
    const cur = unlockState.get(id);
    if (cur && cur.phase === 'timeout') { unlockState.delete(id); renderDevices(); }
  }, 12000);
}

/** 点击「远程解锁」：沿用当前 Web 会话下发，不需要再输一次口令 */
async function doUnlock(d) {
  if (unlockPending(d.device_id)) return;
  const why = unlockBlockReason(d);
  if (why) { snack(why, { error: true }); return; }

  setUnlockPhase(d.device_id, 'sending', '正在发送解锁请求…', { kind: 'pending' });
  try {
    const r = await api(`/api/devices/${d.device_id}/unlock`, { method: 'POST' });
    setUnlockPhase(d.device_id, 'waiting', '已下发解锁请求，等待 PC 执行…',
      { kind: 'pending', requestId: (r && r.request_id) || '', timeout: UNLOCK_EXPIRE_MS });
  } catch (e) {
    // 403 / 404 / 409 / 429 的 detail 就是中文原因，原样提示
    setUnlockPhase(d.device_id, 'idle');
    snack('解锁失败：' + (e.message || '未知错误'), { error: true, action: '知道了' });
  }
}

/** /ws/web 推来的 unlock_result */
function onUnlockResult(msg) {
  const id = msg.device_id || '';
  const dev = state.devices.find((x) => x.device_id === id);
  const who = dev ? dev.name : id;
  const rid = msg.request_id || '';
  const cur = unlockState.get(id);
  // 别人（另一个浏览器）发起的请求，不该动本地的 loading 状态
  const mine = !rid || !cur || !cur.requestId || cur.requestId === rid;

  if (msg.status === 'armed') {
    setUnlockPhase(id, 'waiting', 'PC 已接收，正在解锁…',
      { kind: 'pending', requestId: rid, timeout: UNLOCK_EXPIRE_MS });
    return;
  }

  if (mine) setUnlockPhase(id, 'idle');          // 无论成败，按钮恢复可用

  if (msg.status === 'success') {
    snack(`「${who}」解锁成功`);
    loadDevices();                               // 会话状态会变，重新拉一次设备
    return;
  }

  const reason = UNLOCK_REASON[msg.reason] || 'PC 端没能完成解锁';
  snack(`「${who}」解锁失败：${reason}`, { error: true, action: '知道了' });
  loadDevices();
  renderDevices();
}

/* ── B7. 消息 ────────────────────────────────────────────────── */
/** 发一条消息进群聊：只带昵称 + 内容，服务端自动广播给所有已注册设备。 */
async function sendMessage() {
  const content = $('content').value.trim();
  if (!content) { snack('先写点什么再发吧', { error: true }); return; }

  const btn = $('btn-send');
  btn.disabled = true;
  $('send-hint').textContent = '发送中……';
  try {
    // 群聊模型：不传 targets（没有「发送给」这一步）。字段服务端保留了但会忽略。
    const r = await api('/api/messages', {
      method: 'POST',
      body: JSON.stringify({
        sender_name: currentSender(),
        content,
      }),
    });
    $('content').value = '';
    // offline 只是「本次即时推送没送到的设备」，不是消息状态 —— 它们上线后照样会看到。
    const off = r.offline || [];
    $('send-hint').textContent = '';
    snack(off.length
      ? `已发送（${off.length} 台设备离线，上线后也能看到）`
      : '已发送，在线的设备都能看到', { action: '知道了' });
    upsertMessage(r.message);
  } catch (e) {
    $('send-hint').textContent = '';
    snack('发送失败：' + e.message, { error: true });
  } finally {
    btn.disabled = false;
  }
}

/** 聊天界面的阅读顺序：**旧 → 新，最新的在最下面**。
    /api/messages 给的是新 → 旧（接口没变），展示前统一翻过来。
    群聊是聊天界面，新消息从下面长出来才符合直觉。 */
function chatOrder(list) {
  return (list || []).slice().sort((a, b) => (Number(a.id) || 0) - (Number(b.id) || 0));
}

/** 滚到最新一条。消息页没显示时不动（群聊/首页渲染不该把页面拽走） */
function scrollLogToEnd() {
  const log = $('log');
  if (!log) return;
  const sec = log.closest('section');
  if (!sec || sec.hidden) return;
  const last = log.lastElementChild;
  if (last) last.scrollIntoView({ block: 'end' });
}

async function loadMessages(opts) {
  state.messages = await api(`/api/messages?limit=${state.limit}`);
  renderMessages(!(opts && opts.keepScroll));
}

function upsertMessage(msg) {
  const i = state.messages.findIndex((m) => m.id === msg.id);
  if (i >= 0) state.messages[i] = msg; else state.messages.push(msg);
  renderMessages(true);
}

/** 一条消息的 DOM —— 直接交给共用的群聊组件（static/chat.js）。
    网页端「消息」页、首页「最近消息」、PC 端客户端窗口因此长得**完全一样**：
    别人发的靠左，自己发的靠右。
    靠右只认「昵称 == 我当前用的昵称」（群聊模型：空间里完全以昵称区分），
    不看 device_id —— 设备不是身份。 */
function buildMsgEl(m) {
  return FMChat.row(m, { myName: currentSender(), status: STATUS_SENT });
}

function renderMessages(toEnd) {
  const box = $('log');
  box.textContent = '';
  $('log-empty').hidden = state.messages.length > 0;
  chatOrder(state.messages).forEach((m) => box.appendChild(buildMsgEl(m)));
  renderHomeRecent();
  if (toEnd !== false) scrollLogToEnd();
}

/** 首页只放最近 3 条（同样按聊天顺序：旧→新），完整列表在「消息」页 */
function renderHomeRecent() {
  const box = $('home-recent');
  box.textContent = '';
  const list = chatOrder(state.messages).slice(-3);
  $('home-recent-empty').hidden = list.length > 0;
  list.forEach((m) => box.appendChild(buildMsgEl(m)));
}

/* ── B8. 截图页 ──────────────────────────────────────────────── */
let shotDevice = null;

/** 截图页顶部的设备选择（复用设备数据，不额外请求） */
function renderShotPicks() {
  const box = $('shot-picks');
  box.innerHTML = '';

  if (state.devices.length === 0) {
    const hint = document.createElement('span');
    hint.className = 'text-caption text-tertiary';
    hint.textContent = '还没有设备可用';
    box.appendChild(hint);
  }

  state.devices.forEach((d) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'chip';
    el.setAttribute('aria-pressed', shotDevice && shotDevice.device_id === d.device_id ? 'true' : 'false');
    const dot = document.createElement('span');
    dot.className = 'presence ' + (d.online ? 'presence--online' : 'presence--offline');
    const t = document.createElement('span');
    t.textContent = d.name;
    el.append(dot, t);
    el.onclick = () => openShot(d);
    box.appendChild(el);
  });

  $('shot-again').disabled = !shotDevice;

  if (!shotDevice) {
    $('shot-empty').hidden = false;
    $('shot-loading').hidden = true;
    $('shot-img').hidden = true;
    $('shot-title').textContent = '还没有选择设备';
    $('shot-meta').textContent = '';
  }
}

async function openShot(d) {
  shotDevice = d;
  go('shot');                     // go() 里会 renderShotPicks()，标记选中的设备
  $('shot-title').textContent = `${d.name} · 桌面`;
  $('shot-meta').textContent = '';
  await fetchShot();
}

async function fetchShot() {
  if (!shotDevice) return;
  const d = shotDevice;
  const img = $('shot-img');
  img.hidden = true;
  img.removeAttribute('src');
  $('shot-empty').hidden = true;
  $('shot-loading').hidden = false;
  $('shot-loading').classList.remove('is-error');
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
    $('shot-loading').classList.add('is-error');
    $('shot-loading-text').textContent = '截图失败：' + e.message;
    $('shot-meta').textContent = '';
  }
}

/* ── B10. WebSocket 实时事件 ─────────────────────────────────── */
function setConn(on) {
  $('ws-dot').className = 'presence ' + (on ? 'presence--online' : 'presence--offline');
  $('ws-text').textContent = on ? '已连接' : '连接断开，重连中…';
}

function connectWS() {
  // 壳模式：宿主持有 WebSocket（断线重连/离线队列都在它那边），页面**不再**连
  // /ws/web —— 否则同一条消息会进来两份。实时事件改走桥，由 shell.js 分流到
  // 下面这个 handleServerFrame()，帧格式与之完全一致。
  if (window.FM_SHELL && window.FM_SHELL.enabled) return;

  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(`${proto}://${location.host}${BASE}/ws/web`);
  ws.onopen = () => setConn(true);
  ws.onclose = () => { setConn(false); setTimeout(connectWS, 3000); };
  ws.onerror = () => ws.close();
  ws.onmessage = (ev) => {
    let d; try { d = JSON.parse(ev.data); } catch (_) { return; }
    handleServerFrame(d);
  };
}

/** 服务端实时帧的唯一处理入口（浏览器模式来自 /ws/web；壳模式由桥喂进来） */
function handleServerFrame(d) {
  if (!d || typeof d !== 'object') return;
  if (d.type === 'device_status' || d.type === 'device_updated' || d.type === 'device_deleted') {
    loadDevices();
  } else if (d.type === 'unlock_result') {
    // 解锁结果只从 WebSocket 回来（不走 HTTP），设备名与请求配对都在 onUnlockResult 里
    onUnlockResult(d);
  } else if (d.type === 'message') {
    upsertMessage(d.message);
  } else if (d.type === 'message_status') {
    // 逐设备状态上报（ack / popup_displayed）链路服务端仍然保留，但群聊模型下
    // 一条消息只有一个「已发送」——所以这里**故意什么都不做**，不要"修好"它。
  } else if (d.type === 'wake') {
    snack(`${d.result.plug || '米家设备'} 已开启，等待 PC 上线……`);
  } else if (d.type === 'xiaomi') {
    loadXmBound();
  } else if (d.type === 'shutdown_sent') {
    const dev = state.devices.find((x) => x.device_id === d.device_id);
    snack(`「${dev ? dev.name : d.device_id}」关机指令已下发`);
  }
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

/** 把本地昵称同步到发送区那个下拉框（群聊模型下只有这一个） */
function renderNameSelectors() {
  const items = names.map((n) => ({ value: n, label: n, color: nickColor(n) }));

  buildSelect($('sender-sel'), items, currentSender(), (v) => rememberSender(v));

  renderNamesList();

  // 壳模式：全屏弹窗里那个「以谁的名义回复」的下拉也要跟着改
  if (window.FM_SHELL && window.FM_SHELL.enabled) window.FM_SHELL.syncNames();
}

function renderNamesList() {
  const box = $('names-list');
  box.innerHTML = '';
  if (names.length === 0) {
    const p = document.createElement('p');
    p.className = 'text-caption text-secondary';
    p.textContent = '还没有昵称，先添加一个吧。';
    box.appendChild(p);
    return;
  }

  names.forEach((name, idx) => {
    const row = document.createElement('div');
    row.className = 'name-row';

    const sw = document.createElement('span');
    sw.className = 'name-row__swatch';
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
    del.className = 'icon-btn icon-btn--danger';
    del.setAttribute('aria-label', `删除昵称 ${name}`);
    del.appendChild(icon('delete', 'icon icon--sm'));
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
let xmDevices = [];        // 从米家云端发现的设备
let xmBoundList = [];      // 已建立的绑定规则
let xmSearch = '';         // 设备搜索关键字
let xmProps = [];          // 当前选中设备的可控属性
const specCache = {};      // urn -> 可控属性列表（服务端已缓存，这里再缓存一层）
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
    ? '已授权米家。绑定规则：米家设备 + 属性/值 → 某台 PC（只影响那台 PC 上的「开机」按钮）'
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
    iconName: 'i-sign-out',
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

/* ── 可控属性：不再局限于开/关 ───────────────────────────────
   属性表来自米家公开的 miot-spec（与官方 ha_xiaomi_home 同一份数据），
   取其中 access 含 write 的属性，连同它的可选值一起给界面用。 */

async function loadSpec(urn) {
  const key = (urn || '').trim();
  if (!key) return [];
  if (specCache[key]) return specCache[key];
  try {
    const d = await api('/api/xiaomi/spec?urn=' + encodeURIComponent(key));
    specCache[key] = d.props || [];
  } catch (e) {
    specCache[key] = [];
    snack('查不到这个型号的可控属性：' + e.message, { error: true });
  }
  return specCache[key];
}

const specOf = (urn) => specCache[(urn || '').trim()] || [];

/** 某个属性可以取哪些值 */
function valueItems(prop) {
  if (!prop) return [];
  if (prop.options && prop.options.length) {
    return prop.options.map((o) => ({ value: o.value, label: String(o.label) }));
  }
  const r = prop.range;
  if (Array.isArray(r) && r.length >= 2) {
    const min = Number(r[0]);
    const max = Number(r[1]);
    let step = Number(r[2] || 1) || 1;
    // 档位太多（比如亮度 1~100）就把步长放粗，保证整个区间都能选到，
    // 而不是截断成前 60 个值
    if ((max - min) / step > 60) step = Math.max(1, Math.ceil((max - min) / 60));
    const label = (v) => String(v) + (prop.unit ? ' ' + prop.unit : '');
    const out = [];
    for (let v = min; v <= max && out.length < 80; v += step) {
      out.push({ value: v, label: label(v) });
    }
    if (out.length && out[out.length - 1].value !== max) {
      out.push({ value: max, label: label(max) });     // 补齐上界
    }
    return out.length ? out : [{ value: min, label: label(min) }];
  }
  return [{ value: true, label: '写入 true' }, { value: false, label: '写入 false' }];
}

const propItems = (props) => props.map((p) => ({
  value: p.siid + ':' + p.piid,
  label: `${p.service} · ${p.name}`,
}));

const propOf = (props, key) => {
  const [siid, piid] = String(key || '').split(':').map(Number);
  return props.find((p) => p.siid === siid && p.piid === piid) || null;
};

function valueLabel(prop, value) {
  const hit = valueItems(prop).find((o) => o.value === value);
  if (hit) return hit.label;
  return value === true ? '开' : value === false ? '关' : String(value);
}

/* ── 新增绑定表单 ───────────────────────────────────────────── */

/** 按搜索关键字过滤设备 */
function filteredDevices() {
  const kw = xmSearch.trim().toLowerCase();
  if (!kw) return xmDevices;
  return xmDevices.filter((d) =>
    String(d.name || '').toLowerCase().includes(kw) ||
    String(d.model || '').toLowerCase().includes(kw) ||
    String(d.miot_device_id || '').toLowerCase().includes(kw));
}

function renderXmForm() {
  const pool = filteredDevices();
  const devItems = pool.length
    ? pool.map((d) => ({
        value: d.miot_device_id,
        label: (d.name || d.miot_device_id) + (d.is_online === false ? '（离线）' : ''),
      }))
    : [{ value: '', label: xmDevices.length ? '（没有匹配的设备）' : '（先点「刷新米家设备」）' }];

  const keep = $('xm-dev-pick')._value;
  const val = devItems.some((i) => i.value === keep) ? keep : devItems[0].value;
  buildSelect($('xm-dev-pick'), devItems, val, () => onXmDeviceChange());
  renderXmPropPick();
  renderXmPcPick();
  // 首次渲染时也拉一次规格，否则属性下拉一直停在占位文案上
  if (!xmProps.length && val) onXmDeviceChange();
}

/** 「关联到哪台 PC」——只有被关联的那台才会出现「开机」按钮 */
function renderXmPcPick() {
  const items = [{ value: '', label: '不指定 PC' }].concat(
    state.devices.map((d) => ({ value: d.device_id, label: d.name })));
  const keep = $('xm-pc-pick')._value;
  const val = items.some((i) => i.value === keep) ? keep : items[0].value;
  buildSelect($('xm-pc-pick'), items, val, null);
}

async function onXmDeviceChange() {
  const mid = $('xm-dev-pick')._value;
  const dev = xmDevices.find((d) => d.miot_device_id === mid);
  xmProps = dev ? await loadSpec(dev.model) : [];
  renderXmPropPick();
}

function renderXmPropPick() {
  const items = xmProps.length
    ? propItems(xmProps)
    : [{ value: '', label: '（选好设备后自动加载）' }];
  const keep = $('xm-prop-pick')._value;
  const val = items.some((i) => i.value === keep) ? keep : items[0].value;
  buildSelect($('xm-prop-pick'), items, val, () => renderXmValPick());
  renderXmValPick();
}

function renderXmValPick() {
  const prop = propOf(xmProps, $('xm-prop-pick')._value);
  const items = valueItems(prop);
  const shown = items.length ? items : [{ value: '', label: '（无可选值）' }];
  const keep = $('xm-val-pick')._value;
  const val = items.some((i) => i.value === keep) ? keep : shown[0].value;
  buildSelect($('xm-val-pick'), shown, val, null);
}

async function xmAdd() {
  const mid = $('xm-dev-pick')._value;
  if (!mid) { snack('先选一个米家设备（点「刷新米家设备」）', { error: true }); return; }

  const prop = propOf(xmProps, $('xm-prop-pick')._value);
  if (!prop) { snack('先选一个要控制的属性', { error: true }); return; }

  const value = $('xm-val-pick')._value;
  const dev = xmDevices.find((d) => d.miot_device_id === mid) || {};
  const target = $('xm-pc-pick')._value || '';

  try {
    await api('/api/xiaomi/devices', {
      method: 'POST',
      body: JSON.stringify({
        name: dev.name || mid,
        miot_device_id: mid,
        urn: dev.model || '',
        device_type: 'plug',
        power_siid: prop.siid,
        power_piid: prop.piid,
        power_value: value,
        // 旧字段保留，便于老数据/老版本兼容
        power_action: value === false ? 'off' : 'on',
        target_device_id: target,
      }),
    });
    snack(`已绑定：${prop.name} = ${valueLabel(prop, value)}`);
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
  // 预取每个绑定设备的规格，否则显示不出属性名和可选值
  await Promise.all(xmBoundList.map((r) => loadSpec(r.urn)));
  renderXmBound();
}

function renderXmBound() {
  const box = $('xm-bound');
  box.innerHTML = '';
  if (!xmBoundList.length) {
    const p = document.createElement('p');
    p.className = 'text-caption text-secondary';
    p.textContent = '还没有绑定。上面选好设备、属性和目标值，再选关联的 PC，点「添加绑定」。';
    box.appendChild(p);
    return;
  }

  const sec = document.createElement('div');
  sec.className = 'setting-group__title';
  sec.style.marginTop = 'var(--spacing-lg)';
  sec.textContent = '已建立的绑定（改完即时生效）';
  box.appendChild(sec);

  const pcItems = [{ value: '', label: '不指定 PC' }].concat(
    state.devices.map((d) => ({ value: d.device_id, label: d.name })));

  xmBoundList.forEach((row) => {
    const specs = specOf(row.urn);
    const nowKey = row.power_siid + ':' + row.power_piid;
    const rowProp = propOf(specs, nowKey)
      || { siid: row.power_siid, piid: row.power_piid, name: '属性', service: '', format: 'bool' };

    const readValue = () => {
      const raw = row.power_value;
      if (raw === null || raw === undefined || raw === '') {
        return (row.power_action || 'on').toLowerCase() !== 'off';
      }
      try { return JSON.parse(raw); } catch (_) { return raw; }
    };
    const cur = readValue();

    const el = document.createElement('div');
    el.className = 'xm-row';

    const meta = document.createElement('div');
    meta.className = 'xm-row__meta';
    const n = document.createElement('div');
    n.className = 'xm-row__name';
    n.textContent = row.name;
    const dd = document.createElement('div');
    dd.className = 'xm-row__desc';
    dd.textContent = specs.length
      ? `${rowProp.service ? rowProp.service + ' · ' : ''}${rowProp.name} = ${valueLabel(rowProp, cur)}`
      : `${row.urn || '未知型号'} · siid=${row.power_siid} piid=${row.power_piid} = ${valueLabel(rowProp, cur)}`;
    meta.append(n, dd);

    const acts = document.createElement('div');
    acts.className = 'xm-row__acts';

    // 属性
    const propPick = document.createElement('div');
    propPick.className = 'combo';
    const pItems = specs.length ? propItems(specs)
      : [{ value: nowKey, label: `${rowProp.name}（siid=${row.power_siid} piid=${row.power_piid}）` }];
    buildSelect(propPick, pItems, nowKey, (v) => {
      const [siid, piid] = v.split(':').map(Number);
      xmPatch(row, { power_siid: siid, power_piid: piid },
        `已改属性：${propOf(specs, v)?.name || v}`);
    });

    // 值
    const valPick = document.createElement('div');
    valPick.className = 'combo';
    const vItems = valueItems(rowProp);
    buildSelect(valPick, vItems.length ? vItems : [{ value: '', label: '（无可选值）' }], cur,
      (v) => xmPatch(row, { power_value: v },
        `已改值：${valueLabel(rowProp, v)}`));

    // 关联 PC
    const pcPick = document.createElement('div');
    pcPick.className = 'combo';
    buildSelect(pcPick, pcItems, row.target_device_id || '',
      (v) => xmPatch(row, { target_device_id: v }, '已改关联 PC'));

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'btn btn--danger';
    del.append(icon('delete', 'btn__icon'),
               Object.assign(document.createElement('span'), { textContent: '删除' }));
    del.onclick = () => xmUnbind(row);

    acts.append(propPick, valPick, pcPick, del);
    el.append(meta, acts);
    box.appendChild(el);
  });
}

async function xmPatch(row, fields, okText) {
  try {
    await api(`/api/xiaomi/devices/${row.id}`, { method: 'PATCH', body: JSON.stringify(fields) });
    Object.assign(row, fields);
    if (fields.power_value !== undefined) {
      row.power_value = JSON.stringify(fields.power_value);
    }
    snack(okText || '已更新');
    renderXmBound();
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

/* ── B13. 设置页 ─────────────────────────────────────────────── */
/** 进入设置页时刷新这一段内容（设备列表可能变了） */
function enterSettings() {
  renderModeRow();
  renderNamesList();
  loadXiaomi();
}

function renderModeRow() {
  const box = $('mode-row');
  box.innerHTML = '';
  const cur = loadMode();
  MODES.forEach((m) => {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'choice';
    b.setAttribute('role', 'radio');
    b.setAttribute('aria-checked', m.id === cur ? 'true' : 'false');
    b.append(icon(m.icon, 'icon icon--sm'),
             Object.assign(document.createElement('span'), { textContent: m.name }));
    b.onclick = () => { applyMode(m.id, true); renderModeRow(); };
    box.appendChild(b);
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
$('btn-history').onclick = () => { state.limit += 30; loadMessages({ keepScroll: true }); };
$('home-more').onclick = () => go('messages');

/* 导航（左导航 + 底部导航共用同一套 data-page 按钮） */
document.querySelectorAll('[data-page]').forEach((b) => {
  b.onclick = () => go(b.dataset.page);
});
$('nav-toggle').onclick = () => {
  if ($('app-shell').classList.contains('nav-open')) closeNavDrawer(); else openNavDrawer();
};
$('nav-scrim').onclick = closeNavDrawer;

$('shot-again').onclick = fetchShot;

$('content').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) sendMessage();
});

$('btn-settings').onclick = () => go('settings');
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
$('xm-search').addEventListener('input', (e) => {
  xmSearch = e.target.value || '';
  renderXmForm();
});
$('xm-code').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); xmExchange(); }
});

boot().catch((e) => {
  console.error(e);
  snack('初始化失败：' + e.message, { error: true, duration: 12000 });
});
