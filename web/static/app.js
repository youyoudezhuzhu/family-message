/* 家庭消息控制台 —— 原生 JS，零构建 */

const STATE_ORDER = ['created', 'server_received', 'device_received', 'popup_displayed', 'read'];
const STATE_LABEL = {
  created: '已创建',
  server_received: '服务器已接收',
  device_received: 'PC 已收到',
  popup_displayed: '弹窗已显示',
  read: '已点击「知道了」',
};
const QUICK = ['下来吃饭了', '该睡觉了', '有人找你', '快出来一下', '开会中，勿扰'];

/* 裸端口访问时 BASE=""；走飞牛网关时 BASE="/app/family-message"。
   所有请求都拼 BASE，两种入口下代码完全一致。 */
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

/* ── 初始化 ───────────────────────────────────── */
async function boot() {
  state.config = await api('/api/config');
  renderNameSelectors();

  $('quick').innerHTML = '';
  QUICK.forEach((q) => {
    const b = document.createElement('button');
    b.textContent = q;
    b.onclick = () => { $('content').value = q; $('content').focus(); };
    $('quick').appendChild(b);
  });

  await Promise.all([loadDevices(), loadMessages()]);
  connectWS();
  setInterval(refreshTimes, 1000);
}

/* ── 设备 ─────────────────────────────────────── */
async function loadDevices() {
  state.devices = await api('/api/devices');
  // 默认选中全部在线设备
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
        <button data-act="wake" ${d.online ? 'disabled' : ''}>远程开机</button>
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
    el.className = 'target' + (state.selected.has(d.device_id) ? ' sel' : '') + (d.online ? '' : ' off');
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
    if (!confirm(`确定要开启「${d.name}」的米家电源吗？`)) return;
    toast(`正在开启米家电源……`);
    try {
      const r = await api(`/api/devices/${d.device_id}/wake`, { method: 'POST' });
      toast(r.already_online ? '设备已经在线' : (r.message || '已发出开机指令'));
    } catch (e) {
      toast('开机失败：' + e.message);
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
        sender_name: $('sender').value,
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
    el.className = 'msg' + (m.sender_kind === 'device' ? ' reply' : '');
    const head = document.createElement('div');
    head.className = 'msg-head';
    const left = document.createElement('span');
    left.className = 'msg-sender';
    left.textContent = m.sender_name;
    if (m.sender_kind === 'device') {
      const badge = document.createElement('span');
      badge.className = 'badge';
      badge.textContent = '设备回复';
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
  $('conv-state').textContent = device.online ? '🟢 在线' : '⚪ 离线（消息会在它上线后补投）';
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
      // in = 网页发出去的（靠左）；out = 设备回复的（靠右）
      b.className = 'bubble ' + (m.sender_kind === 'device' ? 'out' : 'in');
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
  try {
    await api('/api/messages', {
      method: 'POST',
      body: JSON.stringify({
        sender_name: $('conv-sender').value,
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
    if (d.type === 'device_status' || d.type === 'device_updated') {
      loadDevices();
    } else if (d.type === 'device_deleted') {
      loadDevices();
    } else if (d.type === 'message') {
      upsertMessage(d.message);
      // 对话窗开着且这条属于当前设备 → 实时刷新
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
      toast(`⚡ ${d.result.plug || '米家设备'} 已开启，等待 PC 上线……`);
    }
  };
}

/* ── 发送昵称：纯本地，存在浏览器 localStorage，服务端不参与 ── */
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
  return ['我'];   // 首次打开给一个默认昵称，之后随便改
}

function persistNames() {
  try { localStorage.setItem(NAMES_KEY, JSON.stringify(names)); } catch (_) {}
}

function rememberSender(value) {
  try { localStorage.setItem(LAST_SENDER_KEY, value || ''); } catch (_) {}
}

/** 把本地昵称同步到两个下拉框（主发送区 + 对话窗） */
function renderNameSelectors() {
  const keepMain = $('sender').value ||
    (() => { try { return localStorage.getItem(LAST_SENDER_KEY) || ''; } catch (_) { return ''; } })();

  $('sender').innerHTML = '';
  names.forEach((n) => $('sender').add(new Option(n, n)));
  if (keepMain && names.includes(keepMain)) $('sender').value = keepMain;

  const keepConv = $('conv-sender').value;
  $('conv-sender').innerHTML = '';
  names.forEach((n) => $('conv-sender').add(new Option(n, n)));
  if (keepConv && names.includes(keepConv)) $('conv-sender').value = keepConv;

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

    const input = document.createElement('input');
    input.value = name;
    input.maxLength = 16;
    input.onchange = () => {
      const v = input.value.trim();
      if (!v || (names.includes(v) && names[idx] !== v)) {
        input.value = names[idx];   // 空值或重名，回滚
        return;
      }
      names[idx] = v;
      persistNames();
      renderNameSelectors();
    };

    const del = document.createElement('button');
    del.textContent = '删除';
    del.onclick = () => {
      names.splice(idx, 1);
      if (names.length === 0) names = ['我'];   // 至少留一个
      persistNames();
      renderNameSelectors();
    };

    row.append(input, del);
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
  $('sender').value = v;
  rememberSender(v);
  renderNameSelectors();
  $('sender').value = v;
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
$('sender').addEventListener('change', () => rememberSender($('sender').value));

$('btn-names').onclick = (e) => {
  e.preventDefault();
  renderNamesList();
  $('names').classList.add('show');
  $('name-new').focus();
};
$('names-close').onclick = () => $('names').classList.remove('show');
$('names').onclick = (e) => { if (e.target.id === 'names') $('names').classList.remove('show'); };
$('name-add').onclick = addName;
$('name-new').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') { e.preventDefault(); addName(); }
});

boot().catch((e) => { console.error(e); toast('初始化失败：' + e.message); });
