/* 昵称显示色：逻辑色 ID → 显示色（主题变体 / 头像字色自适应）
 * ═══════════════════════════════════════════════════════════════════
 * 规格：docs/NICKNAME-SYSTEM-PLAN.md §4.4（客户端渲染契约）+ 最终说明 §8。
 *
 * 为什么单独一个文件：颜色**不再由客户端算**（原来 app.js 的 nickColor() 按名字哈希），
 * 而是 NAS 下发**逻辑色 ID**（`color_01`…`color_16` / 灰 `gray`），
 * 显示色（含浅/深主题变体）在这里按映射表算出来 —— 这是**渲染职责**。
 * 这份映射表 PC / Web 两端必须**完全一致**（改这里 = 同时改 PC 端那份，
 * 否则会出现「网页端一个色、PC 弹窗另一个色」，§11 风险 8）。
 *
 * 四条规则（与 §4.2 一一对应）：
 *   1. dot       浅色 mix(base, #000, 35%) / 深色 mix(base, #FFF, 30%)   —— 两个主题都 ≥3.0
 *   2. avatarBg / avatarFg  优先原色底：对比(白字,底) ≥4.5 → 白字；
 *                          否则 对比(0.7 黑字,底) ≥4.5 → 0.7 黑字
 *   3. 两头都不达标（只有 color_03/05/06/11/12 + gray 命中）→ 底 mix(base,#000,18%) + 白字
 *   4. 文本 / 下拉项**不用色值当字色**（调用方用「色块 + 常规字色」，本文件不管）
 *
 * ★ 安全（R2）：所有返回值都来自本文件里的常量表；
 *   **未知逻辑色 ID 一律走灰兜底**，绝不把收到的字符串塞进 style（防注入 / 脏数据）。
 *   本文件里**只有逻辑色 ID 与基础色值**，没有主题变体的预存表 —— 变体是按主题现算的。
 * ═══════════════════════════════════════════════════════════════════ */
(function (global) {
  'use strict';

  /* ── 逻辑色 ID → 基础色值（与 server/nicknames.py 的 LOGICAL_COLORS 逐字对应）──
     顺序无意义（分配优先级在服务端），这里只做「ID → 色值」查找。 */
  var BASE = {
    color_01: '#EF5350', color_02: '#FF7043', color_03: '#FFA726', color_04: '#FFCA28',
    color_05: '#66BB6A', color_06: '#9CCC65', color_07: '#26A69A', color_08: '#26C6DA',
    color_09: '#42A5F5', color_10: '#5C6BC0', color_11: '#7E57C2', color_12: '#AB47BC',
    color_13: '#EC407A', color_14: '#8D6E63', color_15: '#78909C', color_16: '#B71C1C',
    color_17: '#D84315', color_18: '#E65100', color_19: '#F57F17', color_20: '#2E7D32',
    color_21: '#558B2F', color_22: '#00695C', color_23: '#00838F', color_24: '#1565C0',
    color_25: '#283593', color_26: '#4527A0', color_27: '#6A1B9A', color_28: '#AD1457',
    color_29: '#4E342E', color_30: '#37474F', color_31: '#004D40',
    gray: '#8A8A8A',           // 本地临时昵称（不在池里，第 17 个 ID）
  };

  /** 本地临时昵称的逻辑色 ID（服务端常量 LOCAL_TEMP_COLOR_ID） */
  var LOCAL_TEMP_ID = 'gray';
  /** 未知 / 脏 ID 的兜底：一律按灰渲染（不抛异常、不塞原串） */
  var FALLBACK_ID = 'gray';

  /* ── 色值工具（逐通道取整，与 §4.4 表的算法一致）────────────── */
  function rgb(h) {
    return [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
  }
  function toHex(c) {
    var s = '#';
    for (var i = 0; i < 3; i++) {
      var v = Math.max(0, Math.min(255, Math.round(c[i])));
      s += (v < 16 ? '0' : '') + v.toString(16).toUpperCase();
    }
    return s;
  }
  /** mix(a, b, p)：p=0 → a，p=1 → b（线性混合，逐通道取整） */
  function mix(a, b, p) {
    var A = rgb(a), B = rgb(b), out = [0, 0, 0];
    for (var i = 0; i < 3; i++) out[i] = A[i] * (1 - p) + B[i] * p;
    return toHex(out);
  }
  /** WCAG 2.1 相对亮度 */
  function luminance(h) {
    var c = rgb(h).map(function (v) {
      v = v / 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
  }
  /** WCAG 2.1 对比度（1 ≤ r ≤ 21） */
  function contrast(a, b) {
    var l1 = luminance(a), l2 = luminance(b);
    if (l1 < l2) { var t = l1; l1 = l2; l2 = t; }
    return (l1 + 0.05) / (l2 + 0.05);
  }

  /** 这个逻辑色 ID 认不认识（'gray' 算认识的）。调用方据此决定是否走哈希兜底 */
  /* ★ v0.19：色表是**数据**（服务端表 `nickname_palette` 是权威），上面那份 BASE 只做
     「首屏兜底」：拿到服务端表前 / 服务端没下发时按它渲染，认不出的 ID 依旧走 FALLBACK（灰）。
     setTable(rows, version) 由 app.js 在启动时与收到 color_table_changed 后调用。 */
  var SERVER_TABLE = null;                 // {color_17:'#0FA3B1', …}；null = 还没拿到
  var TABLE_VERSION = 1;                   // 服务端 color_pool_version

  function setTable(rows, version) {
    if (!rows || typeof rows.length !== 'number') return false;
    var next = {};
    for (var i = 0; i < rows.length; i++) {
      var id = rows[i] && rows[i].color_id, hex = rows[i] && rows[i].hex;
      if (typeof id === 'string' && typeof hex === 'string'
          && /^#[0-9A-Fa-f]{6}$/.test(hex)) {
        next[id] = hex.toUpperCase();
      }
    }
    if (!Object.keys(next).length) return false;   // 空表 / 全不合法 → 保留旧表，绝不把表清空
    SERVER_TABLE = next;
    if (typeof version === 'number') TABLE_VERSION = version;
    return true;
  }

  function tableVersion() { return TABLE_VERSION; }
  function hasServerTable() { return !!SERVER_TABLE; }
  function tableIds() { return Object.keys(SERVER_TABLE || BASE); }

  function isKnown(id) {
    if (SERVER_TABLE && Object.prototype.hasOwnProperty.call(SERVER_TABLE, id)) return true;
    return typeof id === 'string' && Object.prototype.hasOwnProperty.call(BASE, id);
  }

  /** 逻辑色 ID → 基础色值（**服务端表优先**，未知 → 灰的基础色值） */
  function base(id) {
    if (SERVER_TABLE && Object.prototype.hasOwnProperty.call(SERVER_TABLE, id)) return SERVER_TABLE[id];
    return isKnown(id) ? BASE[id] : BASE[FALLBACK_ID];
  }

  /** 当前主题：'light' | 'dark'（读 html[data-mode]，由 app.js 的 applyMode 写） */
  function theme() {
    try {
      return document.documentElement.getAttribute('data-mode') === 'dark' ? 'dark' : 'light';
    } catch (_) {
      return 'light';
    }
  }

  /** 圆点（8–10px）：浅色 35% 黑 / 深色 30% 白 —— 两个主题都 ≥3.0（§4.4 规则 1） */
  function dot(id, th) {
    var b = base(id);
    return (th || theme()) === 'dark' ? mix(b, '#FFFFFF', 0.30) : mix(b, '#000000', 0.35);
  }

  /** 头像底：优先原色；两头都不达标才取 18% 黑变体（§4.4 规则 2 / 3） */
  function avatarBg(id) {
    var b = base(id);
    if (contrast('#FFFFFF', b) >= 4.5) return b;                   // 白字达标 → 原色底
    if (contrast(mix(b, '#000000', 0.70), b) >= 4.5) return b;     // 0.7 黑字达标 → 原色底
    return mix(b, '#000000', 0.18);                                // 两头都不达标 → 加深
  }

  /** 头像字色：白字 / 0.7 黑字（与 avatarBg 同一个判据，保证两者配套） */
  function avatarFg(id) {
    var b = base(id);
    if (contrast('#FFFFFF', b) >= 4.5) return '#FFFFFF';
    if (contrast(mix(b, '#000000', 0.70), b) >= 4.5) return 'rgba(0, 0, 0, 0.7)';
    return '#FFFFFF';                                              // 用了 18% 黑底 → 白字
  }

  /** 统一入口：display(kind, id, theme?) —— kind ∈ {dot, avatarBg, avatarFg} */
  function display(kind, id, th) {
    if (kind === 'dot') return dot(id, th);
    if (kind === 'avatarBg') return avatarBg(id);
    if (kind === 'avatarFg') return avatarFg(id);
    return dot(id, th);
  }

  global.FMNickColor = {
    /** 与 server/nicknames.py 的 COLOR_POOL_VERSION 对应：+1 表示映射表语义变了 */
    VERSION: 1,
    LOCAL_TEMP_ID: LOCAL_TEMP_ID,
    isKnown: isKnown,
    base: base,
    theme: theme,
    dot: dot,
    avatarBg: avatarBg,
    avatarFg: avatarFg,
    display: display,
    /* ★ v0.19：色表以服务端为准 */
    setTable: setTable,
    tableVersion: tableVersion,
    hasServerTable: hasServerTable,
    tableIds: tableIds,
    /* 下面几个只给测试 / 排查用（断言 §4.4 表用得上） */
    mix: mix,
    contrast: contrast,
    ids: Object.keys(BASE),
    baseColors: BASE,
  };
})(window);
