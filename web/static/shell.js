/* 家庭消息 · 控制台 —— WebView2 壳模式（额外分支）
 * ═══════════════════════════════════════════════════════════════
 * PC 端从 WPF 手绘界面改成 **WebView2 壳 + 加载网页端页面**，两端共用这一套界面。
 * 本文件就是「网页端支持壳模式」的那一半，协议见 docs/PC-WEBVIEW2-REWRITE.md §4（已冻结）。
 *
 * 三条硬规矩：
 *   1. **浏览器模式一行都不变** —— 没有 window.chrome.webview、也没带 ?shell=1 时，
 *      这个文件在顶部就 return 了，只往 window 上挂一个 { enabled:false }。
 *   2. **壳模式下页面不连 /ws/web**（宿主持有 WebSocket，免得双份消息），
 *      实时事件全走桥：host.connection / host.message / host.session / host.action_result。
 *      HTTP API 照常用（app.js 里那套原样不动）。
 *   3. **页面不直接调宿主能力** —— 截图 / 关机 / 解锁 / 退出都必须 postMessage 请求。
 *
 * 「已显示」语义：收到 host.message 后，等这一帧真正画到屏幕上（双 rAF）才发 web.ack，
 * 让宿主能如实回报 popup_displayed（提前发就是谎报）。
 * 注意：ack 只在**弹窗形态**发 —— 控制台里消息进的是列表，不是「弹窗已显示」。
 *
 * 形态：host.hello.mode=popup → 全屏消息弹窗；=console → 现有的控制台界面。
 * 之后宿主可以用 host.mode 随时切换，页面里的「打开控制台」也会发 web.switch_mode 请求。
 */
(function () {
  'use strict';

  /* ── 0. 是不是壳 ──────────────────────────────────────────────
     真壳：WebView2 往页面注入 window.chrome.webview；
     命令行调试：?shell=1（配合 Playwright 注入的假桥，或没有宿主时的兜底）。 */
  var params = new URLSearchParams(location.search);
  var HAS_BRIDGE = !!(window.chrome && window.chrome.webview);
  var IS_SHELL = HAS_BRIDGE || params.get('shell') === '1';
  var PARAM_MODE = params.get('mode') === 'popup' || params.get('mode') === 'console'
    ? params.get('mode') : '';

  if (!IS_SHELL) {
    window.FM_SHELL = { enabled: false };
    return;
  }

  // 第一帧就把壳标记写到 html 上（index.html 的内联脚本已经写过一次，这里兜底）
  document.documentElement.setAttribute('data-shell', '1');

  var HELLO_TIMEOUT = 1500;      // 等 host.hello 的兜底时限（毫秒）
  var POPUP_EMPTY_GRACE = 1200;  // 弹窗刚打开、消息还没到时，加载态再多留一会儿
  var REPLY_TIMEOUT = 15000;     // 等 host.reply_ack 的兜底时限
  var ACTION_TIMEOUT = 20000;    // 等 host.action_result 的兜底时限
  var MAX_HISTORY = 6;           // 弹窗里最多堆几条（最新的 1 条 + 更早的 5 条）
  var OWN_REPLY_CAP = 50;        // 自己回复过的 message_id 记忆上限

  var S = {
    mode: '',                 // '' | 'popup' | 'console'
    helloDone: false,
    pendingHello: '',         // hello 比 start() 先到时先存着
    consoleBoot: null,        // app.js 传进来的「起控制台」函数
    consolePromise: null,     // 只跑一次
    consoleBooted: false,
    rendered: [],             // [{ msg, el }] —— 弹窗舞台里已有的消息节点（复用，不重建）
    ownReplyIds: new Set(),   // 自己回复产生的 message_id（宿主回推时不当新消息弹）
    pendingReply: null,
    replyTimer: null,
    shotSeq: 0,
    shotWait: new Map(),      // request_id → {resolve, reject, timer}
    actWait: new Map(),       // action → {resolve, reject, timer}
    session: null,
  };

  var $ = function (id) { return document.getElementById(id); };

  /* ── 1. 桥：唯一出口 ────────────────────────────────────────── */
  function post(msg) {
    if (HAS_BRIDGE) {
      try { window.chrome.webview.postMessage(msg); } catch (e) { console.warn('[shell] postMessage 失败', e); }
      return;
    }
    console.info('[shell] 没有宿主，消息只打到控制台：', msg);
  }

  /* 等这一帧画完再回调（双 rAF = 上一次渲染已经上屏） */
  function afterPaint(fn) { requestAnimationFrame(function () { requestAnimationFrame(fn); }); }

  /* ── 2. 启动加载态（中性，不闪白 / 不闪错布局）───────────────── */
  function setBoot(on, hint) {
    var el = $('shell-boot');
    if (!el) return;
    if (hint !== undefined) {
      var h = $('shell-boot-hint');
      if (h) h.textContent = hint;
    }
    el.hidden = !on;
  }

  /* ── 3. 状态机：popup / console ─────────────────────────────── */
  function setMode(mode, why) {
    if (mode !== 'popup' && mode !== 'console') return;
    if (S.mode === mode) {
      // 已经是这个形态：控制台可能还没起来（弹窗里点「打开控制台」后宿主又发了一次）
      if (mode === 'console') enterConsole();
      return;
    }
    console.info('[shell] 切形态 →', mode, why || '');
    if (mode === 'popup') enterPopup(); else enterConsole();
  }

  /** 弹窗形态：全屏消息弹窗 */
  function enterPopup() {
    S.mode = 'popup';
    var pop = $('shell-popup');
    if (!pop) return;
    pop.hidden = false;
    ensureSenderCombo();
    renderPopupStack(null);
    // 一进来还没消息：先留在加载态，等第一条消息到了再揭开（避免闪一下空弹窗）
    if (!S.rendered.length) {
      setBoot(true, '正在接收消息…');
      setTimeout(function () { if (S.mode === 'popup') setBoot(false); }, POPUP_EMPTY_GRACE);
    } else {
      setBoot(false);
    }
    focusReply();
  }

  /** 控制台形态：现有的网页端界面，只是实时事件走桥 */
  function enterConsole() {
    S.mode = 'console';
    var pop = $('shell-popup');
    if (pop) pop.hidden = true;
    ensureConsole().then(function () {
      setBoot(false);
    }).catch(function () {
      // 起不来时把加载态收掉，让 app.js 自己的错误提示（口令框 / Toast）露出来
      setBoot(false);
    });
  }

  /** 控制台只在需要时初始化一次（弹窗形态下不碰 HTTP，免得在弹窗后面弹口令框） */
  function ensureConsole() {
    if (!S.consolePromise) {
      S.consolePromise = Promise.resolve()
        .then(function () { return S.consoleBoot ? S.consoleBoot() : null; })
        .then(function () { S.consoleBooted = true; });
    }
    return S.consolePromise;
  }

  /* ── 4. 入口：app.js 的 boot() 在壳模式下走这里 ──────────────── */
  function start(consoleBoot) {
    S.consoleBoot = consoleBoot;
    setBoot(true, '正在启动…');
    post({ type: 'web.ready' });

    // 等宿主告诉我们这是什么形态；宿主没应答就用命令行/兜底值
    waitHello().then(function (mode) { setMode(mode, 'host'); });
  }

  var helloResolve = null;
  function waitHello() {
    return new Promise(function (resolve) {
      if (S.pendingHello) { resolve(S.pendingHello); return; }   // hello 先到了
      helloResolve = resolve;
      setTimeout(function () {
        if (!S.helloDone) {
          S.helloDone = true;
          resolve(PARAM_MODE || 'console');   // 没有宿主（或宿主没应答）→ 退回网页端本来的样子
        }
      }, HELLO_TIMEOUT);
    });
  }

  /** 把形态交给正在等的那一方；start() 还没跑就记着，等它来取 */
  function settleHello(mode) {
    if (helloResolve) {
      var r = helloResolve;
      helloResolve = null;
      r(mode);
    } else {
      S.pendingHello = mode;
    }
  }

  /* ── 5. 宿主 → 页面 ────────────────────────────────────────── */
  var HANDLERS = {
    'host.hello': onHello,
    'host.connection': onConnection,
    'host.message': function (d) { onHostMessage(d.message); },
    'host.reply_ack': onReplyAck,
    'host.session': onSession,
    'host.screenshot': onScreenshot,
    'host.action_result': onActionResult,
    'host.mode': function (d) { setMode(d.mode, 'host.mode'); },
  };

  function onBridgeMessage(e) {
    var d = e && typeof e === 'object' && 'data' in e ? e.data : e;
    if (typeof d === 'string') {
      try { d = JSON.parse(d); } catch (_) { return; }   // 非 JSON 一律忽略，别让脏数据把界面搞崩
    }
    if (!d || typeof d !== 'object' || !d.type) return;
    var h = HANDLERS[d.type];
    if (h) { try { h(d); } catch (err) { console.warn('[shell] 处理 ' + d.type + ' 出错', err); } }
    else console.debug('[shell] 未处理的桥消息：', d.type);
  }

  if (HAS_BRIDGE) window.chrome.webview.addEventListener('message', onBridgeMessage);
  // 没有真桥时（例如在普通浏览器里手测 ?shell=1），允许用 window.postMessage 驱动同一套逻辑
  else window.addEventListener('message', onBridgeMessage);

  function onHello(d) {
    if (!S.helloDone) {
      S.helloDone = true;
      settleHello(d.mode === 'popup' ? 'popup' : 'console');
    } else {
      setMode(d.mode, 'host.hello');
    }
    console.info('[shell] 宿主 ' + (d.version || '?') + ' · ' + (d.platform || '?')
      + ' · 服务端 ' + (d.server || '?'));
    applyHostTheme(d.theme_mode);
  }

  /** 宿主说 Windows 现在是什么明暗 —— 用户自己没在设置里选过就跟着它 */
  function applyHostTheme(mode) {
    if (mode !== 'dark' && mode !== 'light') return;
    var stored = null;
    try { stored = localStorage.getItem('fm.mode'); } catch (_) {}
    if (stored) return;                                  // 用户显式选过，尊重用户
    if (typeof window.applyMode === 'function') window.applyMode(mode, false);
  }

  function onConnection(d) {
    var connected = d.connected !== false;
    if (typeof window.setConn === 'function') window.setConn(connected);   // 控制台标题栏那个状态
    var detail = d.detail || (connected ? '已连接' : '连接断开，重连中…');
    var t = $('ws-text'); if (t && d.detail) t.textContent = d.detail;
    var pt = $('popup-conn-text'); if (pt) pt.textContent = detail;
    var pd = $('popup-dot');
    if (pd) pd.className = 'presence ' + (connected ? 'presence--online' : 'presence--offline');
  }

  /** 宿主推来的消息（壳模式下它就是原来的 /ws/web message 帧） */
  function onHostMessage(m) {
    if (!m || typeof m !== 'object') return;

    // 自己刚回的这条：不再当新消息弹一次（「已显示」语义也不该被它触发）
    if (m.id !== undefined && m.id !== null && S.ownReplyIds.has(m.id)) {
      forwardToConsole(m);
      return;
    }

    if (S.mode === 'popup') pushPopup(m);
    forwardToConsole(m);
  }

  /** 控制台那份列表也同步一次（没起来就由它自己去 HTTP 拉全量，无害） */
  function forwardToConsole(m) {
    if (typeof window.handleServerFrame === 'function') {
      window.handleServerFrame({ type: 'message', message: m });
    }
  }

  function onReplyAck(d) {
    if (!d || !d.client_id) return;
    if (!S.pendingReply || S.pendingReply.client_id !== d.client_id) return;   // 不是这一次的
    var sent = S.pendingReply.text;
    S.pendingReply = null;
    if (S.replyTimer) { clearTimeout(S.replyTimer); S.replyTimer = null; }
    if (d.message_id !== undefined && d.message_id !== null) {
      S.ownReplyIds.add(d.message_id);
      if (S.ownReplyIds.size > OWN_REPLY_CAP) {
        S.ownReplyIds.delete(S.ownReplyIds.values().next().value);
      }
    }
    if (d.status === 'ok') {
      var input = $('popup-reply-text');
      if (input && input.value.trim() === sent) input.value = '';
      setReplyHint('已回复', 'ok');
    } else if (d.status === 'empty') {
      setReplyHint(d.detail || '内容是空的，没有发出去', 'error');
    } else {
      setReplyHint(d.detail || '发送失败，可以再试一次', 'error');
    }
  }

  /* app.js 里的 WIN_STATE / unlockState 是 const —— 它们不挂在 window 上，
     只能用标识符直接读（所以这里一律 try/catch，app.js 没加载也不炸）。 */
  var WIN_STATE_FALLBACK = { label: '状态未知', icon: 'info', cls: 'winstate--unknown' };

  function winStateOf(raw) {
    var table = null;
    try { table = WIN_STATE; } catch (_) {}
    var key = table && table[raw] ? raw : 'unknown';
    return { key: key, meta: (table && table[key]) || WIN_STATE_FALLBACK };
  }

  function unlockMap() {
    try { return unlockState || null; } catch (_) { return null; }
  }

  /** 这台电脑自己的 Windows 会话状态（宿主的能力开关也一起带回来） */
  function onSession(d) {
    S.session = d;
    var box = $('shell-local');
    if (!box) return;
    var st = winStateOf(d.windows_state);
    var key = st.key;
    var meta = st.meta;
    box.className = 'winstate shell-local ' + meta.cls;
    box.dataset.winState = key;
    box.textContent = '';
    if (typeof window.icon === 'function') box.appendChild(window.icon(meta.icon, 'icon icon--xs'));
    box.appendChild(Object.assign(document.createElement('span'),
      { textContent: '本机 · ' + meta.label }));
    var caps = [];
    if (d.can_unlock) caps.push('可解锁');
    if (d.can_screenshot) caps.push('可截图');
    if (d.can_shutdown) caps.push('可关机');
    box.title = '这台电脑的 Windows 会话状态：' + meta.label
      + (caps.length ? '（宿主能力：' + caps.join(' / ') + '）' : '');
  }

  /* ── 6. 弹窗里的消息堆叠 ───────────────────────────────────── */
  function pushPopup(m) {
    if (S.rendered.some(function (r) { return r.msg === m || (m.id != null && r.msg.id === m.id); })) return;
    S.rendered.push({ msg: m, el: null });
    while (S.rendered.length > MAX_HISTORY) {
      var gone = S.rendered.shift();
      if (gone.el) gone.el.remove();
    }
    renderPopupStack(m);
    focusReply();

    // 「已显示」：等这一帧真正上屏，再告诉宿主（这就是 popup_displayed 的依据）
    afterPaint(function () {
      setBoot(false);
      post({ type: 'web.ack', message_id: m.id });
    });
  }

  /** 只追加/降级节点，不重建整页（新消息的动画才轻） */
  function renderPopupStack(newMsg) {
    var stack = $('popup-stack');
    if (!stack) return;
    var empty = $('popup-empty');
    if (empty) empty.hidden = S.rendered.length > 0;

    S.rendered.forEach(function (r) {
      if (!r.el) {
        r.el = buildPopupMsgEl(r.msg);
        if (newMsg && r.msg === newMsg) r.el.classList.add('is-new');
        stack.appendChild(r.el);
      }
    });

    // 最后一条 = 主角，其余弱化成字幕
    S.rendered.forEach(function (r, i) {
      var lead = i === S.rendered.length - 1;
      r.el.classList.toggle('popup-msg--lead', lead);
      r.el.classList.toggle('popup-msg--prev', !lead);
      if (lead && r.el.classList.contains('is-new')) {
        setTimeout(function () { r.el.classList.remove('is-new'); }, 400);
      }
    });

    var stage = $('popup-stage');
    if (stage) stage.scrollTop = stage.scrollHeight;
  }

  function buildPopupMsgEl(m) {
    var el = document.createElement('article');
    el.className = 'popup-msg';
    el.style.setProperty('--msg-nick', nickColorOf(m.sender_name));

    var head = document.createElement('div');
    head.className = 'popup-msg__head';

    var who = document.createElement('span');
    who.className = 'popup-msg__sender';
    who.textContent = m.sender_name || '(未知)';

    var time = document.createElement('span');
    time.className = 'popup-msg__time';
    time.textContent = m.created_at || '';

    head.append(who, time);

    var body = document.createElement('div');
    body.className = 'popup-msg__body';
    body.textContent = m.content == null ? '' : String(m.content);

    el.append(head, body);
    return el;
  }

  /* ── 7. 弹窗的回复区 ───────────────────────────────────────── */
  function ensureSenderCombo() {
    var host = $('popup-sender');
    if (!host || typeof window.buildSelect !== 'function') return;
    var list = appNames();
    var items = list.map(function (n) { return { value: n, label: n, color: nickColorOf(n) }; });
    var cur = popupSender();
    var val = items.some(function (i) { return i.value === cur; }) ? cur : items[0].value;
    window.buildSelect(host, items, val, function (v) { rememberSenderOf(v); });
  }

  /** 昵称表在浏览器 localStorage 里（服务端不参与），改完昵称要跟着变 */
  function appNames() {
    try {
      return Array.isArray(names) && names.length ? names : ['我'];
    } catch (_) {
      return ['我'];
    }
  }

  function nickColorOf(name) {
    if (typeof window.nickColor === 'function') return window.nickColor(name);
    return '#90CAF9';
  }

  function popupSender() {
    var host = $('popup-sender');
    var list = appNames();
    var v = host && host._value;
    if (v && list.indexOf(v) >= 0) return v;
    var last = '';
    try { last = localStorage.getItem('fm.lastSender') || ''; } catch (_) {}
    return list.indexOf(last) >= 0 ? last : list[0];
  }

  function rememberSenderOf(v) {
    if (typeof window.rememberSender === 'function') window.rememberSender(v);
    else { try { localStorage.setItem('fm.lastSender', v || ''); } catch (_) {} }
  }

  function newId() {
    try {
      if (crypto && crypto.randomUUID) return crypto.randomUUID();
    } catch (_) {}
    return 'c-' + Date.now().toString(36) + '-' + Math.random().toString(16).slice(2, 10);
  }

  function setReplyHint(text, kind) {
    var el = $('popup-reply-hint');
    if (!el) return;
    el.textContent = text || '';
    el.className = 'popup-actions__hint' + (kind ? ' is-' + kind : '');
  }

  function sendReply() {
    var input = $('popup-reply-text');
    if (!input) return;
    var text = (input.value || '').trim();
    if (!text) { setReplyHint('先写点什么再回复', 'error'); input.focus(); return; }
    if (S.pendingReply) { setReplyHint('上一条还在发送中…', 'error'); return; }

    var id = newId();
    S.pendingReply = { client_id: id, text: text };
    setReplyHint('正在发送…', '');
    post({
      type: 'web.reply',
      client_id: id,
      sender_name: popupSender(),
      content: text,
    });
    S.replyTimer = setTimeout(function () {
      if (S.pendingReply && S.pendingReply.client_id === id) {
        S.pendingReply = null;
        setReplyHint('没收到宿主的回执，可以再发一次', 'error');
      }
    }, REPLY_TIMEOUT);
  }

  function focusReply() {
    var input = $('popup-reply-text');
    if (!input || document.activeElement === input) return;
    setTimeout(function () { try { input.focus(); } catch (_) {} }, 60);
  }

  /* ── 8. 页面主动请求宿主能力（截图 / 动作）───────────────────
     控制台里的截图 / 关机 / 解锁仍然走现有 HTTP API（宿主自己会执行并回推结果），
     下面两个接口留给「需要直接问宿主」的场景（例如本机桌面）：
     页面永远只是 postMessage 请求，拿不拿得到由宿主决定。 */
  function requestScreenshot(timeoutMs) {
    var rid = newId();
    var ttl = timeoutMs || ACTION_TIMEOUT;
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(function () {
        S.shotWait.delete(rid);
        reject(new Error('宿主没有在 ' + Math.round(ttl / 1000) + ' 秒内返回截图'));
      }, ttl);
      S.shotWait.set(rid, { resolve: resolve, reject: reject, timer: timer });
      post({ type: 'web.request_screenshot', request_id: rid });
    });
  }

  function requestAction(action, deviceId, timeoutMs) {
    var ttl = timeoutMs || ACTION_TIMEOUT;
    return new Promise(function (resolve, reject) {
      var timer = setTimeout(function () {
        S.actWait.delete(action);
        reject(new Error('宿主没有在 ' + Math.round(ttl / 1000) + ' 秒内返回结果'));
      }, ttl);
      S.actWait.set(action, { resolve: resolve, reject: reject, timer: timer });
      post({ type: 'web.request_action', action: action, device_id: deviceId || '' });
    });
  }

  function onScreenshot(d) {
    var rid = d.request_id || '';
    var w = S.shotWait.get(rid);
    if (!w) { console.debug('[shell] 收到没人认领的截图响应', rid); return; }
    S.shotWait.delete(rid);
    clearTimeout(w.timer);
    if (d.ok) w.resolve(d); else w.reject(new Error(d.error || '宿主没能截到图'));
  }

  function onActionResult(d) {
    var action = d.action || '';
    var ok = d.ok !== false;
    var detail = d.detail || '';

    // 页面自己发起的 request_action：兑现 Promise
    var w = S.actWait.get(action);
    if (w) {
      S.actWait.delete(action);
      clearTimeout(w.timer);
      if (ok) w.resolve(d); else w.reject(new Error(detail || '宿主没能完成这个动作'));
      return;
    }

    // 控制台走 HTTP 发起、宿主执行完回推的结果
    // （协议里 action_result 只有 action/ok/detail，没有 device_id —— 这里按动作类型处理）
    if (action === 'unlock') {
      try {
        var pending = unlockMap();
        if (pending && typeof window.setUnlockPhase === 'function') {
          Array.from(pending.keys()).forEach(function (id) {
            if (window.unlockPending(id)) window.setUnlockPhase(id, 'idle');
          });
        }
      } catch (_) {}
      if (typeof window.snack === 'function') {
        window.snack(detail || (ok ? '解锁完成' : '解锁失败'),
          ok ? {} : { error: true, action: '知道了' });
      }
      if (typeof window.loadDevices === 'function') window.loadDevices();
    } else if (action === 'shutdown') {
      if (typeof window.snack === 'function') window.snack(detail || '关机指令已下发');
    } else if (detail && typeof window.snack === 'function') {
      window.snack(detail, { error: !ok });
    }
  }

  /* ── 9. 界面事件 ───────────────────────────────────────────── */
  function closePopup() {
    post({ type: 'web.close' });
    setReplyHint('');
    S.pendingReply = null;
    if (S.replyTimer) { clearTimeout(S.replyTimer); S.replyTimer = null; }
  }

  function openConsole() {
    post({ type: 'web.switch_mode', mode: 'console' });
    // 宿主应当回 host.mode=console；万一没回（老宿主/调试），自己也切过去，
    // 免得点一下什么都没发生 —— 页面自己的形态只是表现层，不影响宿主窗口。
    setTimeout(function () { if (S.mode === 'popup') setMode('console', 'switch_mode 兜底'); }, 700);
  }

  function quitApp() {
    var ask = typeof window.confirmDialog === 'function'
      ? window.confirmDialog({
          title: '退出家庭消息？',
          body: '退出后这台电脑就不再接收弹窗提醒了，需要的时候从开始菜单重新打开。',
          okText: '退出',
          danger: true,
          iconName: 'i-power',
        })
      : Promise.resolve(true);
    ask.then(function (ok) { if (ok) post({ type: 'web.quit' }); });
  }

  function bindUI() {
    var ok = $('popup-ok');
    if (ok) ok.onclick = closePopup;

    var toConsole = $('popup-console');
    if (toConsole) toConsole.onclick = openConsole;

    var send = $('popup-reply-send');
    if (send) send.onclick = sendReply;

    var input = $('popup-reply-text');
    if (input) input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); sendReply(); }
    });

    // Esc = 知道了（只有弹窗形态、且不在输入里时才抢）
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || S.mode !== 'popup') return;
      var t = document.activeElement;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
      closePopup();
    });

    var quit = $('btn-quit');
    if (quit) quit.onclick = quitApp;
  }

  /* ── 10. 对外 ───────────────────────────────────────────────── */
  window.FM_SHELL = {
    enabled: true,
    start: start,
    // app.js 改完昵称后同步弹窗里的发送人下拉
    syncNames: function () { if (S.mode === 'popup') ensureSenderCombo(); },
    mode: function () { return S.mode; },
    requestScreenshot: requestScreenshot,
    requestAction: requestAction,
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bindUI);
  } else {
    bindUI();
  }
})();
