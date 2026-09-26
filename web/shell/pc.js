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

   桥协议（只读，不新增帧）：
     页面→宿主  web.ready / web.reply / web.ack / web.close / web.quit
                web.request_screenshot / web.request_action
                web.save_config / web.open_settings
     宿主→页面  host.hello / host.mode / host.message / host.history / host.reply_ack
                host.connection / host.session / host.screenshot / host.action_result
                host.config_saved / host.runtime
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
    var items = senderItems();
    ['client', 'popup'].forEach(function (k) {
      var sel = $(BARS[k].sender);
      if (!sel) return;
      var cur = S.myName || items[0];
      sel.textContent = '';
      items.forEach(function (n) {
        var o = document.createElement('option');
        o.value = n;
        o.textContent = n;
        sel.appendChild(o);
      });
      sel.value = items.indexOf(cur) >= 0 ? cur : items[0];
    });
    /* 回复栏里选中的昵称 = 我的本地昵称（群聊模型：身份只看昵称）。
       选完立刻同步，自己发的消息才会靠右。 */
    var main = $(BARS.client.sender);
    if (main && main.value) S.myName = main.value;
  }

  function onSenderChange() {
    var v = $(BARS.client.sender).value || '';
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
      setHintAll('已回复', 'ok');
    } else if (d.status === 'queued') {
      clearSentInput(sent);
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

  /* ── 宿主 → 页面 ─────────────────────────────────────────── */
  var HANDLERS = {
    'host.hello': onHello,
    'host.mode': function (d) { setView(d.mode); },
    'host.message': function (d) { onMessage(d.message || d); },
    'host.history': onHistory,
    'host.reply_ack': onReplyAck,
    'host.connection': onConnection,
    'host.session': function (d) { S.session = d; },        // PC 端没有设备页，只记着
    'host.screenshot': function (d) { console.debug('[pc] 截图响应（本机界面没有入口）', d && d.ok); },
    'host.action_result': function (d) { console.debug('[pc] 动作结果（本机界面没有入口）', d && d.action); },
    'host.config_saved': onConfigSaved,
    'host.runtime': onRuntime,
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
  };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
