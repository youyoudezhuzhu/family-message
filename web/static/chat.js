/* ────────────────────────────────────────────────────────────────
 * 群聊气泡组件 —— **网页端「消息」页 / 首页「最近消息」/ PC 端客户端窗口
 * 共用同一份**。改这里，两端同时变，不会再各写一套然后慢慢跑偏。
 *
 * 群聊模型（docs/GROUP-CHAT-MODEL.md）：
 *   · 一条消息发进一个共享空间，所有设备都看得到
 *   · 身份**只看昵称**：「这条是不是我发的」= sender_name 是否等于我的本地昵称
 *   · 别人发的靠左，自己发的靠右
 *
 * 用法：
 *   FMChat.fill(box, list, { myName: '爸爸', status: STATUS_SENT });
 *   box.appendChild(FMChat.row(msg, { myName: '爸爸', status: STATUS_SENT }));
 *   opts.status 传一个 {cls, icon, label} 就渲染徽标；不传就不渲染。
 * 靠右的判定只认 opts.myName，不认消息里的 device_id —— 群聊里设备不是身份。
 * ──────────────────────────────────────────────────────────────── */
(function (global) {
  'use strict';

  /* 与 PC 端 / Android 端完全一致的色板（见 docs/DESIGN-TOKENS.md §3）。
     app.js 里有一份权威实现 nickColor()；这里是同页加载顺序兜底用的等价副本。 */
  var NICK_COLORS = [
    '#90CAF9', '#CE93D8', '#80CBC4', '#A5D6A7', '#FFE082', '#FFCC80',
    '#EF9A9A', '#F48FB1', '#9FA8DA', '#80DEEA', '#C5E1A5', '#FFAB91',
  ];

  function fallbackNickColor(name) {
    var s = String(name || '').trim();
    if (!s) return NICK_COLORS[0];
    var h = 0;
    for (var i = 0; i < s.length; i++) {
      h = (Math.imul(h, 31) + s.charCodeAt(i)) >>> 0;   // 32 位无符号回绕
    }
    return NICK_COLORS[h % NICK_COLORS.length];
  }

  /** 昵称 → 颜色。app.js 的 nickColor 可用就用它（唯一权威），否则用等价副本 */
  function colorOf(name) {
    try {
      if (typeof global.nickColor === 'function') return global.nickColor(name);
    } catch (_) { /* app.js 还没执行到那儿（TDZ） */ }
    return fallbackNickColor(name);
  }

  function nameOf(msg) {
    return String((msg && msg.sender_name) || '').trim();
  }

  /** 这条是不是「我」发的 —— 群聊模型里身份只看昵称 */
  function isOwn(msg, myName) {
    var b = String(myName == null ? '' : myName).trim();
    if (!b) return false;                      // 还不知道我叫什么 → 一律当别人发的
    return nameOf(msg) === b;
  }

  /** 一条消息 → 气泡行。opts.myName 决定靠左还是靠右 */
  function row(msg, opts) {
    opts = opts || {};
    var own = isOwn(msg, opts.myName);
    var name = nameOf(msg);

    var el = document.createElement('article');
    el.className = 'chat-row' + (own ? ' chat-row--out' : '');
    el.style.setProperty('--chat-nick', colorOf(name));

    var avatar = document.createElement('span');
    avatar.className = 'chat-avatar';
    avatar.setAttribute('aria-hidden', 'true');
    avatar.textContent = (name[0] || '?');

    var main = document.createElement('div');
    main.className = 'chat-main';

    var meta = document.createElement('div');
    meta.className = 'chat-meta';
    var who = document.createElement('span');
    who.className = 'chat-name';
    who.textContent = name;
    var time = document.createElement('span');
    time.className = 'chat-time';
    time.textContent = (msg && msg.created_at) || '';
    meta.append(who, time);

    var bubble = document.createElement('div');
    bubble.className = 'chat-bubble';
    bubble.textContent = (msg && msg.content) || '';

    main.append(meta, bubble);

    /* 投递状态只挂在自己的消息上 —— 群聊里看别人的消息不需要「已发送」。
       服务端对外只有单一 status="sent"（逐设备状态已按模型去掉）。 */
    if (own && opts.status) {
      var tags = document.createElement('div');
      tags.className = 'chat-tags';
      var tag = document.createElement('span');
      tag.className = 'status ' + opts.status.cls;
      if (opts.status.label) {
        if (opts.status.icon && typeof global.icon === 'function') {
          tag.appendChild(global.icon(opts.status.icon, 'icon icon--xs'));
        }
        var txt = document.createElement('span');
        txt.textContent = opts.status.label;
        tag.appendChild(txt);
      }
      tags.appendChild(tag);
      main.appendChild(tags);
    }

    el.append(avatar, main);
    return el;
  }

  function fill(box, list, opts) {
    if (!box) return;
    box.textContent = '';
    (list || []).forEach(function (m) { box.appendChild(row(m, opts)); });
  }

  function append(box, msg, opts) {
    if (box) box.appendChild(row(msg, opts));
  }

  global.FMChat = {
    row: row,
    fill: fill,
    append: append,
    isOwn: isOwn,
    colorOf: colorOf,
    NICK_COLORS: NICK_COLORS,
    className: 'chat-list',
  };
})(window);
