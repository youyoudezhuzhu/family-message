/* 家庭消息控制台 —— 原生 JS，零构建
   Material Design 3 暗色主题。令牌与配色定义见 docs/DESIGN-TOKENS.md */

const STATE_ORDER = ['created', 'server_received', 'device_received', 'popup_displayed', 'read'];
const STATE_LABEL = {
  created: '已创建',
  server_received: '服务器已接收',
  device_received: 'PC 已收到',
  popup_displayed: '弹窗已显示',
  read: '已点击「关闭窗口」',
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

/* ══════════════════════════════════════════════
   配色方案（只存本地，服务端不参与）
   ══════════════════════════════════════════════ */
const THEMES = [
  { id: 'indigo', name: '靛蓝', c: '#A8C7FA' },
  { id: 'violet', name: '紫罗', c: '#D0BCFF' },
  { id: 'teal',   name: '青碧', c: '#80DEEA' },
  { id: 'green',  name: '松绿', c: '#A5D6A7' },
  { id: 'amber',  name: '琥珀', c: '#FFD54F' },
  { id: 'coral',  name: '珊瑚', c: '#FFB4AB' },
  { id: 'pink',   name: '品红', c: '#F8BBD0' },
  { id: 'cyan',   name: '天青', c: '#90CAF9' },
];
const THEME_KEY = 'fm.theme';

function loadTheme() {
  try {
    const v = localStorage.getItem(THEME_KEY);
    if (v && THEMES.some((t) => t.id === v)) return v;
  } catch (_) { /* 读不到就用默认 */ }
  return 'indigo';
}
function applyTheme(id) {
  document.documentElement.setAttribute('data-theme', id);
  try { localStorage.setItem(THEME_KEY, id); } catch (_) {}
}

/* ══════════════════════════════════════════════
   昵称色：同一昵称在任何一端都是同一个颜色
   算法必须与 PC 端 / Android 端完全一致（见 DESIGN-TOKENS.md §3）
   ══════════════════════════════════════════════ */
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

/* ── 口令 ─────────────────────────────────────── */
function askPassword() {
  const pwd = window.prompt('请输入家庭控制台访问口令');
  if (pwd === null) return;
  fetch(BASE + '/api/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'same-origin',
    body: JSON.stringify({ password: pwd }),
  }).then((r) => { if (r.ok) location.reload(); else alert('口令错误'); });
}

/* ══════════════════════════════════════════════
   MD 下拉组件（原生 select 的下拉浮层无法定制，只能自绘）
   ══════════════════════════════════════════════ */
function buildSelect(host, items, value, onChange) {
  host.innerHTML = '';
  const chosen = items.find((i) => i.value === value) || items[0];
  host._value = chosen ? chosen.value : '';

  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'md-select-btn';

  const label = document.createElement('span');
  label.textContent = chosen ? chosen.label : '（没有可选项）';
  if (chosen && chosen.color) label.style.color = chosen.color;

  const chev = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  chev.setAttribute('viewBox', '0 0 24 24');
  chev.setAttribute('width', '20');
  chev.setAttribute('height', '20');
  chev.setAttribute('class', 'chev');
  const cp = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  cp.setAttribute('d', 'M7 10l5 5 5-5z');
  cp.setAttribute('fill', 'currentColor');
  chev.appendChild(cp);

  btn.append(label, chev);

  const menu = document.createElement('div');
  menu.className = 'md-select-menu';
  items.forEach((it) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'md-select-item' + (it.value === host._value ? ' sel' : '');
    if (it.color) {
      const sw = document.createElement('span');
      sw.className = 'swatch';
      sw.style.background = it.color;
      el.appendChild(sw);
    }
    const t = document.createElement('span');
    t.textContent = it.label;
    el.appendChild(t);
    el.onclick = (ev) => {
      ev.stopPropagation();
      host.classList.remove('open');
      buildSelect(host, items, it.value, onChange);
      if (onChange) onChange(it.value);
    };
    menu.appendChild(el);
  });

  btn.onclick = (ev) => {
    ev.stopPropagation();
    document.querySelectorAll('.md-select.open').forEach((o) => {
      if (o !== host) o.classList.remove('open');
    });
    host.classList.toggle('open');
  };

  host.append(btn, menu);
}

document.addEventListener('click', () => {
  document.querySelectorAll('.md-select.open').forEach((o) => o.classList.remove('open'));
});

/* ── 初始化 ───────────────────────────────────── */
async function boot() {
  applyTheme(loadTheme());
  state.config = await api('/api/config');
  renderNameSelectors();

  $('quick').innerHTML = '';
  QUICK.forEach((q) => {
    const b = document.createElement('button');
    b.textContent = q;
    b.onclick = () => { $('content').value = q; $('content').focus(); };
    $('quick').appendChild(b);
  });

  await Promise.all([loadDevices(), loadMessages(), loadVersion(), loadXiaomi()]);
  connectWS();
  setInterval(refreshTimes, 1000);
}

/* 显示当前运行的服务端版本 —— 升级后如果这个号没变，说明旧进程还在跑 */
async function loadVersion() {
  try {
    const r = await fetch(`${BASE}/healthz`, { cache: 'no-store' });
    const d = await r.json();
    if (d && d.version) $('server-ver').textContent = `v${d.version}`;
  } catch (_) { /* 拿不到就不显示 */ }
}

/* ── 设备 ─────────────────────────────────────── */
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
}

function renderDevices() {
  const box = $('devices');
  box.innerHTML = '';
  $('dev-empty').style.display = state.devices.length ? 'none' : 'block';
  const online = state.devices.filter((d) => d.online).length;
  $('dev-count').textContent = state.devices.length
    ? `${online} 在线 / 共 ${state.devices.length} 台` : '';

  state.devices.forEach((d) => {
    const el = document.createElement('div');
    el.className = 'dev';
    el.innerHTML = `
      <div class="dev-top">
        <span class="sdot ${d.online ? 'on' : ''}"></span>
        <span class="dev-name"></span>
        <span class="dev-state ${d.online ? 'on' : ''}" style="margin-left:auto">
          ${d.online ? '在线' : '离线'}
        </span>
      </div>
      <div class="dev-meta"></div>
      <div class="dev-acts">
        <button data-act="conv">对话</button>
        <button data-act="shot" ${d.online ? '' : 'disabled'}>查看桌面</button>
        ${d.xiaomi ? `<button data-act="wake" ${d.online ? 'disabled' : ''}
            title="执行米家「${d.xiaomi.name}」的${(d.xiaomi.power_action === 'off') ? '关闭' : '开启'}动作">开机</button>` : ''}
        <button data-act="shutdown" ${d.online ? '' : 'disabled'}>关机</button>
      </div>`;
    el.querySelector('.dev-name').textContent = d.name;
    el.querySelector('.dev-meta').textContent =
      (d.type === 'pc' ? 'Windows PC' : d.type) +
      (d.platform ? ` · ${d.platform.split(/\s+/)[0]}` : '') +
      (d.last_seen ? ` · 最后在线 ${d.last_seen}` : ' · 从未上线');
    el.querySelectorAll('button').forEach((b) => {
      b.onclick = () => deviceAction(d, b.dataset.act);
    });
    box.appendChild(el);
  });
}

function renderTargets() {
  const box = $('targets');
  box.innerHTML = '';
  state.devices.forEach((d) => {
    const el = document.createElement('div');
    el.className = 'chip' + (state.selected.has(d.device_id) ? ' sel' : '') + (d.online ? '' : ' off');
    el.innerHTML = `<span class="sdot ${d.online ? 'on' : ''}"></span><span></span>`;
    el.querySelector('span:last-child').textContent = d.name;
    el.onclick = () => {
      if (state.selected.has(d.device_id)) state.selected.delete(d.device_id);
      else state.selected.add(d.device_id);
      renderTargets();
    };
    box.appendChild(el);
  });
  $('btn-send').disabled = state.selected.size === 0;
}

/* ── 设备动作 ─────────────────────────────────── */
async function deviceAction(d, act) {
  if (act === 'conv') {
    openConversation(d);
  } else if (act === 'shot') {
    openModal(`${d.name} · 桌面截图`, '正在请求截图……');
    try {
      const r = await api(`/api/devices/${d.device_id}/screenshot`, { method: 'POST' });
      $('modal-img').src = r.data_url;
      $('modal-foot').textContent =
        `${r.width}×${r.height} · ${(r.bytes / 1024).toFixed(0)} KB · ${r.taken_at}` +
        (r.screen_locked ? ' · ⚠ 屏幕可能处于锁屏状态' : '');
    } catch (e) {
      $('modal-foot').textContent = '截图失败：' + e.message;
    }
  } else if (act === 'wake') {
    const plug = d.xiaomi ? `「${d.xiaomi.name}」` : '绑定的米家设备';
    const verb = (d.xiaomi && d.xiaomi.power_action === 'off') ? '关闭' : '开启';
    if (!confirm(`确定要${verb}米家设备${plug}吗？`)) return;
    toast(`正在${verb}米家设备……`);
    try {
      const r = await api(`/api/devices/${d.device_id}/wake`, { method: 'POST' });
      toast(r.already_online ? '设备已经在线' : (r.message || '已发出指令'));
    } catch (e) {
      toast((e.message || '').includes('未绑定') ? '这台 PC 还没绑定米家开关，去「设置 → 米家」里配一下' : ('操作失败：' + e.message));
    }
  } else if (act === 'shutdown') {
    if (!confirm(`确定要让「${d.name}」关机吗？\n\nPC 端会立即执行关机（有几秒缓冲，可在电脑上运行 shutdown /a 取消）。`)) return;
    toast(`正在下发关机指令……`);
    try {
      const r = await api(`/api/devices/${d.device_id}/shutdown`, { method: 'POST' });
      toast(r.message || '关机指令已下发');
    } catch (e) {
      toast('关机失败：' + e.message);
    }
  }
}

/* ── 消息 ─────────────────────────────────────── */
async function sendMessage() {
  const content = $('content').value.trim();
  if (!content) return;
  if (state.selected.size === 0) return;
  $('btn-send').disabled = true;
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
    $('send-hint').textContent = off.length
      ? `已发送；${off.length} 个设备离线，将在上线后补投`
      : '已送达设备';
    upsertMessage(r.message);
    setTimeout(() => { $('send-hint').textContent = ''; }, 4000);
  } catch (e) {
    $('send-hint').textContent = '发送失败：' + e.message;
  } finally {
    $('btn-send').disabled = false;
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
  state.messages.forEach((m) => {
    const el = document.createElement('div');
    el.className = 'msg';
    // 颜色只看昵称，不看这条是从网页还是从设备来的
    el.style.setProperty('--nick', nickColor(m.sender_name));

    const head = document.createElement('div');
    head.className = 'msg-head';
    const left = document.createElement('span');
    left.className = 'msg-sender';
    left.textContent = m.sender_name;
    if (m.sender_kind === 'device') {
      const badge = document.createElement('span');
      badge.className = 'badge';
      badge.textContent = 'PC 回复';
      left.appendChild(badge);
    }
    const right = document.createElement('span');
    right.textContent = m.created_at;
    head.append(left, right);

    const body = document.createElement('div');
    body.className = 'msg-body';
    body.textContent = m.content;

    const tags = document.createElement('div');
    tags.className = 'tags';
    // 设备回复的接收方是「Web Sender」统一入口，没有逐设备投递状态
    if (m.sender_kind !== 'device') {
      (m.targets || []).forEach((t) => {
        const dev = state.devices.find((d) => d.device_id === t.device_id);
        const tag = document.createElement('span');
        const rank = STATE_ORDER.indexOf(t.status);
        tag.className = 'tag' + (rank >= 3 ? ' s-done' : (rank < 2 ? ' s-fail' : ''));
        tag.textContent = `${dev ? dev.name : t.device_id} · ${STATE_LABEL[t.status] || t.status}`;
        tags.appendChild(tag);
      });
    }

    el.append(head, body, tags);
    box.appendChild(el);
  });
}

/* ── 对话视图（Web Sender ⇄ 某台设备）──────────── */
let convDevice = null;

async function openConversation(device) {
  convDevice = device;
  $('conv-title').textContent = `对话 · ${device.name}`;
  $('conv-state').textContent = device.online ? '在线' : '离线（消息会在它上线后补投）';
  renderNameSelectors();
  $('conv').classList.add('show');
  $('conv-text').focus();
  await refreshConversation();
}

async function refreshConversation() {
  if (!convDevice) return;
  try {
    const list = await api(`/api/conversations/${convDevice.device_id}?limit=100`);
    const box = $('conv-body');
    box.innerHTML = '';
    if (list.length === 0) {
      const p = document.createElement('p');
      p.className = 'empty';
      p.textContent = '还没有消息往来。';
      box.appendChild(p);
    }
    list.forEach((m) => {
      const b = document.createElement('div');
      b.className = 'bubble ' + (m.sender_kind === 'device' ? 'out' : 'in');
      b.style.setProperty('--nick', nickColor(m.sender_name));
      const meta = document.createElement('span');
      meta.className = 'meta';
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

/* ── WebSocket 实时事件 ───────────────────────── */
function connectWS() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const ws = new WebSocket(`${proto}://${location.host}${BASE}/ws/web`);
  ws.onopen = () => $('ws-dot').classList.add('on');
  ws.onclose = () => { $('ws-dot').classList.remove('on'); setTimeout(connectWS, 3000); };
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
      toast(`${d.result.plug || '米家设备'} 已开启，等待 PC 上线……`);
    } else if (d.type === 'xiaomi') {
      loadXmBound();
    } else if (d.type === 'shutdown_sent') {
      const dev = state.devices.find((x) => x.device_id === d.device_id);
      toast(`「${dev ? dev.name : d.device_id}」关机指令已下发`);
    }
  };
}

/* ══════════════════════════════════════════════
   发送昵称：纯本地，存在浏览器 localStorage，服务端不参与
   ══════════════════════════════════════════════ */
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
    p.className = 'empty';
    p.textContent = '还没有昵称，先添加一个吧。';
    box.appendChild(p);
    return;
  }

  names.forEach((name, idx) => {
    const row = document.createElement('div');
    row.className = 'name-row';

    const sw = document.createElement('span');
    sw.className = 'swatch';
    sw.style.background = nickColor(name);
    sw.style.cssText += 'width:12px;height:12px;border-radius:50%;flex:0 0 12px;display:inline-block';

    const input = document.createElement('input');
    input.value = name;
    input.maxLength = 16;
    input.onchange = () => {
      const v = input.value.trim();
      if (!v || (names.includes(v) && names[idx] !== v)) {
        input.value = names[idx];
        return;
      }
      names[idx] = v;
      persistNames();
      renderNameSelectors();
    };

    const del = document.createElement('button');
    del.className = 'del';
    del.textContent = '删除';
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
  }
  $('name-new').value = '';
  rememberSender(v);
  renderNameSelectors();
}

/* ══════════════════════════════════════════════
   米家开关绑定（官方 OAuth2 + MIoT 云端）
   模型：米家设备 + 动作(开/关) → 某台 PC
   这个绑定只用于「设备清单里那台 PC 上的『开机』按钮」。
   ══════════════════════════════════════════════ */
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
  $('xm-login').style.display = on ? 'none' : 'block';
  $('xm-authed').style.display = on ? 'block' : 'none';
  $('xm-state-line').textContent = on
    ? '已授权米家。绑定规则：米家设备 + 动作 → 某台 PC（仅用于设备清单上的「开机」按钮）'
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
    $('xm-url-box').style.display = 'block';
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
    await api('/api/xiaomi/exchange', {
      method: 'POST',
      body: JSON.stringify({ code }),
    });
    $('xm-code').value = '';
    $('xm-url-box').style.display = 'none';
    $('xm-exchange-hint').textContent = '';
    await loadXiaomi();
    toast('米家授权成功');
  } catch (e) {
    $('xm-exchange-hint').textContent = '授权失败：' + e.message;
  }
}

async function xmLogout() {
  if (!confirm('确定要退出米家授权吗？下次需要重新走一遍浏览器授权。')) return;
  try {
    await api('/api/xiaomi/logout', { method: 'POST' });
    xmDevices = [];
    xmBoundList = [];
    await loadXiaomi();
    toast('已退出米家');
  } catch (e) {
    toast('退出失败：' + e.message);
  }
}

async function xmDiscover() {
  const box = $('xm-bound');
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
  if (!mid) {
    toast('先选一个米家设备（点「刷新米家设备」）');
    return;
  }
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
    toast('已添加绑定');
    await loadXmBound();
    await loadDevices();          // 设备卡片上的「开机」按钮要跟着出现
  } catch (e) {
    toast('添加失败：' + e.message);
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
    box.innerHTML = '<p class="hint">还没有绑定。上面选好设备、动作和目标 PC 后点「添加绑定」。</p>';
    return;
  }

  const sec = document.createElement('span');
  sec.className = 'xm-sec';
  sec.textContent = '已建立的绑定（改完即时生效；「开机」按钮只出现在被关联的 PC 上）';
  box.appendChild(sec);

  xmBoundList.forEach((row) => {
    const el = document.createElement('div');
    el.className = 'xm-item';

    const meta = document.createElement('div');
    meta.className = 'meta';
    const n = document.createElement('div');
    n.className = 'n';
    n.textContent = row.name;
    const dd = document.createElement('div');
    dd.className = 'd';
    dd.textContent = `${row.urn || '未知型号'} · 电源 siid=${row.power_siid} piid=${row.power_piid}`;
    meta.append(n, dd);

    const acts = document.createElement('div');
    acts.className = 'acts';

    const actPick = document.createElement('div');
    actPick.className = 'md-select';
    actPick.style.minWidth = '118px';
    buildSelect(actPick, XM_ACTIONS, row.power_action || 'on',
      (v) => xmPatch(row, { power_action: v }));

    const pcPick = document.createElement('div');
    pcPick.className = 'md-select';
    pcPick.style.minWidth = '140px';
    const pcItems = [{ value: '', label: '不指定 PC' }].concat(
      state.devices.map((d) => ({ value: d.device_id, label: d.name })));
    buildSelect(pcPick, pcItems, row.target_device_id || '',
      (v) => xmPatch(row, { target_device_id: v }));

    const del = document.createElement('button');
    del.className = 'btn btn-text small';
    del.style.color = 'var(--bad)';
    del.textContent = '删除';
    del.onclick = () => xmUnbind(row);

    acts.append(actPick, pcPick, del);
    el.append(meta, acts);
    box.appendChild(el);
  });
}

async function xmPatch(row, fields) {
  try {
    await api(`/api/xiaomi/devices/${row.id}`, {
      method: 'PATCH',
      body: JSON.stringify(fields),
    });
    Object.assign(row, fields);
    toast('已更新');
    await loadDevices();
  } catch (e) {
    toast('更新失败：' + e.message);
    await loadXmBound();
  }
}

async function xmUnbind(row) {
  if (!confirm(`确定删除绑定「${row.name}」吗？`)) return;
  try {
    await api(`/api/xiaomi/devices/${row.id}`, { method: 'DELETE' });
    await loadXmBound();
    await loadDevices();
    toast('已删除');
  } catch (e) {
    toast('删除失败：' + e.message);
  }
}

/* ── 设置面板 ─────────────────────────────────── */
function openSettings() {
  renderThemeGrid();
  renderNamesList();
  loadXiaomi();          // 米家那块也要刷新（设备列表可能变了）
  $('settings').classList.add('show');
}

function renderThemeGrid() {
  const box = $('theme-grid');
  box.innerHTML = '';
  const cur = loadTheme();
  THEMES.forEach((t) => {
    const el = document.createElement('button');
    el.type = 'button';
    el.className = 'theme-item' + (t.id === cur ? ' sel' : '');
    const sw = document.createElement('span');
    sw.className = 'sw';
    sw.style.background = t.c;
    const lb = document.createElement('span');
    lb.className = 't';
    lb.textContent = t.name;
    el.append(sw, lb);
    el.onclick = () => { applyTheme(t.id); renderThemeGrid(); };
    box.appendChild(el);
  });
}

/* ── 工具 ─────────────────────────────────────── */
function refreshTimes() {
  const d = new Date();
  $('server-time').textContent = d.toLocaleTimeString('zh-CN', { hour12: false });
}

function openModal(title, foot) {
  $('modal-title').textContent = title;
  $('modal-img').removeAttribute('src');
  $('modal-foot').textContent = foot || '';
  $('modal').classList.add('show');
}

let toastTimer = null;
function toast(text) {
  $('send-hint').textContent = text;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('send-hint').textContent = ''; }, 6000);
}

/* ── 事件绑定 ─────────────────────────────────── */
$('btn-send').onclick = sendMessage;
$('btn-refresh').onclick = () => Promise.all([loadDevices(), loadMessages()]);
$('btn-history').onclick = () => { state.limit += 30; loadMessages(); };
$('modal-close').onclick = () => $('modal').classList.remove('show');
$('modal').onclick = (e) => { if (e.target.id === 'modal') $('modal').classList.remove('show'); };
$('conv-close').onclick = () => { $('conv').classList.remove('show'); convDevice = null; };
$('conv').onclick = (e) => { if (e.target.id === 'conv') { $('conv').classList.remove('show'); convDevice = null; } };
$('conv-send').onclick = sendFromConversation;
$('conv-text').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); sendFromConversation(); }
});
$('content').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) sendMessage();
});

$('btn-settings').onclick = openSettings;
$('settings-close').onclick = () => $('settings').classList.remove('show');
$('settings').onclick = (e) => { if (e.target.id === 'settings') $('settings').classList.remove('show'); };
$('name-add').onclick = addName;
$('name-new').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); addName(); }
});

/* 米家 */
$('xm-get-url').onclick = xmGetUrl;
$('xm-exchange').onclick = xmExchange;
$('xm-logout').onclick = xmLogout;
$('xm-discover').onclick = xmDiscover;
$('xm-code').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); xmExchange(); }
});

boot().catch((e) => { console.error(e); toast('初始化失败：' + e.message); });
