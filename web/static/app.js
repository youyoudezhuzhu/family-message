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
 * 追加（发送区收成一行，纯表现层）：首页底部的发送卡片去掉标题行 / 快捷短语 /
 *   「Ctrl+Enter」提示 / 群聊说明，只剩与 PC 端回复栏同构的一条横向行 ——
 *   左昵称下拉 · 中输入框（占满中间最宽）· 右发送。输入框改单行 <input>，
 *   裸回车即发送（组字中的回车不算，见文件末尾的事件绑定）；
 *   请求体、API 路径、字段校验一律没动。
 */

/* 消息状态：群聊模型下只有一种 —— 已发送。
   逐设备状态表（created / server_received / device_received / popup_displayed / read）
   及其颜色映射已随「设备间投递」模型一起删除。 */
const STATUS_SENT = { cls: 'status--sent', icon: 'check', label: '已发送' };

/* 裸端口访问时 BASE=""；走飞牛网关时 BASE="/app/family-message"。 */
const BASE = (() => {
  let p = location.pathname.replace(/\/index\.html$/, '');
  if (p.endsWith('/')) p = p.slice(0, -1);
  return p;
})();

const $ = (id) => document.getElementById(id);
/* 统一的 API 调用。错误分两种形状（Phase 3 起要区分机器可读的 `code`）：
   · 字符串 detail（老接口）：直接当 message；
   · 对象 detail（昵称接口：{code, message, ...}）：message 给人看，code 给代码分支用
     （例如 409 NICKNAME_ALREADY_EXISTS → UI 引导「直接选用它」）。 */
const api = async (path, opts = {}) => {
  let res;
  try {
    res = await fetch(BASE + path, {
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
      ...opts,
    });
  } catch (e) {
    // fetch 本身失败 = 断网 / 服务不可达：给一个明确的「必须在线」提示（§4 硬约束）
    const err = new Error('连不上服务器');
    err.code = 'OFFLINE';
    throw err;
  }
  if (res.status === 401) { askPassword(); throw new Error('需要访问口令'); }
  if (!res.ok) {
    let detail = res.statusText;
    try { detail = (await res.json()).detail || detail; } catch (_) {}
    const e = new Error(
      typeof detail === 'string' ? detail : (detail && detail.message) || res.statusText);
    if (detail && typeof detail === 'object') {
      e.code = detail.code;
      e.data = detail;
      if (detail.existing_nickname_id != null) e.existing_nickname_id = detail.existing_nickname_id;
    }
    throw e;
  }
  return res.json();
};

/* limit 就是「首页一次拉多少条」—— 首页现在自己就是完整列表（可滚动），
   不再有「查看全部」跳转，所以直接要服务端允许的上限 200
   （main.py 的 api_messages 里 limit = min(limit, 200)）。 */
let state = { config: null, devices: [], messages: [], limit: 200 };

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
  // 昵称圆点 / 头像色是**按主题算**的（§4.4 规则 1）→ 换主题要按新主题重画一遍。
  // （nickEnabled 由 bootConsole 设好；开机时这里还是 false，不做事。）
  if (nickEnabled) redrawNicks();
}

// 系统主题变化时，只有「跟随系统」需要跟着变
mqDark.addEventListener('change', () => {
  if (loadMode() === 'system') { applyMode('system', false); renderModeRow(); }
});

/* ── A2. NavigationView 路由（纯前端切页，不涉及任何后端路由）──
   「消息」页已删除：首页的「家庭消息」直接渲染整条群聊流并且可滚动，
   所以导航里不再需要第二个看消息的入口。localStorage 里残留的
   'messages'（老版本存下的当前页）会由 go() 兜底回首页，不会白屏。 */
const PAGES = ['home', 'devices', 'shot', 'settings'];
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

  // 切页时把内容区滚回顶部。
  // ★ 不能用 window.scrollTo：.main 现在是**固定高度的内部滚动容器**（style.css §4），
  //   document 本身不滚了，调 window.scrollTo 等于什么都没做（切页后停在上一页的位置）。
  //   到底滚哪个是可测的：看 window.scrollY 恒为 0、.main.scrollTop 才是那个会变的量。
  const mainEl = $('main');
  if (mainEl) mainEl.scrollTop = 0;
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

/* ── A3. （原「首页问候语」区块已删除）─────────────────────────
   首页顶部那句「早上好 / 夜深了 …」和「N 台电脑在线，发一条消息它们都会看到。」
   整块去掉了 —— 设备数量在「在线设备」标题右边已经写着，问候语属于重复信息。
   greetingText() / renderGreeting() 一并删掉，避免留下没人调的死代码。 */

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
  label.className = 'combo__label';   // ★ v0.19.1：名字按类定宽（不能再靠 span:first-child，见 style.css 注释）
  label.textContent = chosen ? chosen.label : '（没有可选项）';
  /* §4.4 规则 4：**不用色值当字色** —— 昵称颜色（逻辑色 ID 经 §4.4 算出来的显示色）
     在「已选项」这里走「色块 + 常规字色」。item.swatch 只有共享昵称路径会传
     （老路径 item.color 是本地哈希色，保持原来的彩色字 = 逐像素回退）。 */
  if (chosen && chosen.color && chosen.swatch) {
    const sw = document.createElement('span');
    sw.className = 'combo__swatch';
    sw.style.background = chosen.color;      // 值来自 §4.4 映射表，绝不透传后端原串
    btn.appendChild(sw);
  } else if (chosen && chosen.color) {
    label.style.color = chosen.color;
  }

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
  // 共享昵称开关：字段缺失 = false = 完全走今天的本地哈希路径（§7 回退）
  nickEnabled = !!(state.config && state.config.nickname_enabled);
  nickPoolVersion = (state.config && state.config.color_pool_version) || 0;
  dropLegacyNamesKey();                       // 旧 key 只删不读（不再有第二真相源）
  if (nickEnabled) await loadColorTable();     // ★ v0.19：色表以服务端为准（内置表只兜底）
  if (nickEnabled) await loadNicknames();      // 拉整表（空库 → 空数组，服务端不建任何行）
  redrawNicks();

  await Promise.all([loadDevices(), loadMessages(), loadVersion(), loadXiaomi()]);
  connectWS();
  setInterval(refreshTimes, 1000);
  refreshTimes();

  go(loadPage());
}

async function boot() {
  applyMode(loadMode(), false);
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
  renderXmPcPick();
  renderShotPicks();
}

/* ★ v0.19.1：`renderHomeDevices()` 整块删掉了 —— 首页顶部那块「在线设备」和侧栏「设备」页
   是同一份数据、同一个去处（点一下都是 go('devices')），用户要求去掉重复。
   设备列表现在只有一处渲染：renderDevices()（设备页 / #devices）。 */

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
  cp_missing:    'PC 已收到请求、凭据也已就绪，但本机还缺「真正施加解锁」的组件'
                 + '（Credential Provider 阶段）—— 请等这一步做完',
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

/**
 * 不能解锁的原因（空串 = 可以下发）。
 *
 * ★ 只有**离线**才真拦。Windows 会话状态**不再**用来禁用按钮，只用来提示：
 *   · 状态来自心跳，可能滞后十几秒；
 *   · 同一台机器可能有两个实例（登录前的 headless / 登录后的交互式），
 *     曾经因此报出一个"未锁屏"的旧值，按钮灰着、用户想验证都没法验证；
 *   · 需求就是"能验证这条路" —— 显示不准的状态挡在门口比点一下失败更糟。
 *   于是改成：仍可点，但先二次确认（见 unlockWarnReason / doUnlock）。
 */
function unlockBlockReason(d) {
  if (!d.online) return '设备离线，无法远程解锁';
  return '';
}

/** 需要二次确认的原因（空串 = 直接下发，不用确认） */
function unlockWarnReason(d) {
  const s = d.windows_state || 'unknown';
  if (s === 'unlocked') return '系统显示这台电脑未锁屏（可能是状态还没刷新，或电脑确实没锁）';
  if (s === 'unknown') return '还没拿到这台电脑的 Windows 会话状态';
  return '';                                     // locked / logon_screen：正是该解锁的时候
}

/* 诊断用句柄（和 window.FM_PC 同一用途）：测试直接调这两个纯函数，
   免得为了验"按钮该不该灰"去造一整套设备数据。reason 表也放出来，
   让测试能断言"每个机器码都有中文说法"（新增码忘了加映射 = 用户看到英文码）。 */
window.FM_UNLOCK_POLICY = {
  blockReason: unlockBlockReason,
  warnReason: unlockWarnReason,
  reason: UNLOCK_REASON,
};

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

/** 卡片上的说明行：进行中的文案优先，其次是「为什么要你确认一次」 */
function unlockNoteFor(d) {
  const st = unlockState.get(d.device_id);
  if (st && st.note) return { kind: st.kind, text: st.note };
  if (!d.online) return null;                    // 「离线」状态行已经说了，不重复
  const warn = unlockWarnReason(d);
  if (warn) return { kind: 'info', text: warn + '；仍可下发一次请求' };
  return null;                                   // 已锁屏 / 登录界面：会话徽标已经说清楚
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
      const warn = unlockWarnReason(d);
      const pending = unlockPending(d.device_id);
      const ub = mkBtn('远程解锁', 'lock-open', 'btn--secondary', () => doUnlock(d));
      ub.dataset.unlockBtn = d.device_id;
      // ★ 只有离线（或请求进行中）才禁用 —— 会话状态显示不准时也给用户一条验证的路，
      //   点下去先二次确认，而不是给一个永远灰着的按钮。
      ub.disabled = !!why || pending;
      ub.title = why
        ? why
        : `向「${d.name}」下发一次性解锁请求（PC 用本机 Windows 凭据解锁）`
          + (warn ? `\n注意：${warn}。仍可下发，会先请你确认。` : '');
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

  // 会话状态和"该不该解锁"不一致时，只说清事实让用户拍板 —— 状态可能滞后，
  // 而且用户可能就是想验证这条路（"电脑没锁"不该让按钮变成点不动的摆设）。
  const warn = unlockWarnReason(d);
  if (warn) {
    const go = await confirmDialog({
      title: '仍然下发解锁请求？',
      body: warn + '。仍然下发一次解锁请求吗？',
      okText: '下发请求',
      danger: true,
      iconName: 'i-lock-open',
    });
    if (!go) return;
  }

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
    // nickname_id = 这个浏览器当前选中的共享昵称（null = 灰临时「默认用户」）；
    // **颜色一律不传**：服务端自己抄昵称当前的颜色、灰临时写常量 'gray'（§5.5 / R2）。
    const r = await api('/api/messages', {
      method: 'POST',
      body: JSON.stringify({
        sender_name: currentSender(),
        content,
        nickname_id: currentSenderId(),
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

/** 滚到最新一条。
    列表容器自己可滚动（#home-recent），所以直接把它滚到底；
    首页没显示时不动（别的页面渲染不该把滚动位置拽走）。 */
function scrollLogToEnd() {
  const box = $('home-recent');
  if (!box) return;
  const page = box.closest('.page');
  if (page && page.hidden) return;
  box.scrollTop = box.scrollHeight;
}

/** 列表当前是不是贴在底部（留 24px 容差）。
    用户往上翻看历史时不能硬把他拽回底部 —— 新消息只在「本来就在底部」时才跟随。 */
function isLogAtBottom() {
  const box = $('home-recent');
  if (!box) return true;
  return box.scrollHeight - box.scrollTop - box.clientHeight < 24;
}

async function loadMessages(opts) {
  state.messages = await api(`/api/messages?limit=${state.limit}`);
  renderMessages(!(opts && opts.keepScroll));
}

function upsertMessage(msg) {
  const i = state.messages.findIndex((m) => m.id === msg.id);
  if (i >= 0) state.messages[i] = msg; else state.messages.push(msg);
  // 新消息只在用户没往上翻的时候才贴到底（否则保持他正在看的位置）
  renderMessages(isLogAtBottom());
}

/** 一条消息的 DOM —— 直接交给共用的群聊组件（static/chat.js）。
    网页端首页「家庭消息」、PC 端客户端窗口因此长得**完全一样**：
    别人发的靠左，自己发的靠右。
    靠右只认「昵称 == 我当前用的昵称」（群聊模型：空间里完全以昵称区分），
    不看 device_id —— 设备不是身份。 */
function buildMsgEl(m) {
  return FMChat.row(m, { myName: currentSender(), status: STATUS_SENT });
}

/** 首页即完整消息列表：整条群聊流都长在这里，容器自己可滚动上下看全部，
    所以没有二级「消息」页、也没有「查看全部」。
    顺序仍是聊天阅读顺序：旧 → 新，最新的在最下面。
    toEnd === false 时保持用户当前看的位置（重画会把 scrollTop 归零，
    所以按「距底部多少像素」还原）。 */
function renderMessages(toEnd) {
  const box = $('home-recent');
  const fromBottom = box.scrollHeight - box.scrollTop - box.clientHeight;
  box.textContent = '';
  const list = chatOrder(state.messages);
  // 空列表：整块列表容器一起藏起来（不是只藏空态）——
  // 否则 flex:1 的列表和 flex:1 的空态会平分剩余空间，
  // 「还没有消息」跑到半空、发送框也被顶上去（用户报的就是这个）。
  box.hidden = list.length === 0;
  $('home-recent-empty').hidden = list.length > 0;
  $('home-msg-count').textContent = list.length ? `共 ${list.length} 条` : '';
  list.forEach((m) => box.appendChild(buildMsgEl(m)));
  if (toEnd !== false) scrollLogToEnd();
  else box.scrollTop = Math.max(0, box.scrollHeight - box.clientHeight - fromBottom);
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
  } else if (d.type === 'nickname_created' || d.type === 'nickname_updated'
             || d.type === 'nickname_color_changed') {
    // 三个「一行变了」的增量事件：更新本地缓存 + 重画（**不需要刷新页面**，§5.6.2）
    upsertNickname(d.nickname);
  } else if (d.type === 'nickname_removed') {
    dropNickname(d.nickname);          // 软删：摘缓存；正在用它的人回退灰临时（§11 风险 4）
  } else if (d.type === 'color_table_changed') {
    // ★ v0.19：色表被增删 → 重拉一次并按新表重画（老客户端忽略这一帧即可，帧里只有版本号）
    loadColorTable();
  } else if (d.type === 'nickname_list_sync') {
    // 整表下发（新客户端连上 / 客户端主动请求时服务端推）：直接替换本地缓存，对齐一致性
    if (typeof d.pool_version === 'number') nickPoolVersion = d.pool_version;
    applyNicknames(d.nicknames);
  }
}

/* ── B11. 昵称（Phase 3：共享昵称来自 NAS；开关关闭时回到本机老路径）
   ─────────────────────────────────────────────────────────────────
   三层状态（docs/NICKNAME-SYSTEM-SUMMARY.md §2）：
     ① 共享昵称池  = NAS 的 nicknames 表（唯一权威）—— 本文件只做**本地缓存 + 转发写操作**
     ② 当前用哪个  = **这个浏览器**本地（localStorage 的 fm.lastSender 存 nickname_id）
     ③ 消息快照    = 消息行内的 sender_name / sender_nickname_id / sender_color

   四条硬规矩（施工时不许破）：
     · **颜色只来自 NAS**：本地只认**逻辑色 ID**（color_01…color_16 / gray），
       显示色交给 static/nickcolor.js 的 §4.4 映射表按主题算（这里不写任何 HEX 表）；
     · 四个管理操作（新建 / 改名 / 重新分配颜色 / 删除）**全是全局操作、必须在线**：
       离线一律失败并在 UI 说明原因，**不排队、不本地生效、不补发**（§4 / §10 已定 3）；
     · **空库不假造任何昵称**（§0.9）：列表空就是空态 + 引导，服务端也不会懒建；
     · 本地只存「我选了谁」这一件事（§5.2）：**没有服务端指针**，
       A 浏览器选了别人，不会改到 B 的界面。
   ───────────────────────────────────────────────────────────────── */
const LAST_SENDER_KEY = 'fm.lastSender';       // 值 = nickname_id（数字字符串）；空 = 没选
const LOCAL_TEMP_NAME = '默认用户';             // 网页端的本地临时昵称（固定中性文案）
const LOCAL_TEMP_COLOR = 'gray';               // 它的逻辑色 ID（= 服务端常量 LOCAL_TEMP_COLOR_ID）
const NAME_MAX_LEN = 32;                       // 昵称长度上限：NAS / API / PC / Web 统一 32（§5.5）
const NICK_MAX_ACTIVE = 16;                    // 活跃上限 = 逻辑色池大小（14 起提示、16 拉闸，§4.3）
/* 旧 key（本地昵称数组）：Phase 3 起**停止读写**，只在启动时删掉残留。
   ⚠ 用拼接写法的原因：施工判据要求 `grep -rn 'fm.names' web/` 为 0（不再有第二真相源）。 */
const LEGACY_NAMES_KEY = 'fm.' + 'names';

/* 开关关闭时用的本机名字（= 改造前的行为）：**只活在内存里**，不再落 localStorage ——
   昵称的真相源只有 NAS 一处，浏览器本地不留第二份清单。 */
let localNames = ['我'];

let nickEnabled = false;       // /api/config 的 nickname_enabled（字段缺失 = false = 全走老路径）
let nickPoolVersion = 0;       // NAS 的池子版本，用于判断本地映射表是否落后（§4.3）
let nickList = [];             // NAS 上 status=active 的整表（按 nickname_id 升序）
let nickLoaded = false;        // 是否已经成功拉到过整表
let names = [];                // 显示名数组（从 nickList 派生；壳模式 / 下拉复用这一份）
const nickById = new Map();    // nickname_id → 昵称对象（主索引：消息快照按 id 查）
const nickByName = new Map();  // display_name → 昵称对象（反查：只有 sender_name 的历史消息）

/* ── 颜色：本地不生成，只把逻辑色 ID 交给 §4.4 映射表 ───────────── */
/** 逻辑色 ID → 显示色（kind ∈ dot / avatarBg / avatarFg）。
    未知 ID 由 nickcolor.js 内部走**灰兜底**，绝不把收到的字符串塞进 style（R2）。 */
function nickDisplay(kind, colorId) {
  if (!window.FMNickColor) return '#8A8A8A';    // nickcolor.js 在 app.js 之前加载，正常到不了
  return FMNickColor.display(kind, colorId);
}

/* ── ② 当前昵称（本地状态；服务端不参与）────────────────────────── */
/** 我选的 nickname_id；null = 没选 → 灰临时「默认用户」 */
function selectedNickId() {
  let raw = '';
  try { raw = localStorage.getItem(LAST_SENDER_KEY) || ''; } catch (_) { return null; }
  if (!/^\d+$/.test(raw)) return null;          // 空 / 老格式（名字）= 没选
  const id = Number(raw);
  return nickById.has(id) ? id : null;          // 服务端已经没有这条 = 没选
}
function rememberSenderId(id) {
  try { localStorage.setItem(LAST_SENDER_KEY, id == null ? '' : String(id)); } catch (_) {}
}
function currentNick() {
  const id = selectedNickId();
  return id == null ? null : (nickById.get(id) || null);
}
/** 当前发送人：选了共享昵称就是它，没选就是灰临时「默认用户」 */
function currentSender() {
  if (!nickEnabled) return localSender();
  const n = currentNick();
  return n ? n.display_name : LOCAL_TEMP_NAME;
}
/** 当前发送人的 nickname_id（发消息时带上；null = 灰临时） */
function currentSenderId() {
  if (!nickEnabled) return null;
  const n = currentNick();
  return n ? n.nickname_id : null;
}
/** 当前发送人的逻辑色 ID（灰临时 = 'gray'） */
function currentSenderColor() {
  const n = currentNick();
  return n ? n.color : LOCAL_TEMP_COLOR;
}

/* 开关关闭时的老路径（本机名字 + 哈希色，与改造前一致） */
function localSender() {
  const v = $('sender-sel')._value;
  if (v && localNames.includes(v)) return v;
  let last = '';
  try { last = localStorage.getItem(LAST_SENDER_KEY) || ''; } catch (_) {}
  return localNames.includes(last) ? last : localNames[0];
}
function rememberSender(value) {
  try { localStorage.setItem(LAST_SENDER_KEY, value || ''); } catch (_) {}
}

/* ── 一次性迁移 / 清理 ─────────────────────────────────────────── */
/** 旧 key 只删不读（读它 = 又有了第二个昵称真相源） */
function dropLegacyNamesKey() {
  try { localStorage.removeItem(LEGACY_NAMES_KEY); } catch (_) {}
}
/** `fm.lastSender` 迁移：老值存的是**名字**，能对上某个 active 昵称就换成它的 nickname_id，
    对不上就回到「未选」（= 灰色「默认用户」）。只在第一次拉到整表后跑一次。 */
let nickMigrated = false;
function migrateLastSender() {
  if (nickMigrated) return;
  nickMigrated = true;
  let raw = '';
  try { raw = localStorage.getItem(LAST_SENDER_KEY) || ''; } catch (_) { return; }
  if (!raw || /^\d+$/.test(raw)) return;         // 空的 / 已经是 id → 不动
  const hit = nickByName.get(raw.trim());
  rememberSenderId(hit ? hit.nickname_id : null);
  if (hit) snack(`已把「当前昵称」记为「${hit.display_name}」（改用 nickname_id 记住它）`);
}

/* ── ① 共享昵称池的本地缓存（读写全在 NAS，这边只是镜像）───────── */
function rebuildNickIndex() {
  nickById.clear();
  nickByName.clear();
  nickList.forEach((n) => {
    nickById.set(n.nickname_id, n);
    if (n.display_name) nickByName.set(n.display_name, n);
  });
  names = nickList.map((n) => n.display_name);
}

/** 从 NAS 拉整表（空库就是空数组 —— 服务端不建任何行，§0.9） */
async function loadNicknames() {
  try {
    const r = await api('/api/nicknames?status=active');
    applyNicknames(r && r.nicknames);
  } catch (e) {
    nickLoaded = true;                            // 拿不到就按空处理：界面**不假造**任何昵称
    nickList = [];
    rebuildNickIndex();
    snack('读取昵称失败：' + nickErrText(e), { error: true });
  }
}

/** ★ v0.19：拉服务端**颜色表**并让本地以它为准（内置表只做首屏兜底）。

    谁调：① bootConsole（启动就拉一次）；② 收到 `color_table_changed` 广播后重拉；
          ③ 增删颜色 / 指定颜色后（幂等，广播也会来一次）。
    拉的是 `status=all`（界面要显示已停用的那些），但**只把可用的**交给渲染表
    （停用的色不再参与显示色计算，选中它服务端会 409）。
    拿到就重画（昵称行色点 / 设置页 / 消息里的头像底色都按新表重算，因为显示色是现算的）。
    拉不到 → 保留手上那份（内置表或上次的表），**绝不清空**，只提示一次。 */
async function loadColorTable() {
  if (!window.FMNickColor) return false;
  try {
    const r = await api('/api/nicknames/colors?status=all');
    const rows = (r && Array.isArray(r.colors)) ? r.colors : [];
    const act = rows.filter((c) => c && c.status === 'active');
    const okSet = FMNickColor.setTable(act, r && r.color_pool_version);
    if (typeof (r && r.color_pool_version) === 'number') nickPoolVersion = r.color_pool_version;
    if (rows.length) nickColorRows = rows;        // ★ v0.19：色表 UI 用（含已停用）
    if (typeof r.max_color_pool === 'number') nickPoolMax = r.max_color_pool;
    renderColorTable();
    if (okSet) {
      redrawNicks();
      renderMessages(isLogAtBottom());      // 老消息的快照色也要按新表重算
    }
    return !!okSet;
  } catch (e) {
    snack('读取颜色表失败：' + nickErrText(e), { error: true });
    return false;
  }
}

/** 整表替换（GET 结果 / nickname_list_sync）→ 对齐缓存 + 重画 */
function applyNicknames(list) {
  nickList = (Array.isArray(list) ? list : [])
    .filter((n) => n && n.nickname_id != null)
    .sort((a, b) => a.nickname_id - b.nickname_id);
  rebuildNickIndex();
  nickLoaded = true;
  migrateLastSender();
  redrawNicks();
}

/** 增量事件（nickname_created / nickname_updated / nickname_color_changed）：只动一行 */
function upsertNickname(one) {
  if (!one || one.nickname_id == null) return;
  const id = Number(one.nickname_id);
  const i = nickList.findIndex((n) => n.nickname_id === id);
  if (i >= 0) nickList[i] = Object.assign({}, nickList[i], one, { nickname_id: id });
  else nickList.push(Object.assign({}, one, { nickname_id: id }));
  nickList.sort((a, b) => a.nickname_id - b.nickname_id);
  nickLoaded = true;
  rebuildNickIndex();
  redrawNicks();
}

/** 增量事件（nickname_removed）：摘掉缓存；**正在用它的人立刻回退灰临时并提示**（§11 风险 4） */
function dropNickname(one) {
  const id = one && one.nickname_id != null ? Number(one.nickname_id) : null;
  if (id == null) return;
  const wasMine = selectedNickId() === id;
  const gone = (one && one.display_name) || (nickById.get(id) || {}).display_name || '这个昵称';
  nickList = nickList.filter((n) => n.nickname_id !== id);
  rebuildNickIndex();
  if (wasMine) {
    rememberSenderId(null);
    snack(`「${gone}」已被删除，已回到「${LOCAL_TEMP_NAME}」`, { action: '知道了' });
  }
  redrawNicks();
}

/** 重画的**单一入口**（昵称下拉 + 设置页列表 + 消息行）——避免「一处新色、一处旧色」 */
function redrawNicks() {
  renderNameSelectors();
  renderNickList();
  if (nickEnabled) renderMessages(isLogAtBottom());
}

/* ── 消息上色：三级查找（§2-C1）─────────────────────────────────── */
/** 一条消息的逻辑色 ID：
      ① 消息快照的 sender_nickname_id（新消息，最准）
      ② 消息快照的 sender_color（逻辑色 ID；灰临时 = 'gray'）
         ⚠ NULL（改造前的老消息）**不算灰**，继续往下找
      ③ 名字反查昵称表（历史消息只有 sender_name —— 这张反查索引就是给它兜底的）
    都没命中 → null（调用方退回哈希色，老消息观感与今天一致；§4.4 的「未知 → 兜底」） */
function nickColorIdForMessage(msg) {
  if (!msg) return null;
  if (msg.sender_nickname_id != null) {
    const n = nickById.get(Number(msg.sender_nickname_id));
    if (n) return n.color;
  }
  const snap = msg.sender_color;
  if (typeof snap === 'string' && window.FMNickColor && FMNickColor.isKnown(snap)) return snap;
  const name = String(msg.sender_name || '').trim();
  if (name && nickByName.has(name)) return nickByName.get(name).color;
  if (name && name === LOCAL_TEMP_NAME) return LOCAL_TEMP_COLOR;   // 本地临时昵称一律灰
  return null;
}

/** 给 chat.js / shell.js 用的钩子（它们不直接碰昵称表） */
window.FMNickResolver = {
  /** 头像底 + 头像字色；null = 让调用方走哈希兜底（开关关闭 / 老消息 / 未知名字） */
  avatarFor(msg) {
    if (!nickEnabled) return null;
    const id = nickColorIdForMessage(msg);
    if (!id) return null;
    return { bg: nickDisplay('avatarBg', id), fg: nickDisplay('avatarFg', id) };
  },
  /** 逻辑色 ID（给需要自己决定 kind 的地方，例如壳里的小圆点） */
  colorIdForMessage(msg) { return nickEnabled ? nickColorIdForMessage(msg) : null; },
  /** 按显示名查逻辑色 ID（壳模式的下拉 / 老消息兜底） */
  colorIdForName(name) {
    if (!nickEnabled) return null;
    const s = String(name == null ? '' : name).trim();
    const hit = nickByName.get(s);
    if (hit) return hit.color;
    return s === LOCAL_TEMP_NAME ? LOCAL_TEMP_COLOR : null;
  },
  dot(colorId) { return nickDisplay('dot', colorId); },
  /** 壳模式（PC 端 WebView2 页面）用的几个小接口 —— 它不直接碰昵称表，
      但「以谁的名义回复」的下拉与弹窗小圆点需要同一份颜色/同一条本地选择（§5.6.2 第 2 条）。 */
  enabled() { return nickEnabled; },
  /** 按显示名选择（壳里的下拉给的是名字）—— 存进本地的仍然是 nickname_id */
  selectByName(name) {
    const s = String(name == null ? '' : name).trim();
    const n = nickByName.get(s);
    rememberSenderId(n ? n.nickname_id : null);
    redrawNicks();
    return !!n;
  },
  /** 本地存的那个选择，换回显示名（没选 = 灰临时） */
  currentName() { return nickEnabled ? currentSender() : null; },
  /** 当前选中（调试 / 测试断言用） */
  current() {
    return { nickname_id: currentSenderId(), display_name: currentSender(),
             color: currentSenderColor(), enabled: nickEnabled };
  },
};

/* ── 发送区下拉（群聊模型下只有这一个）───────────────────────────── */
function renderNameSelectors() {
  if (!nickEnabled) {
    const items = localNames.map((n) => ({ value: n, label: n, color: nickColor(n) }));
    buildSelect($('sender-sel'), items, localSender(), (v) => { rememberSender(v); redrawNicks(); });
  } else {
    const cur = currentSenderId();
    /* 没选 → 第一项就是灰色的本地临时昵称（它**不是**共享昵称：不进池、不占色、不参与同步，
       只在这台浏览器上存在，§3.2.1）；条目的颜色是「色块 + 常规字色」，不用色值当字色（§4.4 规则 4） */
    const items = [{ value: '', label: LOCAL_TEMP_NAME, color: nickDisplay('dot', LOCAL_TEMP_COLOR), swatch: true }]
      .concat(nickList.map((n) => ({
        value: String(n.nickname_id), label: n.display_name, color: nickDisplay('dot', n.color), swatch: true,
      })));
    buildSelect($('sender-sel'), items, cur == null ? '' : String(cur),
                (v) => selectNick(v === '' ? null : Number(v)));
  }
}

/** 选择昵称 = **纯本地动作**：不上传、不广播、不影响别的浏览器（§5.2） */
function selectNick(id) {
  rememberSenderId(id);
  const n = id == null ? null : nickById.get(id);
  snack(n ? `以后以「${n.display_name}」的名义发言` : `已回到「${LOCAL_TEMP_NAME}」（灰临时昵称）`);
  redrawNicks();
}

/* ── 设置页：昵称列表（全局管理：改名 / 重新分配颜色 / 删除）──────── */
function noteEl(text, cls) {
  const p = document.createElement('p');
  p.className = 'nick-note' + (cls ? ' ' + cls : '');
  p.textContent = text;
  return p;
}

function renderNickList() {
  const box = $('names-list');
  if (!box) return;
  box.innerHTML = '';
  if (!nickEnabled) { renderLocalNickList(box); renderNickQuota(); return; }

  if (!nickLoaded) { box.appendChild(noteEl('正在读取 NAS 上的昵称……')); renderNickQuota(); return; }
  if (nickList.length === 0) {
    // ★ 空态是**正常路径**（全新部署）：不假造任何默认昵称，只给引导（§0.9）
    box.appendChild(noteEl('还没有昵称，先添加一个吧。'));
  } else {
    nickList.forEach((n) => box.appendChild(nickRowEl(n)));
  }
  renderNickQuota();
}

/** 昵称行里的文字按钮 —— 与 PC 端 `mkBtn()` **逐字对齐**（选用 / 换色 / 删除）。
 *  v0.18.0 实测反馈：网页端原来只有「换色 / 删除」两个图标按钮，PC 端是三个文字按钮
 *  —— 两端要长得一样、用得一样。id 也与 PC 同构（`nick-use-<id>` 等），
 *  两端测试就能照同一套选择器写。 */
function nickBtn(label, id, title) {
  const b = document.createElement('button');
  b.type = 'button';
  if (id) b.id = id;
  b.className = 'btn btn--secondary btn--sm';
  b.textContent = label;
  if (title) b.title = title;
  return b;
}

/** 一行的 DOM：色点（NAS 的逻辑色 ID）+ 名字（可就地改名）+ 选用 / 换色 / 删除 */
function nickRowEl(n) {
  const row = document.createElement('div');
  row.className = 'name-row' + (selectedNickId() === n.nickname_id ? ' name-row--using' : '');
  row.dataset.nickId = String(n.nickname_id);

  const sw = document.createElement('button');
  sw.type = 'button';
  sw.className = 'name-row__swatch';
  sw.style.background = nickDisplay('dot', n.color);   // 显示色 = §4.4 按主题算出来的圆点变体
  sw.dataset.colorId = n.color;                        // 逻辑色 ID 只进 data 属性，不参与排版
  // ★ v0.19：点色点 = 给这个昵称**指定**颜色（弹窗里也能随机换）—— 不新增按钮，窄屏也放得下
  sw.title = `颜色：${n.color}（点它给「${n.display_name}」选颜色）`;
  sw.setAttribute('aria-label', `给「${n.display_name}」选颜色`);
  sw.onclick = () => openColorPicker(n);

  const input = document.createElement('input');
  input.className = 'name-row__input';
  input.value = n.display_name;
  input.maxLength = NAME_MAX_LEN;
  input.setAttribute('aria-label', `昵称 ${n.display_name}`);
  input.onchange = () => renameNick(n, input);

  const actions = document.createElement('div');
  actions.className = 'name-row__actions';

  /* 「选用」= 选择昵称，**纯本地动作 —— 唯一能离线的**（§5.2，与 PC 端同口径）；
     正在用的那条置灰（不用重复点）。 */
  const use = nickBtn('选用', `nick-use-${n.nickname_id}`,
    `以「${n.display_name}」的名义发言（只影响本机）`);
  use.disabled = selectedNickId() === n.nickname_id;
  use.onclick = () => selectNick(Number(n.nickname_id));

  const recolor = nickBtn('换色', `nick-color-${n.nickname_id}`,
    '重新分配颜色（全局：所有在用的机器一起变）');
  recolor.onclick = () => reassignNickColor(n);

  const del = nickBtn('删除', `nick-del-${n.nickname_id}`,
    '删除这个共享昵称（全局；历史消息原样不变）');
  del.onclick = () => deleteNick(n);

  actions.append(use, recolor, del);
  row.append(sw, input, actions);
  return row;
}

/** 额度提示（文案逐字取自 §4.3，括号里是**真实计数**）：
      x ≥ 14 →「共享昵称颜色即将用尽（x/16）」；x = 16 → 满额文案 + 新建入口置灰 */
function renderNickQuota() {
  const el = $('nick-quota');
  const addBtn = $('name-add');
  const input = $('name-new');
  if (!el) return;
  if (!nickEnabled) {
    el.hidden = true; el.textContent = ''; el.className = 'nick-note';
    if (addBtn) addBtn.disabled = false;
    if (input) input.disabled = false;
    return;
  }
  const x = nickList.length;
  const full = x >= NICK_MAX_ACTIVE;
  if (full) {
    el.hidden = false;
    el.className = 'nick-note nick-note--error';
    el.textContent = '已达到共享昵称上限，请删除不再使用的昵称后再添加。';
  } else if (x >= NICK_MAX_ACTIVE - 2) {
    el.hidden = false;
    el.className = 'nick-note nick-note--warn';
    el.textContent = `共享昵称颜色即将用尽（${x}/${NICK_MAX_ACTIVE}）`;
  } else {
    el.hidden = true; el.textContent = ''; el.className = 'nick-note';
  }
  if (addBtn) addBtn.disabled = full;             // 满额置灰（服务端仍会兜底 503）
  if (input) input.disabled = full;
}

/* ── 撞名引导（409 NICKNAME_ALREADY_EXISTS →「已存在，直接选用它？」）── */
function hideNickConflict() {
  const box = $('nick-conflict');
  if (!box) return;
  box.hidden = true;
  box.innerHTML = '';
}

function showNickConflict(id, name) {
  const box = $('nick-conflict');
  if (!box) return;
  box.innerHTML = '';
  box.hidden = false;

  const t = document.createElement('span');
  t.className = 'nick-conflict__text';
  t.textContent = `「${name}」已存在，直接选用它？`;

  const acts = document.createElement('div');
  acts.className = 'nick-conflict__actions';

  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = 'btn';
  cancel.textContent = '取消';
  cancel.onclick = hideNickConflict;

  const use = document.createElement('button');
  use.type = 'button';
  use.className = 'btn btn--primary';
  use.textContent = '选用它';
  use.disabled = id == null;                      // 服务端没给 id（异常情况）→ 只能取消
  use.onclick = () => {
    $('name-new').value = '';
    hideNickConflict();
    if (id != null) selectNick(Number(id));       // 点一下 = 选用（纯本地动作）
  };

  acts.append(cancel, use);
  box.append(t, acts);
}

/* ── 四个管理操作（**全是全局操作，必须在线**）────────────────────── */
/** 管理操作的错误文案：离线要**明说原因**，否则用户以为功能坏了（§4 / §11 风险 6） */
function nickErrText(e) {
  if (e && e.code === 'OFFLINE') return '连不上服务器（昵称管理必须在线）';
  return (e && e.message) || '未知错误';
}

async function addName() {
  if (!nickEnabled) return addLocalName();
  const input = $('name-new');
  const v = (input.value || '').trim();
  if (!v) return;
  if (v.length > NAME_MAX_LEN) { snack(`昵称最长 ${NAME_MAX_LEN} 个字符`, { error: true }); return; }
  if (nickByName.has(v)) {                        // 本地已知同名 → 不白跑一次请求
    showNickConflict(nickByName.get(v).nickname_id, v);
    return;
  }
  try {
    const r = await api('/api/nicknames', {
      method: 'POST', body: JSON.stringify({ display_name: v }),
    });
    input.value = '';
    hideNickConflict();
    if (r && r.nickname) {
      upsertNickname(r.nickname);
      selectNick(r.nickname.nickname_id);         // 新建完就直接用它
    }
  } catch (e) {
    if (e.code === 'NICKNAME_ALREADY_EXISTS') {
      const id = e.existing_nickname_id != null
        ? e.existing_nickname_id : (nickByName.get(v) || {}).nickname_id;
      showNickConflict(id, v);
    } else if (e.code === 'NO_AVAILABLE_COLOR') {
      snack(e.message, { error: true, action: '知道了' });
      loadNicknames();                            // 计数可能被别的浏览器改了 → 重新对齐
    } else {
      snack('新建失败：' + nickErrText(e), { error: true });
    }
  }
}

async function renameNick(n, input) {
  const v = (input.value || '').trim();
  if (!v || v === n.display_name) { input.value = n.display_name; return; }
  if (v.length > NAME_MAX_LEN) {
    input.value = n.display_name;
    snack(`昵称最长 ${NAME_MAX_LEN} 个字符`, { error: true });
    return;
  }
  try {
    const r = await api(`/api/nicknames/${n.nickname_id}`, {
      method: 'PATCH', body: JSON.stringify({ display_name: v }),
    });
    if (r && r.nickname) upsertNickname(r.nickname);
  } catch (e) {
    input.value = n.display_name;                 // 失败就回滚显示，**不本地生效**
    if (e.code === 'NAME_TAKEN') snack(`「${v}」已被占用，换个名字吧`, { error: true });
    else if (e.code === 'NICKNAME_INACTIVE') snack('这个昵称已经被删除了', { error: true });
    else snack('改名失败：' + nickErrText(e), { error: true });
  }
}

async function reassignNickColor(n) {
  try {
    const r = await api(`/api/nicknames/${n.nickname_id}/reassign-color`, { method: 'POST' });
    if (r && r.nickname) upsertNickname(r.nickname);
    snack(`「${n.display_name}」换了新颜色（所有在用的机器一起变）`);
  } catch (e) {
    snack('换色失败：' + nickErrText(e), { error: true });
  }
}

async function deleteNick(n) {
  const ok = await confirmDialog({
    title: '删除这个昵称？',
    body: `「${n.display_name}」会从所有设备上消失，颜色立刻回到池子；它过去发的消息保留当时的名字和颜色。`,
    okText: '删除',
    danger: true,
  });
  if (!ok) return;
  try {
    await api(`/api/nicknames/${n.nickname_id}`, { method: 'DELETE' });
    // 广播会带来 nickname_removed；WS 断线时本地也顺手摘掉，列表不会残留
    dropNickname({ nickname_id: n.nickname_id, display_name: n.display_name });
  } catch (e) {
    snack('删除失败：' + nickErrText(e), { error: true });
  }
}

/* ── 颜色表 UI（v0.19：色表是数据，网页端可增删；docs/COLOR-TABLE-PLAN.md §3.4）
   · 色块区：每格写「谁在用」，正被用的不能停用；底部一行加颜色（HEX / RGB 都收）。
   · 选色：点昵称行的**色点** → 弹窗列出可用色（别人占着的置灰）→ 点一个就指定；
           弹窗里保留「随机换一个」（= 原来的换色）。 ────────────────────── */

/** 从服务端拉到的整份色表（含 retired；服务端 rows() 的字段原样） */
let nickColorRows = [];     // 色表 UI 用（含已停用；可用色另由 FMNickColor 那份管）
let nickPoolMax = 0;        // 颜色池上限（服务端说了算，v0.19.1 起别写死）

/** 画色块区：状态、谁在用、能否停用都在这一处决定（不在别处重复判断）。 */
function renderColorTable() {
  const grid = $('color-grid');
  const count = $('color-count');
  const block = $('color-block');
  if (!grid || !count || !block) return;
  block.hidden = !nickEnabled;                    // 开关关闭时整块不出现（老路径照旧）
  if (!nickEnabled) return;

  const act = nickColorRows.filter((c) => c.status === 'active');
  const used = act.filter((c) => c.in_use).length;
  count.textContent = `可用 ${act.length}${nickPoolMax ? ` / 上限 ${nickPoolMax}` : ''} · 在用 ${used}/${NICK_MAX_ACTIVE}`;

  grid.textContent = '';
  nickColorRows.forEach((c) => {
    const chip = document.createElement('div');
    chip.className = 'color-chip'
      + (c.status === 'retired' ? ' color-chip--retired' : '')
      + (c.in_use ? ' color-chip--used' : '');
    chip.dataset.colorId = c.color_id;

    const dot = document.createElement('span');
    dot.className = 'color-chip__dot';
    dot.style.background = nickDisplay('dot', c.color_id);   // 显示色按当前主题现算

    const id = document.createElement('span');
    id.className = 'color-chip__id';
    id.textContent = c.color_id.replace('color_', '');
    id.title = `${c.color_id} · ${c.hex}`;

    chip.append(dot, id);

    if (c.status === 'active') {
      const who = (c.used_by || []).map((u) => u.display_name).join('、');
      const del = document.createElement('button');
      del.type = 'button';
      del.className = 'color-chip__del';
      del.textContent = '×';
      del.disabled = !!c.in_use;
      del.title = c.in_use
        ? `${who} 正在用它，先给它换个颜色再停用`
        : `停用 ${c.color_id}（行保留，已发的消息颜色不变）`;
      del.setAttribute('aria-label', del.title);
      del.onclick = () => retireNickColor(c);
      chip.appendChild(del);
    }
    grid.appendChild(chip);
  });
}

/** 就地提示（色表区底部）：错误留在输入框下面，**不静默** */
function colorHint(text, error = true) {
  const el = $('color-hint');
  if (!el) return;
  el.hidden = !text;
  el.className = 'nick-note' + (error ? ' nick-note--error' : '');
  el.textContent = text || '';
}

async function addNickColor() {
  const input = $('color-new');
  if (!input) return;
  const v = (input.value || '').trim();
  if (!v) return;
  colorHint('');
  try {
    const r = await api('/api/nicknames/colors', {
      method: 'POST', body: JSON.stringify({ hex: v }),
    });
    input.value = '';
    await loadColorTable();                       // 广播也会来一次，这里先对齐（幂等）
    snack(`已加入颜色 ${(r && r.color && r.color.hex) || v}`);
  } catch (e) {
    if (e.code === 'COLOR_ALREADY_EXISTS') {
      colorHint(`这个颜色已经在表里了（${e.existing_color_id || '见色块区'}）`);
    } else if (e.code === 'INVALID_COLOR_HEX') {
      colorHint('颜色格式不对：支持 #0FA3B1、0FA3B1、#0F3、rgb(15,163,177)');
    } else if (e.code === 'COLOR_POOL_FULL') {
      colorHint(e.message || '颜色表已满');
    } else {
      colorHint('加颜色失败：' + nickErrText(e));
    }
  }
}

async function retireNickColor(row) {
  if (row.in_use) {
    const who = (row.used_by || []).map((u) => u.display_name).join('、');
    snack(`${row.color_id} 正被「${who}」用着，先给它换个颜色`, { error: true });
    return;
  }
  const ok = await confirmDialog({
    title: `停用 ${row.color_id}？`,
    body: `这个颜色（${row.hex}）会从可选列表里去掉：新昵称不会再分到它，`
        + `但**已经发过的消息保持当时的颜色**（颜色 ID 不会被回收给别的颜色用）。`,
    okText: '停用',
    danger: true,
  });
  if (!ok) return;
  try {
    await api(`/api/nicknames/colors/${row.color_id}`, { method: 'DELETE' });
    await loadColorTable();
    snack(`已停用 ${row.color_id}`);
  } catch (e) {
    if (e.code === 'COLOR_IN_USE') snack('这个颜色正被别的昵称用着', { error: true });
    else snack('停用失败：' + nickErrText(e), { error: true });
    loadColorTable();                             // 表可能被别的端改了 → 重新对齐
  }
}

/** 当前正在给它选颜色的那个昵称（弹窗用） */
let pickerNick = null;

function openColorPicker(n) {
  pickerNick = n;
  const dlg = $('dlg-color');
  if (!dlg) return;
  $('color-dlg-sub').textContent = `给「${n.display_name}」指定颜色（全局：所有在用的机器一起变）`;
  colorPickerHint('');
  const grid = $('color-picker-grid');
  grid.textContent = '';
  nickColorRows.filter((c) => c.status === 'active').forEach((c) => {
    const others = (c.used_by || []).filter((u) => u.nickname_id !== n.nickname_id)
      .map((u) => u.display_name);
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'color-pick'
      + (c.color_id === n.color ? ' color-pick--current' : '')
      + (others.length ? ' color-pick--taken' : '');
    b.dataset.colorId = c.color_id;
    const dot = document.createElement('span');
    dot.className = 'color-chip__dot';
    dot.style.background = nickDisplay('dot', c.color_id);
    const label = document.createElement('span');
    label.textContent = c.color_id.replace('color_', '')
      + (others.length ? `（${others.join('、')}）` : '');
    b.append(dot, label);
    b.disabled = others.length > 0;               // 别人占着的选不了（服务端也会 409）
    b.onclick = () => applyColorPick(c.color_id, b);
    grid.appendChild(b);
  });
  openDialog(dlg, grid.querySelector('.color-pick--current') || grid.querySelector('.color-pick'));
}

function colorPickerHint(text) {
  const el = $('color-dlg-hint');
  if (!el) return;
  el.hidden = !text;
  el.className = 'nick-note nick-note--error';
  el.textContent = text || '';
}

async function applyColorPick(colorId, btn) {
  if (!pickerNick) return;
  const n = pickerNick;
  if (btn) btn.disabled = true;
  try {
    const r = await api(`/api/nicknames/${n.nickname_id}/color`, {
      method: 'POST', body: JSON.stringify({ color_id: colorId }),
    });
    if (r && r.nickname) upsertNickname(r.nickname);
    closeDialog($('dlg-color'));
    snack(`「${n.display_name}」的颜色已改成 ${colorId}`);
  } catch (e) {
    if (btn) btn.disabled = false;
    if (e.code === 'COLOR_IN_USE') colorPickerHint('这个颜色正被别的昵称用着，换一个吧');
    else if (e.code === 'COLOR_RETIRED') colorPickerHint('这个颜色已停用');
    else if (e.code === 'COLOR_NOT_FOUND') colorPickerHint('这个颜色已经不在表里了');
    else colorPickerHint('指定失败：' + nickErrText(e));
    loadColorTable();                             // 表可能被别的端改了 → 重新对齐
  }
}

/* ── 开关关闭时的老路径（nickname.enabled=false，行为回到改造前）──── */
function renderLocalNickList(box) {
  const wrap = box || $('names-list');
  wrap.innerHTML = '';
  if (localNames.length === 0) { wrap.appendChild(noteEl('还没有昵称，先添加一个吧。')); return; }

  localNames.forEach((name, idx) => {
    const row = document.createElement('div');
    row.className = 'name-row';

    const sw = document.createElement('span');
    sw.className = 'name-row__swatch';
    sw.style.background = nickColor(name);

    const input = document.createElement('input');
    input.className = 'name-row__input';
    input.value = name;
    input.maxLength = NAME_MAX_LEN;               // 老路径也统一 32（§5.5：四端同一上限）
    input.setAttribute('aria-label', `昵称 ${name}`);
    input.onchange = () => {
      const v = input.value.trim();
      if (!v || (localNames.includes(v) && localNames[idx] !== v)) {
        input.value = localNames[idx];
        if (v && localNames.includes(v)) snack('这个昵称已经有了', { error: true });
        return;
      }
      localNames[idx] = v;
      redrawNicks();
    };

    const del = document.createElement('button');
    del.type = 'button';
    del.className = 'icon-btn icon-btn--danger';
    del.setAttribute('aria-label', `删除昵称 ${name}`);
    del.appendChild(icon('delete', 'icon icon--sm'));
    del.onclick = () => {
      localNames.splice(idx, 1);
      if (localNames.length === 0) localNames = ['我'];
      redrawNicks();
    };

    row.append(sw, input, del);
    wrap.appendChild(row);
  });
}

function addLocalName() {
  const v = $('name-new').value.trim();
  if (!v) return;
  if (v.length > NAME_MAX_LEN) { snack(`昵称最长 ${NAME_MAX_LEN} 个字符`, { error: true }); return; }
  if (!localNames.includes(v)) localNames.push(v);
  else snack('这个昵称已经有了');
  $('name-new').value = '';
  rememberSender(v);
  redrawNicks();
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
  renderNickList();
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

/* 导航（左导航 + 底部导航共用同一套 data-page 按钮）。
   注意：绑定写在这两处导航的**查询结果**上，删掉「消息」项后这里自然只剩 4 项，
   不会留下指向已删元素的监听。 */
document.querySelectorAll('[data-page]').forEach((b) => {
  b.onclick = () => go(b.dataset.page);
});
$('nav-toggle').onclick = () => {
  if ($('app-shell').classList.contains('nav-open')) closeNavDrawer(); else openNavDrawer();
};
$('nav-scrim').onclick = closeNavDrawer;

$('shot-again').onclick = fetchShot;

/* 发送区是单行输入框，回车即发送（与 PC 端回复栏一致：pc.js 里也是裸 Enter）。
   中文输入法组字中的那次回车是「确认候选」，不能当成发送 —— isComposing /
   keyCode 229 的判断专门挡这一下。Ctrl/⌘+Enter 保留，快发习惯不用改。 */
$('content').addEventListener('keydown', (e) => {
  if (e.key !== 'Enter') return;
  if (e.isComposing || e.keyCode === 229) return;
  e.preventDefault();
  sendMessage();
});

$('btn-settings').onclick = () => go('settings');
$('name-add').onclick = addName;
// ★ v0.19：颜色表（加颜色）+ 选色弹窗（点昵称行的色点打开）
$('color-add-btn').onclick = addNickColor;
$('color-new').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); addNickColor(); }
});
$('color-dlg-close').onclick = () => closeDialog($('dlg-color'));
$('color-dlg-cancel').onclick = () => closeDialog($('dlg-color'));
$('color-dlg-random').onclick = () => {
  const n = pickerNick;
  closeDialog($('dlg-color'));
  if (n) reassignNickColor(n);                    // 「随机换一个」= 原来的换色
};
$('dlg-color').addEventListener('click', (e) => {   // 点遮罩空白处 = 关
  if (e.target === $('dlg-color')) closeDialog($('dlg-color'));
});
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
