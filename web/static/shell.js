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
 * 形态：host.hello.mode=popup → 全屏消息弹窗；=client → PC 群聊客户端窗口
 *       （只有群聊消息流 + 回复栏，见 §6b）；=console → 网页端那个完整控制台界面。
 * 形态由**宿主**用 host.hello/host.mode 决定；页面自己**不再**发 web.switch_mode
 * （两个「控制台」按钮已从页面删掉：PC 端不展示设备管理，设备页只在 NAS 网页端）。
 */
(function () {
  'use strict';

  /* ── 0. 是不是壳 ──────────────────────────────────────────────
     真壳：WebView2 往页面注入 window.chrome.webview；
     命令行调试：?shell=1（配合 Playwright 注入的假桥，或没有宿主时的兜底）。 */
  var params = new URLSearchParams(location.search);
  var HAS_BRIDGE = !!(window.chrome && window.chrome.webview);
  var IS_SHELL = HAS_BRIDGE || params.get('shell') === '1';
  var PARAM_MODE = params.get('mode') === 'popup' || params.get('mode') === 'client'
    || params.get('mode') === 'console' ? params.get('mode') : '';

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
  var CLIENT_HISTORY = 100;      // 客户端进来时拉多少条历史（现有接口的上限内）
  var CLIENT_MAX_NODES = 300;    // 客户端窗口里最多留多少条 DOM（防长跑无界增长）
  var CLIENT_STICK_SLACK = 24;   // 离底部多少像素以内算「还贴在底部」

  var S = {
    mode: '',                 // '' | 'popup' | 'client' | 'console'
    helloDone: false,
    pendingHello: '',         // hello 比 start() 先到时先存着
    deviceId: '',             // host.hello 带来的「我是哪台机器」（顶栏本机标识用）
    deviceName: '',
    hostNames: null,          // 宿主带来的昵称表（PC 端本地设置这份），没有就用网页端那份
    hostName: '',             // 宿主默认选中的昵称
    consoleBoot: null,        // app.js 传进来的「起控制台」函数
    consolePromise: null,     // 只跑一次
    consoleBooted: false,
    rendered: [],             // [{ msg, el }] —— 弹窗舞台里已有的消息节点（复用，不重建）
    clientList: [],           // [{ msg, el }] —— 客户端窗口里的完整消息记录
    clientLoaded: false,      // 历史拉过没有（同一个形态重复通知时不重复拉）
    clientLoading: null,      // 拉历史中的 Promise（防重入）
    clientNote: '',           // 历史没读出来时的原因（空态里如实说明）
    clientStick: true,        // 记录是否贴在底部；用户往上翻时置 false，别打断他
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

  /* ── 3. 状态机：popup / client / console ────────────────────── */
  function setMode(mode, why) {
    if (mode !== 'popup' && mode !== 'client' && mode !== 'console') return;
    if (S.mode === mode) {
      // 已经是这个形态：界面可能还没起来（宿主又发了一次，或从别处切回来）
      if (mode === 'console') enterConsole();
      else if (mode === 'client') showClient();
      return;
    }
    console.info('[shell] 切形态 →', mode, why || '');
    // 供 CSS 用：client 形态要把下面的网页控制台收起来（见 shell.css 15.6）
    document.documentElement.setAttribute('data-shell-mode', mode);
    if (mode === 'popup') enterPopup();
    else if (mode === 'client') enterClient();
    else enterConsole();
  }

  /** 弹窗形态：全屏消息弹窗 */
  function enterPopup() {
    S.mode = 'popup';
    hideClient();
    var pop = $('shell-popup');
    if (!pop) return;
    pop.hidden = false;
    ensureSenderCombo($('popup-sender'));
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
    hideClient();
    var pop = $('shell-popup');
    if (pop) pop.hidden = true;
    ensureConsole().then(function () {
      setBoot(false);
    }).catch(function () {
      // 起不来时把加载态收掉，让 app.js 自己的错误提示（口令框 / Toast）露出来
      setBoot(false);
    });
  }

  /* ── 3b. 客户端形态（mode=client）：群聊视图 —— 消息流 + 回复栏 ────
     跟弹窗同一套视觉语言（同一批 .popup-* 组件），区别只在：
       · 没有「知道了」（消息已经在看板上了，不需要人确认）
       · 没有侧边栏/导航，**也没有「控制台」入口**（设备页只在 NAS 网页端显示）
       · 消息是**整个群聊的完整记录**（进形态时拉一次），不是弹窗那最多 6 条的舞台
     实时事件照旧走桥（host.message），不连 /ws/web。 */
  function enterClient() {
    showClient();
    ensureClientHistory(true);    // 进这个形态时拉一次群聊记录
  }

  /** 让客户端窗口就位（不碰 HTTP；宿主重复通知同一个形态时走这里） */
  function showClient() {
    S.mode = 'client';
    var el = $('shell-client');
    if (!el) return;
    el.hidden = false;
    ensureSenderCombo($('client-sender'));
    bindClientScroll();
    updateClientEmpty();
    renderClientStack(null);
    setBoot(false);
    focusReply();
  }

  function hideClient() {
    var el = $('shell-client');
    if (el) el.hidden = true;
  }

  /** 群聊流：**不按设备拉**，就是这个共享空间里最近的一批消息。
      现有接口原样调用（GET /api/messages），不改路径/结构；它对外只有单一状态
      status="sent"，所以客户端窗口也没有逐设备状态可展示。
      force=true 表示「进这个形态了，重新拉一次」（同一形态重复通知不会重复拉）。 */
  function ensureClientHistory(force) {
    if (S.clientLoading) return S.clientLoading;
    if (S.clientLoaded && !force) return Promise.resolve();
    S.clientNote = '';
    updateClientEmpty();          // 先显示「正在读取消息记录…」
    S.clientLoading = fetchClientHistory().then(function (list) {
      S.clientLoading = null;
      S.clientLoaded = true;
      resetClientStack();
      S.clientList = list.map(function (m) { return { msg: m, el: null }; });
      renderClientStack(null);
      updateClientEmpty();
      scrollClientToBottom(true);
    }).catch(function (e) {
      S.clientLoading = null;
      S.clientLoaded = true;
      S.clientNote = (e && e.message) ? e.message : '未知错误';
      updateClientEmpty();
    });
    return S.clientLoading;
  }

  function fetchClientHistory() {
    return fmApi('/api/messages?limit=' + CLIENT_HISTORY).then(function (list) {
      if (!Array.isArray(list)) return [];
      // 群聊流接口是**新→旧**；消息记录要从上往下读，统一成时间正序（旧→新）
      return list.slice().sort(function (a, b) {
        return (Number(a && a.id) || 0) - (Number(b && b.id) || 0);
      });
    });
  }

  /** 借用 app.js 那套 api()（同一份 401 → 口令框、同一份错误提示），
      拿不到（app.js 没加载 / 还没初始化）就退回等价的 fetch。 */
  function fmApi(path) {
    var base = '';
    try { base = BASE; } catch (_) {}
    try {
      if (typeof api === 'function') return Promise.resolve(api(path));
    } catch (_) { /* api 还在 TDZ 里 */ }
    return fetch(base + path, {
      headers: { 'Content-Type': 'application/json' },
      credentials: 'same-origin',
    }).then(function (r) {
      if (!r.ok) throw new Error('HTTP ' + r.status);
      return r.json();
    });
  }

  function resetClientStack() {
    var stack = $('client-stack');
    if (stack) stack.textContent = '';
    S.clientList = [];
  }

  /** 空态文案：加载中 / 群里还没有消息 / 没读出来（如实说明原因） */
  function updateClientEmpty() {
    var title = $('client-empty-title');
    var hint = $('client-empty-hint');
    var first = !S.clientLoaded;          // 还在读历史 = 首屏
    if (title) {
      title.textContent = S.clientNote ? '没能读取群聊消息'
        : (first ? '正在读取消息记录…' : '群里还没有消息');
    }
    if (hint) {
      hint.textContent = S.clientNote ? ('服务端说：' + S.clientNote)
        : (first ? '' : '家里人在群里说的、和这台电脑回复的，都在这里，靠昵称区分谁说的。');
    }
    var empty = $('client-empty');
    if (empty) empty.hidden = S.clientList.length > 0;
  }

  /** 桥推来的新消息：进客户端窗口的记录（贴在底部时自动滚下去） */
  function pushClient(m) {
    if (!m || typeof m !== 'object') return;
    var dup = S.clientList.some(function (r) {
      return r.msg === m || (m.id != null && r.msg.id === m.id);
    });
    if (dup) return;
    S.clientList.push({ msg: m, el: null });
    while (S.clientList.length > CLIENT_MAX_NODES) {
      var gone = S.clientList.shift();
      if (gone.el) gone.el.remove();
    }
    renderClientStack(m);
    updateClientEmpty();
  }

  /* 记录是不是还贴着底部：用户往上翻看旧消息时，不把他拽回来 */
  function clientAtBottom() {
    var s = $('client-stage');
    if (!s) return true;
    return s.scrollHeight - s.scrollTop - s.clientHeight <= CLIENT_STICK_SLACK;
  }

  function bindClientScroll() {
    var s = $('client-stage');
    if (!s || s._fmScrollBound) return;
    s._fmScrollBound = true;
    s.addEventListener('scroll', function () { S.clientStick = clientAtBottom(); });
  }

  /** force=true（刚进来/刚拉完历史）一定滚到底；否则只在本来就贴底时才滚 */
  function scrollClientToBottom(force) {
    var s = $('client-stage');
    if (!s) return;
    if (!force && !S.clientStick) return;
    s.scrollTop = s.scrollHeight;
    S.clientStick = true;
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
    // 「我是哪台机器」—— 群聊模型下不再用它拉记录（客户端窗口拉的是共享空间
    // 的群聊流），只用于顶栏那个本机标识（协议只加字段，一个都没改）
    if (d.device_id) S.deviceId = String(d.device_id);
    if (d.device_name) S.deviceName = String(d.device_name);
    // PC 端自己那份昵称表（本地设置）：客户端窗口/弹窗的「以谁的名义回复」照它来
    if (Array.isArray(d.reply_names)) {
      var ns = d.reply_names.filter(function (n) {
        return typeof n === 'string' && n.trim();
      });
      S.hostNames = ns.length ? ns : null;
    }
    if (d.reply_name) S.hostName = String(d.reply_name);
    applyHostDevice();

    if (!S.helloDone) {
      S.helloDone = true;
      settleHello(normMode(d.mode));
    } else {
      setMode(d.mode, 'host.hello');
    }
    console.info('[shell] 宿主 ' + (d.version || '?') + ' · ' + (d.platform || '?')
      + ' · 服务端 ' + (d.server || '?'));
    applyHostTheme(d.theme_mode);
  }

  /** 形态取值：popup | client | console（不认识的按 console 处理，跟以前一样） */
  function normMode(m) {
    return m === 'popup' || m === 'client' ? m : 'console';
  }

  /** 顶栏里那行「这台电脑是谁」（宿主给的 device_name / device_id） */
  function applyHostDevice() {
    var el = $('client-device');
    if (!el) return;
    var name = S.deviceName || S.deviceId;
    el.textContent = name ? '· ' + name : '';
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
    setPresence($('popup-dot'), connected);
    setPresence($('client-dot'), connected);
    var pt = $('popup-conn-text'); if (pt) pt.textContent = detail;
    var ct = $('client-conn-text'); if (ct) ct.textContent = detail;
  }

  function setPresence(dot, connected) {
    if (dot) dot.className = 'presence ' + (connected ? 'presence--online' : 'presence--offline');
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
    else if (S.mode === 'client') pushClient(m);   // 客户端窗口：追加到消息记录
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
      clearReplyInput(sent);
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

  /* ── 6. 消息堆叠（弹窗与客户端窗口共用同一套渲染）─────────────
     两个形态装的是同一批节点、同一批样式（.popup-msg--lead / --prev），
     只是容器和条数不同：
       · popup  —— 全屏舞台，最多 MAX_HISTORY 条（最新一条 + 更早的几条做上下文）
       · client —— 客户端窗口，完整消息记录（进来时拉的历史 + 桥推来的新消息）
     一样是「只追加/降级节点，不重建整页」。 */
  var STACKS = {
    popup:  { stack: 'popup-stack',  empty: 'popup-empty',  list: function () { return S.rendered; } },
    client: { stack: 'client-stack', empty: 'client-empty', list: function () { return S.clientList; } },
  };

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
  function renderStack(which, newMsg) {
    var spec = STACKS[which];
    var list = spec.list();
    var stack = $(spec.stack);
    if (!stack) return;
    var empty = $(spec.empty);
    if (empty) empty.hidden = list.length > 0;

    list.forEach(function (r) {
      if (!r.el) {
        r.el = buildPopupMsgEl(r.msg);
        if (newMsg && r.msg === newMsg) r.el.classList.add('is-new');
        stack.appendChild(r.el);
      }
    });

    // 最后一条 = 主角，其余弱化成字幕
    list.forEach(function (r, i) {
      var lead = i === list.length - 1;
      r.el.classList.toggle('popup-msg--lead', lead);
      r.el.classList.toggle('popup-msg--prev', !lead);
      if (lead && r.el.classList.contains('is-new')) {
        setTimeout(function () { r.el.classList.remove('is-new'); }, 400);
      }
    });
  }

  /** 弹窗那份：渲染完直接把舞台滚到底（弹窗永远只看最新） */
  function renderPopupStack(newMsg) {
    renderStack('popup', newMsg);
    var stage = $('popup-stage');
    if (stage) stage.scrollTop = stage.scrollHeight;
  }

  /** 客户端那份：只有本来就贴着底时才滚 —— 用户正在往上翻旧消息就别打断他 */
  function renderClientStack(newMsg) {
    renderStack('client', newMsg);
    afterPaint(function () { scrollClientToBottom(false); });
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

    // 群聊模型：**只有一种状态 —— 已发送**。逐设备状态（已送达 / 已显示）
    // 服务端都不再对外暴露，这里也就不可能有第二种。跟网页端同一个 .status 组件。
    el.appendChild(buildSentTag());

    return el;
  }

  /** 单一「已发送」标签（网页端 .status / .status--sent，跨端同一套样式） */
  function buildSentTag() {
    var tag = document.createElement('span');
    tag.className = 'popup-msg__status status status--sent';
    if (typeof window.icon === 'function') tag.appendChild(window.icon('check', 'icon icon--xs'));
    tag.appendChild(Object.assign(document.createElement('span'), { textContent: '已发送' }));
    return tag;
  }

  /* ── 7. 回复区（弹窗与客户端窗口共用同一段逻辑）───────────────
     两套回复栏是同一套组件（.popup-reply + 发送人下拉 + 输入框 + 发送按钮），
     只是元素 id 不同；同一时刻只有一个形态可见，所以 pendingReply 一份就够。 */
  var REPLY_BARS = {
    popup:  { sender: 'popup-sender',  text: 'popup-reply-text',  hint: 'popup-reply-hint' },
    client: { sender: 'client-sender', text: 'client-reply-text', hint: 'client-reply-hint' },
  };

  function activeBar() {
    return S.mode === 'client' ? REPLY_BARS.client : REPLY_BARS.popup;
  }

  function ensureSenderCombo(host) {
    if (!host) host = $(activeBar().sender);
    if (!host || typeof window.buildSelect !== 'function') return;
    var list = appNames();
    var items = list.map(function (n) { return { value: n, label: n, color: nickColorOf(n) }; });
    var cur = barSender(host);
    var val = items.some(function (i) { return i.value === cur; }) ? cur : items[0].value;
    window.buildSelect(host, items, val, function (v) { rememberSenderOf(v); });
  }

  /** 昵称表：壳带来了 PC 端自己那份就用它（客户端窗口就是这台机器在说话），
      没有则沿用网页端 localStorage 那份 —— 弹窗原来的行为不变。 */
  function appNames() {
    if (Array.isArray(S.hostNames) && S.hostNames.length) return S.hostNames;
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

  /** 当前选中的发送人（下拉 → 宿主给的默认 → localStorage → 第一个） */
  function barSender(host) {
    if (!host) host = $(activeBar().sender);
    var list = appNames();
    var v = host && host._value;
    if (v && list.indexOf(v) >= 0) return v;
    var last = S.hostName || '';
    if (!last) { try { last = localStorage.getItem('fm.lastSender') || ''; } catch (_) {} }
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
    // 两个形态各有一条提示行：都写一遍，发送途中切形态也不会「回执丢了」
    Object.keys(REPLY_BARS).forEach(function (k) {
      var el = $(REPLY_BARS[k].hint);
      if (!el) return;
      el.textContent = text || '';
      el.className = 'popup-actions__hint' + (kind ? ' is-' + kind : '');
    });
  }

  /** 回执到了：把「还是刚才那条原文」的输入框清掉（哪个形态都清） */
  function clearReplyInput(sent) {
    Object.keys(REPLY_BARS).forEach(function (k) {
      var input = $(REPLY_BARS[k].text);
      if (input && input.value.trim() === sent) input.value = '';
    });
  }

  function sendReply() {
    var bar = activeBar();
    var input = $(bar.text);
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
      sender_name: barSender($(bar.sender)),
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
    var input = $(activeBar().text);
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

  /* 「打开控制台」已随群聊模型去掉：PC 端不展示设备管理（设备页只在 NAS 网页端）。
     页面不再发 web.switch_mode —— 形态由宿主用 host.mode 决定。 */

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

    // 「控制台」按钮已从页面删掉（PC 端不展示设备管理），这里不再绑 web.switch_mode。

    // 两套回复栏（弹窗 / 客户端）绑同一段逻辑
    Object.keys(REPLY_BARS).forEach(function (k) {
      var bar = REPLY_BARS[k];
      var input = $(bar.text);
      if (!input) return;
      input.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); sendReply(); }
      });
    });
    ['popup-reply-send', 'client-reply-send'].forEach(function (id) {
      var send = $(id);
      if (send) send.onclick = sendReply;
    });

    // Esc = 知道了（只有弹窗形态、且不在输入里时才抢；客户端窗口不关窗）
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
    // app.js 改完昵称后同步两套回复栏里的发送人下拉
    syncNames: function () {
      if (S.mode === 'popup') ensureSenderCombo($('popup-sender'));
      else if (S.mode === 'client') ensureSenderCombo($('client-sender'));
    },
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
