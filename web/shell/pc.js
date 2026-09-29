/* ═══════════════════════════════════════════════════════════════
   PC 端本地界面 —— 表现层（配合 shell/app.html）
   ───────────────────────────────────────────────────────────────
   职责（架构见 docs/PC-LOCAL-UI.md）：
     · 三个视图切换（client / popup / settings）—— 同一个页面，不导航、不刷新
     · 形态**只由桥决定**：起始视图 = host.hello.mode，之后 = host.mode
       （再也没有 ?shell=1&mode= 那套 URL 参数）
     · 消息渲染复用 web/static/chat.js 的 FMChat（与网页端同一份组件）
     · 页面只做两件事：画界面、把用户动作转成桥消息（web.*）

   与宿主（WebView2）的通道：window.chrome.webview.postMessage / message 事件。
   没有宿主时（普通浏览器里打开手测）不报错：动作只打到控制台，界面照常可看。

   桥协议（只读，不新增帧 —— 共享昵称那几条见 docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4）：
     页面→宿主  web.ready / web.reply / web.ack / web.close / web.quit
                web.request_screenshot / web.request_action
                web.save_config / web.open_settings
                web.nickname_select / web.nickname_create / web.nickname_rename
                web.nickname_reassign_color / web.nickname_delete / web.nickname_refresh
     宿主→页面  host.hello / host.mode / host.message / host.history / host.reply_ack
                host.connection / host.session / host.screenshot / host.action_result
                host.config_saved / host.runtime
                host.nickname / host.nickname_result / host.nickname_error
   ═══════════════════════════════════════════════════════════════ */
(function () {
  'use strict';

  var HELLO_TIMEOUT = 1500;    // 宿主没应答时退回默认视图，界面不能空等
  var REPLY_TIMEOUT = 8000;    // 没收到回复回执的超时
  var SAVE_TIMEOUT = 8000;     // 没收到保存回执的超时
  var POPUP_KEEP = 6;          // 强提醒里最多留几条（最后一条是主条，更早的做上下文）
  var SVG_NS = 'http://www.w3.org/2000/svg';
  var LS_SENDER = 'fm.pc.lastSender';   // 上次用哪个昵称回复（宿主里那份配置才权威）
  var VIEW_LABEL = { client: '消息客户端', popup: '全屏强提醒', settings: '本机设置' };
  /* 与其他端一致：服务端对外只有单一 status="sent"（逐设备状态已按群聊模型去掉） */
  var STATUS_SENT = { cls: 'status--sent', icon: 'i-check', label: '已发送' };

  /* ── 逻辑色 ID → 显示色（PC 本地页自己那份，docs/NICKNAME-SYSTEM-PLAN.md §4.4）──
     ★ 这是**客户端渲染契约**的 PC 侧实现：NAS 只发逻辑色 ID（color_01…color_16 / gray），
       显示色（含浅 / 深主题变体）由页面按 theme + ID 现算 —— 所以库里 / 协议里没有 HEX。
     ★ 两端一致性是硬要求（§11 风险 8）：本段与 web/static/nickcolor.js（Web 端）
       以及 Core 的 NicknameColor.cs **逐行等价**，改一处必须同时改另外两处，
       否则会出现「网页端一个色、PC 弹窗另一个色」。
     ⚠ 这里不引 web/static/nickcolor.js：PC 端运行时只带 shell/ 下那几个文件
       （见 FamilyAgent.csproj 的 Content 复制规则），所以必须自带一份。
     四条规则（§4.2）：
       1. dot        浅色 mix(base,#000,35%) / 深色 mix(base,#FFF,30%) —— 两个主题都 ≥3.0
       2. avatarBg / avatarFg  优先原色底：对比(白字,底)≥4.5 → 白字；否则对比(0.7 黑字,底)≥4.5 → 0.7 黑字
       3. 两头都不达标（只有 color_03/05/06/11/12 + gray 命中）→ 底 mix(base,#000,18%) + 白字
       4. 文本 / 下拉项不用色值当字色（用「色块 + 常规字色」，由调用方决定）
     ⚠ 安全（R2）：返回值全部来自本段的常量表；**未知 ID 一律走灰兜底**，
       绝不把收到的字符串塞进 style。 */
  var NICK_BASE = {
    color_01: '#5E35B1', color_02: '#3949AB', color_03: '#1E88E5', color_04: '#039BE5',
    color_05: '#00897B', color_06: '#43A047', color_07: '#7CB342', color_08: '#C0CA33',
    color_09: '#F9A825', color_10: '#FB8C00', color_11: '#F4511E', color_12: '#E53935',
    color_13: '#D81B60', color_14: '#8E24AA', color_15: '#6D4C41', color_16: '#546E7A',
    gray: '#8A8A8A',           // 本地临时昵称（不在池里，第 17 个 ID；永不分配给共享昵称）
  };
  var NICK_LOCAL_TEMP_ID = 'gray';
  var NAME_MAX_LEN = 32;       // 昵称长度上限：NAS / API / Web / PC 统一 32（r6 第 ① 条）

  function nickRgb(h) {
    return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
  }
  function nickHex(c) {
    var s = '#';
    for (var i = 0; i < 3; i++) {
      var v = Math.max(0, Math.min(255, Math.round(c[i])));
      s += (v < 16 ? '0' : '') + v.toString(16).toUpperCase();
    }
    return s;
  }
  function nickMix(a, b, p) {
    var A = nickRgb(a), B = nickRgb(b), out = [0, 0, 0];
    for (var i = 0; i < 3; i++) out[i] = A[i] * (1 - p) + B[i] * p;
    return nickHex(out);
  }
  function nickLum(h) {
    var c = nickRgb(h).map(function (v) {
      v = v / 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  }
  function nickContrast(a, b) {
    var l1 = nickLum(a), l2 = nickLum(b);
    if (l1 < l2) { var t = l1; l1 = l2; l2 = t; }
    return (l1 + 0.05) / (l2 + 0.05);
  }
  /* ★ v0.19：色表是**数据**（服务端表是权威）。上面那份 NICK_BASE 只做「首屏兜底」：
     宿主（C#）会在 host.hello / host.nickname 里带 color_table（来自服务端整表帧），
     拿到就整份覆盖；**空表不覆盖**（保留手上那份）；认不出的 ID 依旧走灰兜底（R2 不变）。 */
  var NICK_SERVER_TABLE = null;      // {color_17:'#0FA3B1', …}；null = 还没拿到服务端的表
  var NICK_TABLE_VERSION = 1;

  function nickSetTable(rows, version) {
    if (!rows || typeof rows.length !== 'number') return false;
    var next = {};
    for (var i = 0; i < rows.length; i++) {
      var id = rows[i] && rows[i].color_id, hex = rows[i] && rows[i].hex;
      if (typeof id === 'string' && typeof hex === 'string' && /^#[0-9A-Fa-f]{6}$/.test(hex)) {
        next[id] = hex.toUpperCase();
      }
    }
    if (!Object.keys(next).length) return false;   // 空表 / 全不合法 → 保留旧表，绝不清空
    NICK_SERVER_TABLE = next;
    if (typeof version === 'number') NICK_TABLE_VERSION = version;
    return true;
  }
  function nickTableVersion() { return NICK_TABLE_VERSION; }
  function nickHasServerTable() { return !!NICK_SERVER_TABLE; }

  function nickIsKnown(id) {
    if (NICK_SERVER_TABLE && Object.prototype.hasOwnProperty.call(NICK_SERVER_TABLE, id)) return true;
    return typeof id === 'string' && Object.prototype.hasOwnProperty.call(NICK_BASE, id);
  }
  /** 逻辑色 ID → 基础色值（**服务端表优先**，未知 → 灰的兜底色） */
  function nickBase(id) {
    if (NICK_SERVER_TABLE && Object.prototype.hasOwnProperty.call(NICK_SERVER_TABLE, id)) {
      return NICK_SERVER_TABLE[id];
    }
    return nickIsKnown(id) ? NICK_BASE[id] : NICK_BASE[NICK_LOCAL_TEMP_ID];
  }
  function nickTheme() {
    try {
      return document.documentElement.getAttribute('data-mode') === 'dark' ? 'dark' : 'light';
    } catch (_) { return 'light'; }
  }
  function nickDot(id, th) {
    var b = nickBase(id);
    return (th || nickTheme()) === 'dark' ? nickMix(b, '#FFFFFF', 0.30) : nickMix(b, '#000000', 0.35);
  }
  function nickAvatarBg(id) {
    var b = nickBase(id);
    if (nickContrast('#FFFFFF', b) >= 4.5) return b;
    if (nickContrast(nickMix(b, '#000000', 0.70), b) >= 4.5) return b;
    return nickMix(b, '#000000', 0.18);
  }
  function nickAvatarFg(id) {
    var b = nickBase(id);
    if (nickContrast('#FFFFFF', b) >= 4.5) return '#FFFFFF';
    if (nickContrast(nickMix(b, '#000000', 0.70), b) >= 4.5) return 'rgba(0, 0, 0, 0.7)';
    return '#FFFFFF';
  }
  /** 统一入口：FMNick.display('dot'|'avatarBg'|'avatarFg', 逻辑色 ID, 主题?) */
  var FMNick = {
    VERSION: 1,
    LOCAL_TEMP_ID: NICK_LOCAL_TEMP_ID,
    baseColors: NICK_BASE,
    isKnown: nickIsKnown,
    base: nickBase,
    /* ★ v0.19：色表以服务端为准（宿主带过来的 color_table） */
    setTable: nickSetTable,
    tableVersion: nickTableVersion,
    hasServerTable: nickHasServerTable,
    theme: nickTheme,
    dot: nickDot,
    avatarBg: nickAvatarBg,
    avatarFg: nickAvatarFg,
    display: function (kind, id, th) {
      if (kind === 'dot') return nickDot(id, th);
      if (kind === 'avatarBg') return nickAvatarBg(id);
      if (kind === 'avatarFg') return nickAvatarFg(id);
      return nickDot(id, th);
    },
  };

  var S = {
    view: 'client', prevView: 'client',
    hello: null, deviceId: '', deviceName: '',
    names: [], myName: '',
    server: '', version: '—', theme: 'system', runtime: '', platform: '',
    enroll: null, autostart: null,
    logPath: '', configPath: '',
    connected: null,
    list: [], popup: [], lastNewId: null,
    pending: null, timer: null, saveTimer: null,
    acked: {}, ownIds: {},
    /* 共享昵称的整份状态（宿主经 host.nickname 推来）。
       null / available=false = 宿主没接进来或服务端 nickname.enabled=false
       → 页面**完全走今天那套本地昵称路径**（行为与改造前逐字一致，§7 回退）。 */
    nick: null,
    nickBusy: false,
  };

  var $ = function (id) { return document.getElementById(id); };

  /* ── 桥 ──────────────────────────────────────────────────── */
  var bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;
  var HAS_BRIDGE = !!bridge;
  var SENT = [];                       // 发出去的桥消息（调试/自检用）

  function post(msg) {
    if (!msg || !msg.type) return;
    SENT.push(msg);
    if (bridge) {
      try { bridge.postMessage(msg); } catch (err) { console.warn('[pc] 发桥消息失败', msg.type, err); }
      return;
    }
    console.info('[pc] 没有宿主，消息只打到控制台：', msg);   // 普通浏览器里手测时的兜底
  }

  function onBridgeEvent(e) {
    var d = (e && typeof e === 'object' && 'data' in e) ? e.data : e;
    if (typeof d === 'string') {
      try { d = JSON.parse(d); } catch (_) { return; }        // 非 JSON 一律忽略，别把界面搞崩
    }
    if (!d || typeof d !== 'object' || !d.type) return;
    var h = HANDLERS[d.type];
    if (!h) { console.debug('[pc] 未处理的桥消息：', d.type); return; }
    try { h(d); } catch (err) { console.warn('[pc] 处理 ' + d.type + ' 出错', err); }
  }

  if (bridge && bridge.addEventListener) bridge.addEventListener('message', onBridgeEvent);
  else window.addEventListener('message', onBridgeEvent);     // 没有真桥时允许 window.postMessage 驱动

  /* ── 小工具 ──────────────────────────────────────────────── */
  function afterPaint(fn) {
    requestAnimationFrame(function () { requestAnimationFrame(fn); });
  }

  function newId() {
    try { if (window.crypto && crypto.randomUUID) return crypto.randomUUID(); } catch (_) {}
    return 'c-' + Date.now().toString(36) + '-' + Math.random().toString(16).slice(2, 10);
  }

  function idOf(m) {
    var v = (m && m.id !== undefined && m.id !== null) ? m.id : (m && m.message_id);
    var n = parseInt(v, 10);
    return isNaN(n) ? 0 : n;
  }

  function normMode(m) {
    return (m === 'popup' || m === 'client' || m === 'settings') ? m : 'client';
  }

  function lsGet(k) { try { return localStorage.getItem(k) || ''; } catch (_) { return ''; } }
  function lsSet(k, v) { try { localStorage.setItem(k, v == null ? '' : String(v)); } catch (_) {} }

  /* ── 共享昵称：本地缓存 + 消息上色（docs/NICKNAME-SYSTEM-PLAN.md §5.6 / §7 Phase 4）──
     PC 本地页**不连 /ws/web**，整表 / 广播一律由宿主经桥推来（host.nickname）。
     这里只做三件事：① 缓存整份状态；② 给 chat.js 一个上色钩子（window.FMNickResolver）；
     ③ 把「我当前用谁的名义」同步给回复栏 —— 没有「本机昵称」这个概念（PC 不拥有昵称）。 */

  /** 共享昵称模式是否生效（服务端 nickname.enabled + 宿主应答过）。false → 全走今天的本地昵称路径 */
  function nickOn() { return !!(S.nick && S.nick.available); }

  /** 灰临时昵称的显示名：宿主给的本机默认名（PC = ComputerName），没给就用本机名兜底 */
  function localTempName() {
    return (S.nick && S.nick.defaultName) || S.deviceName || S.deviceId || '我';
  }

  /** 本机当前选用的共享昵称 id；null = 灰临时（还没选 / 选了但已被删） */
  function currentNickId() {
    if (!nickOn()) return null;
    var c = S.nick.current || {};
    var id = c.nickname_id;
    return (id === null || id === undefined) ? null : Number(id);
  }

  function currentNick() {
    var id = currentNickId();
    if (id === null) return null;
    return S.nick.list.filter(function (n) { return Number(n.nickname_id) === id; })[0] || null;
  }

  /** 本机当前名义的显示名（灰临时 = 本机名） */
  function currentNickName() {
    var n = currentNick();
    if (n) return n.display_name;
    var c = (S.nick && S.nick.current) || {};
    return String(c.display_name || '').trim() || localTempName();
  }

  function nickById(id) {
    if (id === null || id === undefined || isNaN(id)) return null;
    var num = Number(id);
    for (var i = 0; i < S.nick.list.length; i++) {
      if (Number(S.nick.list[i].nickname_id) === num) return S.nick.list[i];
    }
    return null;
  }

  function nickByName(name) {
    var s = String(name == null ? '' : name).trim();
    if (!s) return null;
    for (var i = 0; i < S.nick.list.length; i++) {
      if (String(S.nick.list[i].display_name || '').trim() === s) return S.nick.list[i];
    }
    return null;
  }

  /** 一条消息的逻辑色 ID（三级查找，与 Web 端 app.js 的 nickColorIdForMessage 同口径）：
        ① 快照的 nickname_id（新消息，最准）
        ② 快照的逻辑色 ID（灰临时 = 'gray'）
           ⚠ 改造前的老消息两列都是 NULL —— 绝不当成灰，继续往下找（§3.2.1）
        ③ 名字反查昵称表（历史消息兜底）
        ④ 名字 == 本机灰临时名 → 灰
      都没命中 → null（调用方退回名字哈希色，老消息观感与今天一致） */
  function nickColorIdForMessage(msg) {
    if (!nickOn() || !msg) return null;

    var snapId = (msg.sender_nickname_id !== undefined && msg.sender_nickname_id !== null)
      ? msg.sender_nickname_id : msg.nickname_id;
    var byId = nickById(snapId);
    if (byId) return byId.color;

    var snapColor = (typeof msg.sender_color === 'string') ? msg.sender_color
                  : (typeof msg.color === 'string' ? msg.color : '');
    if (snapColor && FMNick.isKnown(snapColor)) return snapColor;

    var name = String(msg.sender_name || '').trim();
    if (name) {
      var hit = nickByName(name);
      if (hit) return hit.color;
      if (name === localTempName()) return NICK_LOCAL_TEMP_ID;   // 灰临时昵称一律灰
    }
    return null;
  }

  /** 给 chat.js 用的钩子（chat.js 不直接碰昵称表）。
      返回 null = 让调用方走名字哈希兜底（开关关闭 / 老消息 / 未知名字）。 */
  window.FMNickResolver = {
    avatarFor: function (msg) {
      if (!nickOn()) return null;
      var id = nickColorIdForMessage(msg);
      if (!id) return null;
      return { bg: FMNick.avatarBg(id), fg: FMNick.avatarFg(id) };
    },
    colorIdForMessage: function (msg) {
      return nickOn() ? nickColorIdForMessage(msg) : null;
    },
    dot: function (colorId) { return FMNick.dot(colorId); },
    enabled: function () { return nickOn(); },
  };

  /** 把「我当前用谁的名义」同步给回复栏 + 重画消息（昵称/颜色可能变了） */
  function syncNickIdentity() {
    if (nickOn()) {
      S.myName = currentNickName();
      renderClient();
      renderPopup();
    }
  }

  /** chat.js 里的投递状态徽标会找 window.icon —— PC 页面上没有 app.js，
      这里给它一个基于本页内联 sprite 的实现（同一次调用拿到的还是 Fluent 图标）。 */
  window.icon = function (name, cls) {
    var svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', cls || 'icon');
    svg.setAttribute('aria-hidden', 'true');
    if (name) {
      var use = document.createElementNS(SVG_NS, 'use');
      use.setAttribute('href', '#' + name);
      svg.appendChild(use);
    }
    return svg;
  };

  /* ── 视图 ────────────────────────────────────────────────── */
  function setView(mode) {
    mode = normMode(mode);
    S.view = mode;
    if (mode !== 'settings') S.prevView = mode;
    document.documentElement.setAttribute('data-view', mode);
    ['client', 'popup', 'settings'].forEach(function (v) {
      var el = $('v-' + v);
      if (el) el.hidden = (v !== mode);
    });
    document.title = (mode === 'settings') ? '家庭消息 · 本机设置' : '家庭消息';
    var modeEl = $('info-mode');
    if (modeEl) modeEl.textContent = VIEW_LABEL[mode];

    if (mode === 'settings') fillSettings();
    if (mode === 'popup') { ackLead(); focusReply('popup'); }
    if (mode === 'client') focusReply('client');
  }

  /* ── 连接状态 ────────────────────────────────────────────── */
  function setPresence(dot, connected) {
    if (!dot) return;
    dot.className = 'presence ' + (connected ? 'presence--online' : 'presence--offline');
  }

  function onConnection(d) {
    if (!d) return;
    S.connected = d.connected !== false;
    var detail = d.detail || (S.connected ? '已连接' : '连接断开，重连中…');
    setPresence($('client-conn-dot'), S.connected);
    setPresence($('popup-conn-dot'), S.connected);
    var a = $('client-conn-text'); if (a) a.textContent = detail;
    var b = $('popup-conn-text'); if (b) b.textContent = detail;
  }

  /* ── 消息渲染（复用 chat.js）──────────────────────────────── */
  function renderClient() {
    var box = $('client-list');
    if (box && window.FMChat) {
      window.FMChat.fill(box, S.list, { myName: S.myName, status: STATUS_SENT });
    }
    var empty = $('client-empty');
    if (empty) empty.hidden = S.list.length > 0;
  }

  function renderPopup() {
    var box = $('popup-list');
    if (!box || !window.FMChat) return;
    box.textContent = '';
    var items = S.popup.slice(-POPUP_KEEP);
    items.forEach(function (m, i) {
      var lead = (i === items.length - 1);
      var row = window.FMChat.row(m, { myName: S.myName, status: STATUS_SENT });
      row.classList.add(lead ? 'chat-row--lead' : 'chat-row--prev');
      if (lead && idOf(m) && idOf(m) === S.lastNewId) row.classList.add('is-new');
      box.appendChild(row);
    });
    var empty = $('popup-empty');
    if (empty) empty.hidden = items.length > 0;
  }

  function scrollClientToBottom() {
    var s = $('client-stage');
    if (s) afterPaint(function () { s.scrollTop = s.scrollHeight; });
  }

  /** 「已显示」语义：消息真的画到屏幕上了才回 web.ack（宿主据此回报 popup_displayed） */
  function ackLead() {
    var items = S.popup.slice(-POPUP_KEEP);
    var lead = items[items.length - 1];
    var id = idOf(lead);
    if (!id || S.acked[id]) return;
    S.acked[id] = 1;
    afterPaint(function () { post({ type: 'web.ack', message_id: id }); });
  }

  function pushPopup(m) {
    var id = idOf(m);
    if (id && S.popup.some(function (x) { return idOf(x) === id; })) return;
    S.popup.push(m);
    if (S.popup.length > POPUP_KEEP) S.popup = S.popup.slice(-POPUP_KEEP);
  }

  function onMessage(m) {
    if (!m || typeof m !== 'object') return;
    var id = idOf(m);
    var dup = S.list.some(function (x) { return id ? idOf(x) === id : x === m; });
    if (!dup) {
      S.list.push(m);
      renderClient();
      scrollClientToBottom();
    }
    pushPopup(m);
    if (S.view === 'popup') {
      S.lastNewId = id;
      renderPopup();
      if (id && !S.ownIds[id]) ackLead();
    }
  }

  function onHistory(d) {
    var list = (d && Array.isArray(d.messages)) ? d.messages
             : (Array.isArray(d) ? d : null);
    if (!list) return;
    markHistoryAt();          /* ★ 只记 title：host.history 不能冒充「实时帧」 */
    S.list = list.slice();
    renderClient();
    scrollClientToBottom();
    // 记录历史最新一条，切到强提醒时也有上下文
    S.popup = S.list.slice(-POPUP_KEEP);
    renderPopup();
    /* 宿主是「先 hello 后 history」的顺序：进 popup 视图那一刻历史还没到，
       所以这里补一次 ack —— 历史的首屏也是「真的画到屏幕上了」。 */
    if (S.view === 'popup') ackLead();
  }

  /* ── 回复栏 ──────────────────────────────────────────────── */
  var BARS = {
    client: { sender: 'client-sender', text: 'client-text', send: 'client-send', hint: 'client-hint' },
    popup:  { sender: 'popup-sender',  text: 'popup-text',  send: 'popup-send',  hint: 'popup-hint' },
  };

  function senderItems() {
    var items = S.names.slice();
    if (S.myName && items.indexOf(S.myName) < 0) items.unshift(S.myName);
    if (!items.length) items.push(S.myName || S.deviceName || '我');
    return items;
  }

  function buildSenderSelects() {
    ['client', 'popup'].forEach(function (k) { buildSenderSelect($(BARS[k].sender)); });
    if (nickOn()) {
      syncNickIdentity();          // 昵称模式：「我的名义」由选用状态决定，不是下拉里的旧名字
      return;
    }
    /* 旧的本地昵称路径：回复栏里选中的昵称 = 我的本地昵称（群聊模型：身份只看昵称）。
       选完立刻同步，自己发的消息才会靠右。 */
    var main = $(BARS.client.sender);
    if (main && main.value) S.myName = main.value;
  }

  function addOption(sel, value, label) {
    var o = document.createElement('option');
    o.value = value;
    o.textContent = label;
    sel.appendChild(o);
  }

  /** 一个回复栏的下拉。昵称模式下它就是「我以后用谁的名义」（= 选择昵称，纯本地动作）。 */
  function buildSenderSelect(sel) {
    if (!sel) return;
    sel.textContent = '';

    if (nickOn()) {
      /* 第一项是**灰临时昵称**（本机名）：它不进池、不占色、不参与同步 —— 就是「没选」。
         其余是服务端的活跃昵称。选项值是 nickname_id 字符串（'' = 灰临时）。
         ⚠ 条目**不用色值当字色**（§4.4 规则 4），色块由 CSS 的 .select 负责。 */
      var curId = currentNickId();
      addOption(sel, '', localTempName() + '（本地临时）');
      S.nick.list.forEach(function (n) {
        addOption(sel, String(n.nickname_id), String(n.display_name || ''));
      });
      sel.value = curId === null ? '' : String(curId);
      return;
    }

    var items = senderItems();
    var cur = S.myName || items[0];
    items.forEach(function (n) { addOption(sel, n, n); });
    sel.value = items.indexOf(cur) >= 0 ? cur : items[0];
  }

  /** 事件来源 → 是哪个回复栏（'client' / 'popup'）。
      两个回复栏（client / popup）**共用同一个 change handler**（见 bindUI），
      所以值必须从**触发事件的那条下拉**取：写死读 client 那条，在 popup 形态下
      会把隐藏着的旧身份**再发一遍**（客户端界面纹丝不动、宿主的选用状态也没变）。
      没有事件来源（程序化调用 / 老路径）时退回「当前形态的下拉」，
      settings 形态下两个回复栏都在 DOM 里、都不该被当成身份来源 → 退 client。 */
  function senderBarOf(ev) {
    var t = (ev && (ev.currentTarget || ev.target)) || null;
    if (t && t.id) {
      for (var k in BARS) { if (Object.prototype.hasOwnProperty.call(BARS, k) && BARS[k].sender === t.id) return k; }
    }
    return (S.view === 'popup') ? 'popup' : 'client';
  }

  /** 把「我以后用谁的名义」的本地乐观状态先落到界面上（两个下拉 + 身份 + 气泡）。
      选用是纯本地动作，但**不能只靠宿主推回 host.nickname 才动**：
      离线 / 宿主不应答时界面就「选了没反应」。这里先把界面按新选择重画，
      宿主随后推来的整份状态仍然是权威值（会覆盖回去）。 */
  function applyNickSelectionLocally(v) {
    /* ① 两个下拉一起回写（值一样才不回写，避免把用户正在滚动的列表重置） */
    Object.keys(BARS).forEach(function (k) {
      var sel = $(BARS[k].sender);
      if (sel && sel.value !== v) sel.value = v;
    });
    if (!nickOn()) return;

    /* ② 身份乐观更新：id 变了、显示名从昵称表里取（拿不到就不猜，只回写下拉） */
    var id = (v === '') ? null : parseInt(v, 10);
    if (id !== null && isNaN(id)) return;
    var row = (id === null) ? null : nickById(id);
    if (id !== null && !row) return;

    S.nick.current = row
      ? { nickname_id: Number(row.nickname_id), display_name: String(row.display_name || ''),
          color: row.color, is_local_temp: false }
      : { nickname_id: null, display_name: localTempName(), color: NICK_LOCAL_TEMP_ID, is_local_temp: true };

    syncNickIdentity();                                   // myName + 两个消息列表重画
    if (S.view === 'settings') renderNickBlock();         // 设置页的「当前名义」卡也跟手
  }

  function onSenderChange(ev) {
    var which = senderBarOf(ev);            // ★ 按事件来源取值，不写死 client
    var sel = $(BARS[which].sender);
    var v = sel ? sel.value : '';

    if (nickOn()) {
      /* 选用昵称 = **纯本地动作**（唯一能离线的，§5.2）：只发一次 web.nickname_select，
         不上传、不广播、不影响别人。'' = 切回灰临时 —— 显式传 null，
         桥靠「带没带这个字段」区分「切回灰临时」与「坏帧」。 */
      post({ type: 'web.nickname_select', nickname_id: (v === '') ? null : parseInt(v, 10) });
      applyNickSelectionLocally(v);         // 本地立刻跟手（不再等宿主推回）
      return;
    }

    S.myName = v;
    lsSet(LS_SENDER, v);
    ['client', 'popup'].forEach(function (k) {
      var sel = $(BARS[k].sender);
      if (sel && sel.value !== v) sel.value = v;
    });
    renderClient();
    renderPopup();
  }

  function setHint(id, text, kind) {
    var el = $(id);
    if (!el) return;
    el.textContent = text || '';
    el.className = 'hint' + (kind ? ' is-' + kind : '');
  }

  /** 两条提示行都写一遍：发送途中切视图也不会「回执丢了」 */
  function setHintAll(text, kind) {
    Object.keys(BARS).forEach(function (k) { setHint(BARS[k].hint, text, kind); });
  }

  function clearSentInput(sent) {
    Object.keys(BARS).forEach(function (k) {
      var input = $(BARS[k].text);
      if (input && input.value.trim() === sent) input.value = '';
    });
  }

  function sendReply(which) {
    var bar = BARS[which] || BARS.client;
    var input = $(bar.text);
    if (!input) return;
    var text = (input.value || '').trim();
    if (!text) { setHint(bar.hint, '先写点什么再回复', 'error'); input.focus(); return; }
    if (S.pending) { setHint(bar.hint, '上一条还在发送中…', 'error'); return; }

    var id = newId();
    S.pending = { client_id: id, text: text };
    setHintAll('正在发送…', '');
    post({ type: 'web.reply', client_id: id, sender_name: S.myName || '', content: text });

    if (S.timer) clearTimeout(S.timer);
    S.timer = setTimeout(function () {
      if (S.pending && S.pending.client_id === id) {
        S.pending = null;
        setHintAll('没收到宿主的回执，可以再发一次', 'error');
      }
    }, REPLY_TIMEOUT);
  }

  function focusReply(which) {
    var bar = BARS[which] || BARS.client;
    var input = $(bar.text);
    if (!input || document.activeElement === input) return;
    setTimeout(function () { try { input.focus(); } catch (_) {} }, 60);
  }

  /** 本地时间戳，格式与服务端一致（YYYY-MM-DD HH:MM:SS），避免气泡上两种写法并存 */
  function nowStamp() {
    var d = new Date(), p = function (n) { return (n < 10 ? '0' : '') + n; };
    return d.getFullYear() + '-' + p(d.getMonth() + 1) + '-' + p(d.getDate())
         + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  /* ── 顶栏诊断标记：#client-last-recv ──────────────────────────
     宿主 → 页面有两条推送路径：host.message（实时）和 host.history（重开窗口全量重拉）。
     这个标记**只由 host.message 更新**，用来区分：
       · 标记还写着「尚未收到推送」→ 帧根本没到页面（宿主/传输层）
       · 标记有时间戳但列表没动 → 帧到了、但渲染没跟上
     host.history 只写 title（「最后历史：HH:MM:SS」），不动正文。 */
  function clockNow() {
    var d = new Date(), p = function (n) { return (n < 10 ? '0' : '') + n; };
    return p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
  }

  function markRecv() {
    var el = $('client-last-recv');
    if (el) el.textContent = '最后收到 ' + clockNow();
  }

  /** host.history 不改正文，只在 title 里留个痕 —— 两条路径不能混为一谈 */
  function markHistoryAt() {
    var el = $('client-last-recv');
    if (el) el.title = '最后历史：' + clockNow();
  }

  /**
   * 自己刚发的消息**立刻上屏**。
   *
   * 群聊模型下服务端会「排除发起者自己」（不该自己发的话弹自己的窗），
   * 所以这一条**必须由本地补** —— 否则 PC 端永远看不到自己刚说的话，
   * 只有重开界面走一次历史才发现「哦，发出去了」。
   */
  function appendOwnMessage(mid, text) {
    if (!text) return;
    var m = {
      id: mid || 0, message_id: mid || 0,
      sender_name: S.myName || '', content: text,
      created_at: nowStamp(), status: 'sent',
    };
    var dup = mid && S.list.some(function (x) { return idOf(x) === mid; });
    if (!dup) {
      S.list.push(m);
      renderClient();
      scrollClientToBottom();
    }
    pushPopup(m);
    if (S.view === 'popup') renderPopup();
  }

  function onReplyAck(d) {
    if (!d || !d.client_id) return;
    if (!S.pending || S.pending.client_id !== d.client_id) return;   // 不是这一次的
    var sent = S.pending.text;
    S.pending = null;
    if (S.timer) { clearTimeout(S.timer); S.timer = null; }
    var mid = idOf(d);
    if (mid) S.ownIds[mid] = 1;
    if (d.status === 'ok') {
      clearSentInput(sent);
      appendOwnMessage(mid, sent);          // ★ 自己发的立刻上屏
      setHintAll('已回复', 'ok');
    } else if (d.status === 'queued') {
      clearSentInput(sent);
      appendOwnMessage(mid, sent);          // ★ 排队也要上屏（连上后会补发）
      setHintAll(d.detail || '已排队，连上服务端后自动补发', 'ok');
    } else {
      setHintAll(d.detail || '发送失败，可以再试一次', 'error');
    }
  }

  /* ── 主题 ────────────────────────────────────────────────── */
  var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

  function applyTheme(mode) {
    var m = (mode === 'light' || mode === 'dark') ? mode
          : (media && media.matches ? 'dark' : 'light');
    document.documentElement.setAttribute('data-mode', m);
    /* 显示色是**按主题现算**的（§4.4 规则 1：浅色圆点混 35% 黑 / 深色混 30% 白）——
       主题一变必须重画，否则圆点 / 头像还停在旧主题那套。 */
    renderClient();
    renderPopup();
    if (S.view === 'settings') renderNickBlock();
  }

  function setThemeChoice(mode) {
    S.theme = (mode === 'light' || mode === 'dark') ? mode : 'system';
    applyTheme(S.theme);
  }

  if (media && media.addEventListener) {
    media.addEventListener('change', function () { if (S.theme === 'system') applyTheme('system'); });
  }

  /* ── 设置视图 ────────────────────────────────────────────── */
  function setText(id, v) {
    var el = $(id);
    if (el) el.textContent = (v === undefined || v === null || v === '') ? '—' : String(v);
  }

  function fillSettings() {
    var server = $('set-server');
    if (server && document.activeElement !== server) server.value = S.server || '';
    var name = $('set-reply-name');
    if (name && document.activeElement !== name) name.value = S.myName || '';

    var list = $('set-reply-names');
    if (list) {
      list.textContent = '';
      S.names.forEach(function (n) {
        var o = document.createElement('option');
        o.value = n;
        list.appendChild(o);
      });
    }

    var token = $('set-token');
    var state = $('set-token-state');
    if (token) {
      token.placeholder = S.enroll === true ? '已配置 —— 留空表示不修改'
                       : S.enroll === false ? '服务端的 device.enroll_token'
                       : '服务端 config.yaml 里的 device.enroll_token';
    }
    if (state) {
      state.textContent = S.enroll === true ? '已配置' : (S.enroll === false ? '未配置' : '未知');
      state.className = 'chip' + (S.enroll === true ? ' is-ok' : (S.enroll === false ? ' is-warn' : ''));
    }

    var auto = $('set-autostart');
    if (auto) auto.checked = (S.autostart === true);

    var theme = $('set-theme');
    if (theme) theme.value = S.theme;

    setText('settings-ver', S.version);
    setText('info-version', S.version);
    setText('info-runtime', S.runtime);
    setText('info-platform', S.platform);
    setText('info-device', S.deviceName);
    setText('info-device-id', S.deviceId);
    setText('info-server', S.server);
    setText('info-mode', VIEW_LABEL[S.view]);
    setText('info-log', S.logPath);
    setText('info-config', S.configPath);

    /* 共享昵称块（§5.2）：生效时显示它，并**藏掉**旧的「回复昵称」输入框 ——
       PC 没有「自己的昵称」，设置页不该有「本机昵称」输入框。
       开关关闭（nickname.enabled=false）时整块不显示，旧输入框原样保留 = 今天的行为。 */
    var legacy = $('legacy-name-row');
    if (legacy) legacy.hidden = nickOn();
    renderNickBlock();
  }

  function saveSettings() {
    var payload = { type: 'web.save_config' };
    var changed = [];

    var url = ($('set-server').value || '').trim();
    if (url && !/^https?:\/\//i.test(url)) { url = 'http://' + url; $('set-server').value = url; }
    if (url && url !== S.server) { payload.server_url = url; changed.push('服务端地址'); }

    var token = ($('set-token').value || '').trim();
    if (token) { payload.enroll_token = token; changed.push('注册口令'); }

    var name = ($('set-reply-name').value || '').trim();
    if (name && name !== S.myName) { payload.reply_name = name; changed.push('回复昵称'); }

    var auto = !!$('set-autostart').checked;
    if (auto !== !!S.autostart) { payload.autostart = auto; changed.push('开机自启'); }

    var theme = $('set-theme').value;
    if (theme !== S.theme) { payload.theme_mode = theme; changed.push('主题'); }

    if (!changed.length) { setHint('settings-hint', '没有改动，不用保存', ''); return; }

    /* 本地先按新值生效，界面立刻跟手；宿主回执来了再以它为准 */
    if (name) { S.myName = name; lsSet(LS_SENDER, name); buildSenderSelects(); renderClient(); }
    setThemeChoice(theme);
    S.autostart = auto;
    if (payload.server_url) S.server = payload.server_url;

    setHint('settings-hint', '正在保存：' + changed.join('、') + '…', '');
    post(payload);

    if (S.saveTimer) clearTimeout(S.saveTimer);
    S.saveTimer = setTimeout(function () {
      S.saveTimer = null;
      setHint('settings-hint', '没收到宿主的回执，稍后再试一次', 'error');
    }, SAVE_TIMEOUT);

    fillSettings();
  }

  function onConfigSaved(d) {
    if (S.saveTimer) { clearTimeout(S.saveTimer); S.saveTimer = null; }
    var ok = !d || d.ok !== false;
    var detail = (d && d.detail) || (ok ? '已保存' : '保存失败');
    setHint('settings-hint', detail, ok ? 'ok' : 'error');
    var t = $('set-token');
    if (t && ok) t.value = '';
    if (ok) fillSettings();
  }

  function onRuntime(d) {
    if (!d || typeof d !== 'object') return;
    if (d.version) { S.version = String(d.version); setText('client-ver', S.version); setText('popup-ver', S.version); }
    if (d.runtime) S.runtime = String(d.runtime);
    if (d.platform) S.platform = String(d.platform);
    if (d.device_id) S.deviceId = String(d.device_id);
    if (d.device_name) S.deviceName = String(d.device_name);
    if (typeof d.server === 'string') S.server = d.server;
    if (typeof d.enroll_configured === 'boolean') S.enroll = d.enroll_configured;
    if (typeof d.autostart === 'boolean') S.autostart = d.autostart;
    if (d.theme_mode) { S.theme = d.theme_mode; applyTheme(d.theme_mode); }
    if (d.log_path) S.logPath = String(d.log_path);
    if (d.config_path) S.configPath = String(d.config_path);
    applyDevice();
    if (S.view === 'settings') fillSettings();
  }

  function applyDevice() {
    var name = S.deviceName || S.deviceId || '';
    ['client-device', 'popup-device'].forEach(function (id) {
      var el = $(id);
      if (el) el.textContent = name ? '· ' + name : '';
    });
  }

  /* ── 设置视图：共享昵称（docs/NICKNAME-SYSTEM-PLAN.md §7 Phase 4 / §5.2 / §3.3）────
     ★ 这里**没有「修改本机昵称」输入框** —— PC 不拥有昵称，只有「我当前用谁的名义」。
       「选用」= 纯本地动作（离线也能做）；新建 / 改名 / 删除 / 重新分配颜色都是
       **全局操作**，必须在线，离线的四个入口一律置灰并明示原因（不排队、不补发）。 */

  /** 昵称块的一句话提示。
      ★ 同时写到「**当前形态可见**」的那一行：设置页那行在弹窗 / 客户端形态下是隐藏的，
      用户点了改名 / 换色后（或弹窗被新消息顶上来之后）失败文案必须仍然看得见。 */
  function nickHint(text, kind) {
    setHint('settings-nick-hint', text, kind);
    if (S.view !== 'settings') {
      var bar = BARS[S.view] || BARS.client;
      setHint(bar.hint, text, kind);
    }
  }

  /** 管理操作此刻能不能做（服务端连上了吗）。false → 四个入口置灰 + 明示原因。 */
  function nickCanManage() { return !!(S.nick && S.nick.canManage); }

  function nickOfflineReason() {
    return (S.nick && S.nick.offlineReason) || '当前未连接服务器，昵称管理不可用';
  }

  /**
   * 昵称本地校验：**1–32 个字符**（按 Unicode 码点算，emoji 算 1 —— 与 Python 的
   * `len()` / C# 的 `EnumerateRunes()` 同口径）、去空白后不许为空、不许带控制字符。
   * 返回错误文案；null = 通过。服务端仍会再校验一次（它才是权威，§5.5）。
   */
  function validateName(raw) {
    var name = String(raw == null ? '' : raw).trim();
    if (!name) return '昵称不能为空';
    var count = Array.from(name).length;
    if (count > NAME_MAX_LEN) return '昵称最多 ' + NAME_MAX_LEN + ' 个字符（当前 ' + count + ' 个）';
    for (var i = 0; i < name.length; i++) {
      var c = name.charCodeAt(i);
      if (c < 32 || c === 127) return '昵称里不能有控制字符';
    }
    return null;
  }

  function noteEl(text) {
    var p = document.createElement('p');
    p.className = 'field-support';
    p.textContent = text;
    return p;
  }

  function mkBtn(label, id, title) {
    var b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn--secondary btn--sm';
    if (id) b.id = id;
    b.textContent = label;
    if (title) b.title = title;
    return b;
  }

  /* ── 管理操作的 pending 生命周期（新建 / 改名 / 换色 / 删除）───────────────
     宿主回的 <c>host.nickname_result</c> 只说明「请求被接受了吗」，**不是「改成功了吗」**
     （App.OnNicknameRequested 的注释：真正的成功判据是服务端随后推来的整份状态
     `host.nickname`，失败则是 `host.nickname_error`）。

     ⚠ 老实现的两个病：
       ① 收到 accepted 回执就把 8s 看门狗 `clearTimeout` 了 —— 而那时**还没有**任何
          权威结果，于是提示永远停在「等待服务端应答…」（真机 bug：改名后既不成功也不失败）。
       ② 成功一路上**没有任何文案落地**（只有「新建」靠名字反查报了成功）。
     现在：看门狗**保留到有权威结果为止**；权威整份状态一到就用 resolveNickPending()
     判定成功并落文案；错误帧一到就落失败文案（撞名尤其要看得见）。 */
  var NICK_OP_TIMEOUT = 8000;

  /** 把「这次操作等的是什么」记下来（判定成功要用：改的名字 / 改前的颜色 / 删的 id） */
  function beginNickOp(kind, msg) {
    var id = (msg.nickname_id === null || msg.nickname_id === undefined) ? null : Number(msg.nickname_id);
    var row = (id === null) ? null : nickById(id);
    var wanted = msg.display_name ? String(msg.display_name) : '';

    S.nickPending = {
      kind: kind,
      id: id,
      want: wanted,                                  // rename / create 期望出现的名字
      label: (row && row.display_name) ? String(row.display_name) : (wanted || '该昵称'),
      color: row ? row.color : null,                 // 换色：颜色**变了**才算成功
    };
    S.nickBusy = true;

    if (S.nickTimer) clearTimeout(S.nickTimer);
    S.nickTimer = setTimeout(function () {
      S.nickTimer = null;
      S.nickBusy = false;
      var p = S.nickPending;
      S.nickPending = null;
      if (!p) return;
      /* 看门狗兜底：**绝不永远挂着** —— 如实说没等到结果，并给出下一步 */
      nickHint('还没收到服务端的应答（' + nickOpPastLabel(p) + '）：可能没生效。点「刷新」可以看最新状态。', 'error');
    }, NICK_OP_TIMEOUT);
  }

  /** 一次操作的过去式说法（兜底文案用） */
  function nickOpPastLabel(p) {
    if (p.kind === 'rename') return '改为「' + p.want + '」';
    if (p.kind === 'create') return '新建「' + p.want + '」';
    if (p.kind === 'reassign_color') return '给「' + p.label + '」换颜色';
    if (p.kind === 'delete') return '删除「' + p.label + '」';
    return '这次操作';
  }

  /** 操作有了结论（成功 / 失败 / 超时）→ 清 pending + 停看门狗 */
  function endNickOp() {
    if (S.nickTimer) { clearTimeout(S.nickTimer); S.nickTimer = null; }
    S.nickBusy = false;
    S.nickPending = null;
  }

  /** 整份权威状态到了 → 判定挂着的操作成不成功；成功就落文案 + 收尾。
      判不准（服务端还没处理完 / 名字撞了但还没回错误帧）就返回 false，交给看门狗兜底。 */
  function resolveNickPending() {
    var p = S.nickPending;
    if (!p) return false;

    var row = (p.id === null) ? null : nickById(p.id);
    var text = '';

    if (p.kind === 'rename') {
      if (row && p.want && String(row.display_name || '') === p.want)
        text = '已改名为「' + p.want + '」（全局生效，其他机器上的名字也变了）';
    } else if (p.kind === 'create') {
      if (p.want && nickByName(p.want))
        text = '已创建「' + p.want + '」（已入库，所有人可见）';
    } else if (p.kind === 'reassign_color') {
      if (row && p.color && row.color && row.color !== p.color)
        text = '已换一个新颜色（全局生效）';
    } else if (p.kind === 'delete') {
      if (p.id !== null && !row) text = '已删除「' + p.label + '」（历史消息原样保留）';
    } else if (p.kind === 'refresh') {
      /* 刷新没有「增量」可判：整份状态到了就是刷到了 */
      text = '昵称列表已刷新（' + S.nick.list.length + ' 条活跃昵称）';
    }

    if (!text) return false;
    endNickOp();
    nickHint(text, 'ok');
    return true;
  }

  /** 发一次昵称操作。回的是「请求被接受了吗」，**真正的成功判据是服务端随后回的帧**。 */
  function opNick(kind, payload) {
    if (!nickOn()) return;
    var msg = { type: 'web.nickname_' + kind };
    Object.keys(payload || {}).forEach(function (k) { msg[k] = payload[k]; });

    beginNickOp(kind, msg);      // 看门狗 + 判定依据（**不再**被 accepted 回执清掉）
    post(msg);
  }

  function createNick() {
    if (!nickOn()) return;
    var input = $('nick-new-name');
    if (!input) return;
    var name = String(input.value || '').trim();

    var problem = validateName(name);            // 本地先拦（32 字符上限四端统一）
    if (problem) { nickHint(problem, 'error'); input.focus(); return; }
    if (!nickCanManage()) { nickHint(nickOfflineReason(), 'error'); return; }

    S.nickPendingName = name;
    opNick('create', { display_name: name });
  }

  function renameNick(n, input) {
    if (!nickOn() || !n || !input) return;
    var old = String(n.display_name || '');
    var name = String(input.value || '').trim();
    if (name === old) return;                    // 没改

    var problem = validateName(name);
    if (problem) { nickHint(problem, 'error'); input.value = old; return; }
    if (!nickCanManage()) { nickHint(nickOfflineReason(), 'error'); input.value = old; return; }

    opNick('rename', { nickname_id: Number(n.nickname_id), display_name: name });
  }

  /** 昵称块的一行：色点（逻辑色 ID 经 §4.4 渲染）+ 改名输入 + 选用 / 换色 / 删除 */
  function nickRowEl(n) {
    var can = nickCanManage();
    var id = Number(n.nickname_id);

    var row = document.createElement('div');
    row.className = 'nick-row' + (currentNickId() === id ? ' nick-row--using' : '');

    var sw = document.createElement('span');
    sw.className = 'nick-row__swatch';
    sw.style.background = FMNick.dot(n.color);   // 显示色按当前主题现算
    sw.title = '颜色：' + String(n.color || '');  // 逻辑色 ID（明确不是 HEX）
    sw.setAttribute('aria-hidden', 'true');

    var input = document.createElement('input');
    input.className = 'input nick-row__input';
    input.type = 'text';
    input.value = String(n.display_name || '');
    input.maxLength = NAME_MAX_LEN;
    input.disabled = !can;                        // 改名是全局操作 → 离线置灰
    input.setAttribute('aria-label', '改「' + String(n.display_name || '') + '」的名字（会影响所有使用者）');
    input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); input.blur(); }
    });
    input.addEventListener('change', function () { renameNick(n, input); });

    var actions = document.createElement('div');
    actions.className = 'nick-row__actions';

    /* 「选用」= 选择昵称，**纯本地动作 —— 唯一能离线的**（§5.2） */
    var use = mkBtn('选用', 'nick-use-' + id,
                    '以「' + String(n.display_name || '') + '」的名义发言（只影响这台电脑）');
    use.disabled = (currentNickId() === id);
    use.onclick = function () { post({ type: 'web.nickname_select', nickname_id: id }); };

    var recolor = mkBtn('换色', 'nick-color-' + id, '重新分配颜色（全局：所有在用的机器一起变）');
    recolor.disabled = !can;
    recolor.onclick = function () { opNick('reassign_color', { nickname_id: id }); };

    var del = mkBtn('删除', 'nick-del-' + id, '删除这个共享昵称（全局；历史消息原样不变）');
    del.disabled = !can;
    del.onclick = function () { opNick('delete', { nickname_id: id }); };

    actions.append(use, recolor, del);
    row.append(sw, input, actions);
    return row;
  }

  function renderNickBlock() {
    var card = $('nick-card');
    if (!card) return;

    var on = nickOn();
    card.hidden = !on;
    if (!on) return;                              // 开关关闭 → 整块不显示，页面走今天的路径

    var cur = currentNick();
    var isTemp = (currentNickId() === null);

    var dot = $('nick-cur-dot');
    if (dot) dot.style.background = FMNick.dot(cur ? cur.color : NICK_LOCAL_TEMP_ID);
    setText('nick-cur-name', currentNickName());

    var state = $('nick-cur-state');
    if (state) {
      state.textContent = isTemp ? '本地临时（灰色）' : '共享昵称（全局）';
      state.className = 'chip' + (isTemp ? ' is-warn' : ' is-ok');
    }

    // 离线：四个管理入口置灰 + 明示原因（只有「选用」还能用）
    var can = nickCanManage();
    var reason = $('nick-offline-reason');
    if (reason) {
      reason.hidden = can;
      reason.textContent = can ? '' : nickOfflineReason();
    }
    var create = $('nick-create-btn');
    if (create) create.disabled = !can;
    var refresh = $('nick-refresh-btn');
    if (refresh) refresh.disabled = !can;         // 刷新也要在线（拉整表）
    var newInput = $('nick-new-name');
    if (newInput) newInput.disabled = !can;

    var list = $('nick-list');
    if (list) {
      list.textContent = '';
      if (!S.nick.list.length) {
        list.appendChild(noteEl('NAS 上还没有昵称。可以先新建一个；没选之前用灰色本地临时昵称发消息。'));
      } else {
        S.nick.list.forEach(function (n) { list.appendChild(nickRowEl(n)); });
      }
    }

    // 额度：活跃上限 = 16（颜色不重复的必然结果，§3.1）
    var quota = $('nick-quota');
    if (quota) {
      var used = S.nick.list.length;
      var max = S.nick.maxActive || 16;
      quota.textContent = (used >= max)
        ? '已达到共享昵称上限（' + used + '/' + max + '）：请删除不再使用的昵称后再添加。'
        : (used >= max - 2
            ? '共享昵称颜色即将用尽（' + used + '/' + max + '）'
            : '共享昵称数量：' + used + '/' + max);
      quota.className = 'field-support' + (used >= max - 2 ? ' is-warn' : '');
    }
  }

  function onNickname(d) {
    if (!d || typeof d !== 'object') return;
    S.nick = {
      available: d.available === true,
      canManage: d.can_manage === true,
      online: d.online === true,
      offlineReason: d.offline_reason || '当前未连接服务器，昵称管理不可用',
      defaultName: String(d.default_name || ''),
      maxActive: parseInt(d.max_active, 10) || 16,
      poolVersion: d.pool_version,
      current: (d.current && typeof d.current === 'object')
        ? d.current : { nickname_id: null, display_name: '', color: NICK_LOCAL_TEMP_ID, is_local_temp: true },
      list: Array.isArray(d.nicknames) ? d.nicknames.filter(function (n) {
        return !!n && typeof n === 'object' && n.nickname_id !== undefined && n.nickname_id !== null;
      }) : [],
      notice: String(d.notice || ''),
    };
    // ★ v0.19：整份昵称状态里也带色表（服务端整表帧带来的）——先换上再画，避免用旧色画一遍
    nickSetTable(d.color_table, d.pool_version);

    // 刚新建成功（整表里出现了那个名字）→ 清输入框（提示文案由 resolveNickPending 落）
    if (S.nickPendingName && nickByName(S.nickPendingName)) {
      var input = $('nick-new-name');
      if (input) input.value = '';
      S.nickPendingName = null;
    }

    var conflict = $('nick-conflict');
    if (conflict) conflict.hidden = true;

    buildSenderSelects();                         // 下拉跟着换（含「我的名义」）
    if (S.view === 'settings') fillSettings();
    /* ★ 权威整份状态到了 → 把挂着的管理操作判定成成功并落文案（改名尤其需要：
       否则提示永远停在宿主那句「等待服务端应答…」）。 */
    resolveNickPending();
    if (S.nick.notice) nickHint(S.nick.notice, 'warn');   // 「你用的昵称已被删除，已切回…」
  }

  function onNicknameResult(d) {
    if (!d || typeof d !== 'object') return;

    if (d.accepted === true) {
      /* ★ 只是「请求被**宿主**接受了」，不是「改成功了」：
         看门狗**不能**在这里清掉（老实现在这里 clearTimeout → 提示永远挂在
         「等待服务端应答…」，真机 bug）。真正的收尾在 resolveNickPending()
         （权威整份状态到了）或 onNicknameError（服务端拒绝）或看门狗超时。 */
      nickHint(String(d.detail || '') || '已发出，等待服务端应答…', '');
      return;
    }

    // 被拒（离线 / 本地校验不过）：如实说原因，绝不假装成功（§5.2）
    endNickOp();
    nickHint(String(d.detail || '') || (d.offline ? nickOfflineReason() : '操作没成功'), 'error');
    if (S.view === 'settings') renderNickBlock();
  }

  function onNicknameError(d) {
    if (!d || typeof d !== 'object') return;
    endNickOp();                        // ★ 失败也要**收尾**（看门狗停、pending 清）

    var code = String(d.code || '');
    var message = String(d.message || '服务端拒绝了这次操作');
    var existing = (d.existing_nickname_id === null || d.existing_nickname_id === undefined)
      ? null : Number(d.existing_nickname_id);

    // 创建撞名 → 引导「已存在，直接选用它？」（§3.3 方式 A：报错 + 客户端引导选用）
    if (code === 'NICKNAME_ALREADY_EXISTS' && existing) {
      var box = $('nick-conflict');
      var text = $('nick-conflict-text');
      var btn = $('nick-conflict-use');
      if (box && text && btn) {
        var hit = nickById(existing);
        var label = hit ? String(hit.display_name || '') : '';
        text.textContent = (label ? '「' + label + '」已存在。' : '') + (message || '这个昵称已存在。');
        btn.textContent = label ? '直接选用「' + label + '」' : '直接选用它';
        btn.onclick = function () {
          box.hidden = true;
          post({ type: 'web.nickname_select', nickname_id: existing });
        };
        box.hidden = false;
        nickHint('重名会被拒绝：可以直接选用已有的那条。', 'warn');
        refreshNickRowsOnError();
        return;
      }
    }
    /* 服务端的拒绝理由必须**看得见**（撞名 409 NAME_TAKEN / 池满 / 找不到…）：
       文案走 nickHint（设置页 + 当前形态可见的那一行）。 */
    nickHint(code ? (message + '（' + code + '）') : message, 'error');
    refreshNickRowsOnError();
  }

  /** 失败后把列表刷回服务端的真实名字（改名失败时输入框里还留着用户敲的新名字）。
      ★ 不看当前形态：设置页那一块在弹窗 / 客户端形态下是**隐藏**的，但它仍在 DOM 里，
      留着「用户敲过的新名字」会让切回设置页时先看到一眼假的成功态。 */
  function refreshNickRowsOnError() {
    renderNickBlock();
  }

  /* ── 宿主 → 页面 ─────────────────────────────────────────── */
  var HANDLERS = {
    'host.hello': onHello,
    'host.mode': function (d) { setView(d.mode); },
    'host.message': function (d) { markRecv(); onMessage(d.message || d); },
    'host.history': onHistory,
    'host.reply_ack': onReplyAck,
    'host.connection': onConnection,
    'host.session': function (d) { S.session = d; },        // PC 端没有设备页，只记着
    'host.screenshot': function (d) { console.debug('[pc] 截图响应（本机界面没有入口）', d && d.ok); },
    'host.action_result': function (d) { console.debug('[pc] 动作结果（本机界面没有入口）', d && d.action); },
    'host.config_saved': onConfigSaved,
    'host.runtime': onRuntime,
    /* 共享昵称（§7 Phase 4）：整份状态 / 操作回执 / 服务端错误 —— 都是免刷新重画的入口 */
    'host.nickname': onNickname,
    'host.nickname_result': onNicknameResult,
    'host.nickname_error': onNicknameError,
  };

  function onHello(d) {
    S.hello = d;
    if (d.device_id) S.deviceId = String(d.device_id);
    if (d.device_name) S.deviceName = String(d.device_name);
    if (Array.isArray(d.reply_names)) {
      S.names = d.reply_names.filter(function (n) { return typeof n === 'string' && n.trim(); });
    }
    S.myName = String(d.reply_name || '').trim() || lsGet(LS_SENDER) || S.names[0] || S.deviceName || '';
    S.server = d.server || '';
    S.version = d.version || '—';
    S.platform = d.platform || '';
    if (typeof d.enroll_configured === 'boolean') S.enroll = d.enroll_configured;
    if (typeof d.autostart === 'boolean') S.autostart = d.autostart;
    nickSetTable(d.color_table, d.pool_version);   // ★ v0.19：宿主 hello 里带的权威色表
    setThemeChoice(d.theme_mode || 'system');

    applyDevice();
    buildSenderSelects();
    setText('client-ver', S.version);
    setText('popup-ver', S.version);
    setText('settings-ver', S.version);
    renderClient();
    renderPopup();
    setView(normMode(d.mode));          // 起始视图只由 hello.mode 决定（不用 URL 参数）
    console.info('[pc] 宿主 ' + S.version + ' · 视图 ' + normMode(d.mode) + ' · 服务端 ' + (S.server || '?'));
  }

  /* ── 界面事件 ────────────────────────────────────────────── */
  function openSettings() {
    /* 形态归宿主定：只发请求，切不切由它回 host.mode。
       没有宿主时（浏览器预览）本地切一下，省得点了没反应。 */
    post({ type: 'web.open_settings' });
    if (!HAS_BRIDGE) setView('settings');
  }

  function closeSettings() {
    setView(S.prevView || 'client');
    /* 协议里没有独立的「关设置」帧：补一个 open:false 让宿主的形态跟着回去
       （宿主不认识这个字段就只当本地切换，界面不会卡在设置页）。 */
    post({ type: 'web.open_settings', open: false });
  }

  function closePopup() {
    post({ type: 'web.close' });
    if (S.timer) { clearTimeout(S.timer); S.timer = null; }
    S.pending = null;
    setHintAll('');
  }

  function bindUI() {
    var b = $('btn-open-settings'); if (b) b.onclick = openSettings;
    var back = $('settings-back'); if (back) back.onclick = closeSettings;
    var ok = $('popup-ok'); if (ok) ok.onclick = closePopup;
    var save = $('settings-save'); if (save) save.onclick = saveSettings;
    var quit = $('btn-quit'); if (quit) quit.onclick = function () { post({ type: 'web.quit' }); };

    /* 共享昵称块（§7 Phase 4）：新建 / 刷新 / 回车提交 */
    var ncreate = $('nick-create-btn'); if (ncreate) ncreate.onclick = createNick;
    var nrefresh = $('nick-refresh-btn');
    if (nrefresh) nrefresh.onclick = function () { opNick('refresh', {}); };
    var ninput = $('nick-new-name');
    if (ninput) ninput.addEventListener('keydown', function (e) {
      if (e.key === 'Enter') { e.preventDefault(); createNick(); }
    });

    Object.keys(BARS).forEach(function (k) {
      var input = $(BARS[k].text);
      if (input) {
        input.addEventListener('keydown', function (e) {
          if (e.key === 'Enter') { e.preventDefault(); sendReply(k); }
        });
      }
      var send = $(BARS[k].send);
      if (send) send.onclick = function () { sendReply(k); };
      var sel = $(BARS[k].sender);
      if (sel) sel.addEventListener('change', onSenderChange);
    });

    ['set-server', 'set-token', 'set-reply-name'].forEach(function (id) {
      var el = $(id);
      if (el) el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); saveSettings(); }
      });
    });

    // Esc = 知道了（只在强提醒形态、且不在输入里时抢；其他形态不抢 Esc）
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || S.view !== 'popup') return;
      var t = document.activeElement;
      if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA')) return;
      closePopup();
    });
  }

  /* ── 启动 ────────────────────────────────────────────────── */
  function start() {
    bindUI();
    applyTheme('system');
    renderClient();
    renderPopup();
    setView('client');                 // 宿主应答前的占位视图（hello 一到就按 mode 切）
    if (!HAS_BRIDGE) {
      var a = $('client-conn-text'); if (a) a.textContent = '没有宿主（浏览器预览）';
      var bb = $('popup-conn-text'); if (bb) bb.textContent = '没有宿主（浏览器预览）';
      console.info('[pc] 不在 WebView2 宿主里：只渲染界面，动作不会真正发出去。');
    }
    post({ type: 'web.ready' });       // 必须发：宿主收到才回 hello/history
    setTimeout(function () {
      if (!S.hello) console.info('[pc] ' + HELLO_TIMEOUT + 'ms 没等到 host.hello，先用默认视图。');
    }, HELLO_TIMEOUT);
  }

  window.FM_PC = {
    state: S,
    sent: SENT,
    setView: setView,
    sendReply: sendReply,
    saveSettings: saveSettings,
    fillSettings: fillSettings,
    /* 共享昵称的自检入口（§4.4 映射表 / 消息上色 / 本地校验都在这儿对齐） */
    nick: FMNick,
    nickOn: nickOn,
    nickColorIdForMessage: nickColorIdForMessage,
    validateName: validateName,
    createNick: createNick,
    renderNickBlock: renderNickBlock,
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
