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
  const sel = $('sender');
  sel.innerHTML = '';
  state.config.senders.forEach((s) => sel.add(new Option(s, s)));

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
        <button data-act="msg">发送消息</button>
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
  if (act === 'msg') {
    state.selected = new Set([d.device_id]);
    renderTargets();
    $('content').focus();
    window.scrollTo({ top: 0, behavior: 'smooth' });
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
    el.className = 'msg';
    const head = document.createElement('div');
    head.className = 'msg-head';
    const left = document.createElement('span');
    left.className = 'msg-sender';
    left.textContent = m.sender_name;
    const right = document.createElement('span');
    right.textContent = m.created_at;
    head.append(left, right);

    const body = document.createElement('div');
    body.className = 'msg-body';
    body.textContent = m.content;

    const tags = document.createElement('div');
    tags.className = 'tags';
    (m.targets || []).forEach((t) => {
      const dev = state.devices.find((d) => d.device_id === t.device_id);
      const tag = document.createElement('span');
      const rank = STATE_ORDER.indexOf(t.status);
      tag.className = 'tag' + (rank >= 3 ? ' s-done' : (rank < 2 ? ' s-fail' : ''));
      tag.textContent = `${dev ? dev.name : t.device_id} · ${STATE_LABEL[t.status] || t.status}`;
      tags.appendChild(tag);
    });

    el.append(head, body, tags);
    box.appendChild(el);
  });
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
$('content').addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) sendMessage();
});

boot().catch((e) => { console.error(e); toast('初始化失败：' + e.message); });
