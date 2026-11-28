/* ==========================================================================
   图片批处理工具 · 皮肤核心
   - 皮肤外观全部放在独立 CSS 文件里，这里只负责选择、持久化、同步和菜单。
   - 新增皮肤：复制一个 skins/xxx.css，在 SKINS 里加一条即可，不改页面逻辑。
   ========================================================================== */
(function () {
  'use strict';

  var SKINS = [
    {
      id: 'dark',
      name: '深色',
      desc: '经典黑色 · 低亮度',
      cls: 'skin-dark',
      swatch: 'linear-gradient(145deg,#4d4d50 0%,#242427 52%,#101012 100%)'
    },
    {
      id: 'smoke',
      name: '墨色玻璃',
      desc: '烟熏磨砂 · 冷调极光',
      cls: 'skin-smoke',
      css: 'smoke-glass.css',
      swatch: 'linear-gradient(145deg,#0b1731 0%,#1b3b6f 38%,#4b2f80 68%,#10243f 100%)'
    },
    {
      id: 'light',
      name: '浅色',
      desc: '明亮白 · 干净通透',
      cls: 'skin-light',
      swatch: 'linear-gradient(145deg,#ffffff 0%,#f2f2f5 48%,#d8d8de 100%)'
    },
    {
      id: 'frost',
      name: '柔光白',
      desc: '柔光磨砂 · 白玻璃',
      cls: 'skin-frost',
      css: 'frosted-white.css',
      swatch: 'linear-gradient(145deg,#ffffff 0%,#dfeeff 46%,#f3dcff 72%,#ffe4ee 100%)'
    },
    {
      id: 'aurora',
      name: '液态玻璃',
      desc: '实时透明 · 液态高光',
      cls: 'skin-aurora',
      css: 'aurora-glass.css',
      swatch: 'linear-gradient(145deg,#d9fbff 0%,#8fe7ff 24%,#b7a1ff 52%,#ff9edb 76%,#f7fbff 100%)'
    }
  ];

  var ALIASES = {
    liquid: 'smoke',
    glass: 'smoke',
    'liquid-glass': 'smoke',
    liquidglass: 'smoke',
    'old-liquid': 'smoke',
    'dark-glass': 'smoke',
    ink: 'smoke',
    smoke: 'smoke',
    aurora: 'aurora',
    liquid2: 'aurora',
    'new-liquid': 'aurora',
    'realtime-liquid': 'aurora',
    'clear-glass': 'aurora',
    frostwhite: 'frost',
    'frosted-white': 'frost',
    white: 'light',
    black: 'dark'
  };

  var scriptEl = document.currentScript;
  var base = 'skins/';
  try {
    if (scriptEl && scriptEl.src) base = scriptEl.src.replace(/[^/]+$/, '');
  } catch (e) {}

  var current = '';
  var menu = null;
  var menuAnchor = null;
  var switchTimer = 0;
  var fadeTimer = 0;
  var transitionEl = null;

  function playSkinTransition(from, to) {
    try {
      if (!document.body) return;
      if (transitionEl && transitionEl.parentNode) transitionEl.parentNode.removeChild(transitionEl);
      var ax = window.innerWidth / 2, ay = window.innerHeight / 2;
      try {
        if (menuAnchor && menuAnchor.getBoundingClientRect) {
          var ar = menuAnchor.getBoundingClientRect();
          if (ar.width || ar.height) { ax = ar.left + ar.width / 2; ay = ar.top + ar.height / 2; }
        }
      } catch (e) {}
      var el = document.createElement('div');
      el.className = 'skin-transition';
      el.setAttribute('data-from', from || '');
      el.setAttribute('data-to', to || '');
      el.style.setProperty('--st-x', ax + 'px');
      el.style.setProperty('--st-y', ay + 'px');
      el.innerHTML =
        '<div class="st-veil"></div>' +
        '<div class="st-lens"></div>' +
        '<div class="st-prism p1"></div><div class="st-prism p2"></div>' +
        '<span class="st-blob b1"></span><span class="st-blob b2"></span>' +
        '<span class="st-blob b3"></span><span class="st-blob b4"></span>' +
        '<span class="st-streak s1"></span><span class="st-streak s2"></span>' +
        '<span class="st-ring r1"></span><span class="st-ring r2"></span>' +
        '<span class="st-shard sh1"></span><span class="st-shard sh2"></span>' +
        '<span class="st-shard sh3"></span><span class="st-shard sh4"></span>' +
        '<span class="st-shard sh5"></span><span class="st-shard sh6"></span>' +
        '<span class="st-shard sh7"></span><span class="st-shard sh8"></span>' +
        '<span class="st-core"></span>' +
        '<span class="st-grain"></span>';
      document.body.appendChild(el);
      transitionEl = el;
      try { document.body.classList.add('skin-switching'); } catch (e) {}
      requestAnimationFrame(function () {
        try { el.classList.add('on'); } catch (e) {}
      });
      setTimeout(function () {
        try { el.classList.add('out'); } catch (e) {}
        try { document.body.classList.remove('skin-switching'); } catch (e) {}
        setTimeout(function () {
          try { if (el.parentNode) el.parentNode.removeChild(el); } catch (e) {}
          if (transitionEl === el) transitionEl = null;
        }, 470);
      }, 1180);
    } catch (e) {}
  }

  function normalize(id) {
    id = String(id || '').trim().toLowerCase();
    id = ALIASES[id] || id;
    for (var i = 0; i < SKINS.length; i++) if (SKINS[i].id === id) return id;
    return 'dark';
  }
  function defFor(id) {
    id = normalize(id);
    for (var i = 0; i < SKINS.length; i++) if (SKINS[i].id === id) return SKINS[i];
    return SKINS[0];
  }
  function readInitial() {
    try {
      var m = location.search.match(/skin=([a-z0-9_-]+)/i);
      if (m) return normalize(m[1]);
    } catch (e) {}
    try {
      var s = localStorage.getItem('skin');
      if (s) return normalize(s);
    } catch (e) {}
    return 'dark';
  }
  function webPost(msg) {
    try {
      if (window.chrome && chrome.webview) chrome.webview.postMessage(msg);
      else if (typeof window.post === 'function') window.post(msg);
    } catch (e) {}
  }
  function ensureCss(def) {
    if (!def || !def.css) return;
    try {
      if (document.querySelector('link[data-skin-css="' + def.id + '"]')) return;
      var link = document.createElement('link');
      link.rel = 'stylesheet';
      link.setAttribute('data-skin-css', def.id);
      link.href = def.css.indexOf('://') >= 0 ? def.css : base + def.css + '?v=2288';
      document.head.appendChild(link);
    } catch (e) {}
  }
  function injectCoreCss() {
    try {
      if (document.querySelector('link[data-skin-core]')) return;
      var link = document.createElement('link');
      link.rel = 'stylesheet';
      link.setAttribute('data-skin-core', '1');
      link.href = base + 'skin-core.css?v=2288';
      document.head.appendChild(link);
    } catch (e) {}
  }
  function setInlineSurface(id) {
    try {
      var body = document.body;
      if (!body) return;
      if (id === 'dark') {
        body.style.background = '#2b2b2b';
        body.style.color = '#e0e0e0';
      } else if (id === 'light') {
        body.style.background = '#f5f5f7';
        body.style.color = '#222';
      } else {
        body.style.removeProperty('background');
        body.style.removeProperty('color');
      }
      var sb = document.getElementById('statusBox');
      if (sb) {
        if (id === 'dark') {
          sb.style.background = '#1f1f1f';
          sb.style.borderColor = '#333';
        } else if (id === 'light') {
          sb.style.background = '#fff';
          sb.style.borderColor = '#ccc';
        } else {
          sb.style.removeProperty('background');
          sb.style.removeProperty('border-color');
        }
      }
    } catch (e) {}
  }
  function refreshSelection() {
    if (!menu) return;
    var items = menu.querySelectorAll('.skin-item');
    for (var i = 0; i < items.length; i++) {
      var on = items[i].getAttribute('data-skin') === current;
      items[i].classList.toggle('on', on);
      items[i].setAttribute('aria-checked', on ? 'true' : 'false');
    }
  }
  function apply(id, opts) {
    opts = opts || {};
    id = normalize(id);
    var def = defFor(id);
    var previous = current;
    ensureCss(def);
    current = id;
    try {
      document.documentElement.setAttribute('data-skin', id);
      document.documentElement.classList.toggle('skin-light-html', id === 'light');
      document.documentElement.classList.toggle('skin-frost-html', id === 'frost');
      document.documentElement.classList.toggle('skin-smoke-html', id === 'smoke');
      document.documentElement.classList.toggle('skin-aurora-html', id === 'aurora');
    } catch (e) {}
    try {
      document.body.classList.remove('skin-dark', 'skin-light', 'skin-frost', 'skin-smoke', 'skin-aurora');
      document.body.classList.add(def.cls);
    } catch (e) {}
    setInlineSurface(id);
    refreshSelection();
    if (opts.animate !== false) {
      try {
        document.body.classList.add('skin-fade');
        if (previous && previous !== id) playSkinTransition(previous, id);
        if (id === 'smoke' || id === 'frost' || id === 'aurora') document.body.classList.add('skin-switching', 'skin-entering');
        clearTimeout(switchTimer);
        clearTimeout(fadeTimer);
        switchTimer = setTimeout(function () {
          try { document.body.classList.remove('skin-switching', 'skin-entering'); } catch (e) {}
        }, 760);
        fadeTimer = setTimeout(function () {
          try { document.body.classList.remove('skin-fade'); } catch (e) {}
        }, 560);
      } catch (e) {}
    }
    if (opts.persist !== false) {
      try { localStorage.setItem('skin', id); } catch (e) {}
    }
    if (opts.broadcast !== false) webPost('setskin|' + id);
    try {
      if (typeof window.drawHist === 'function') {
        if (window._lastHist) window.drawHist(window._lastHist);
        else window.drawHist(null);
      }
    } catch (e) {}
  }
  function placeMenu(anchor) {
    if (!menu || !anchor) return;
    var r = anchor.getBoundingClientRect();
    var mw = menu.offsetWidth || 246;
    var mh = menu.offsetHeight || 220;
    var gap = 8;
    var left = Math.min(window.innerWidth - mw - 10, Math.max(10, r.right - mw));
    var below = r.bottom + gap;
    var top;
    if (below + mh <= window.innerHeight - 10) {
      top = below;
      menu.style.setProperty('--skin-origin', '100% 0%');
    } else {
      top = Math.max(10, r.top - mh - gap);
      menu.style.setProperty('--skin-origin', '100% 100%');
    }
    menu.style.left = Math.round(left) + 'px';
    menu.style.top = Math.round(top) + 'px';
  }
  function buildMenu() {
    if (menu) return;
    menu = document.createElement('div');
    menu.id = 'skinMenu';
    menu.className = 'skin-menu';
    menu.setAttribute('role', 'menu');
    menu.setAttribute('aria-hidden', 'true');
    var html = '<div class="skin-menu-head"><b>皮肤</b><span>所有窗口同步</span></div>';
    for (var i = 0; i < SKINS.length; i++) {
      var s = SKINS[i];
      html += '<button type="button" class="skin-item" role="menuitemradio" data-skin="' + s.id + '" aria-checked="false">'
        + '<span class="skin-swatch" style="background:' + s.swatch + '"></span>'
        + '<span class="skin-copy"><span class="skin-name">' + s.name + '</span><span class="skin-desc">' + s.desc + '</span></span>'
        + '<span class="skin-check">✓</span></button>';
    }
    html += '<div class="skin-menu-foot">选择后写入配置，所有窗口自动跟随切换。</div>';
    menu.innerHTML = html;
    document.body.appendChild(menu);
    menu.addEventListener('click', function (e) {
      var item = e.target && e.target.closest ? e.target.closest('.skin-item') : null;
      if (!item) return;
      e.preventDefault();
      e.stopPropagation();
      var id = item.getAttribute('data-skin');
      apply(id, { persist: true, broadcast: true, animate: true });
      hideMenu();
    });
    menu.addEventListener('mousedown', function (e) { e.stopPropagation(); });
  }
  function showMenu(anchor) {
    buildMenu();
    menuAnchor = anchor || null;
    menu.classList.add('on');
    menu.setAttribute('aria-hidden', 'false');
    refreshSelection();
    requestAnimationFrame(function () { placeMenu(menuAnchor); });
  }
  function hideMenu() {
    if (!menu) return;
    menu.classList.remove('on');
    menu.setAttribute('aria-hidden', 'true');
  }
  function toggleMenu(ev, anchor) {
    if (ev) {
      try { ev.preventDefault(); ev.stopPropagation(); } catch (e) {}
    }
    anchor = anchor || (ev && (ev.currentTarget || ev.target)) || null;
    if (menu && menu.classList.contains('on')) hideMenu();
    else showMenu(anchor);
  }
  function installGlobalClose() {
    document.addEventListener('click', function (e) {
      if (!menu || !menu.classList.contains('on')) return;
      if (menu.contains(e.target)) return;
      if (e.target && e.target.closest && e.target.closest('.skin-trigger')) return;
      hideMenu();
    }, true);
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') hideMenu();
    });
    window.addEventListener('resize', hideMenu);
    window.addEventListener('blur', hideMenu);
  }
  function installMessage() {
    try {
      if (!window.chrome || !chrome.webview || !chrome.webview.addEventListener) return;
      chrome.webview.addEventListener('message', function (e) {
        var d = e && e.data;
        if (d && d.type === 'glassbg' && d.data) {
          try {
            var live = 'url("' + d.data + '")';
            document.documentElement.style.setProperty('--live-glass', live);
            if (document.body) document.body.style.setProperty('--live-glass', live);
          } catch (e2) {}
          return;
        }
        if (d && d.type === 'skin' && d.skin && normalize(d.skin) !== current)
          apply(d.skin, { persist: false, broadcast: false, animate: true });
      });
    } catch (e) {}
  }
  function boot() {
    injectCoreCss();
    apply(readInitial(), { persist: false, broadcast: false, animate: false });
    buildMenuOnlyIfNeeded();
    installGlobalClose();
    installMessage();
    // 页面入场：一次性的液态玻璃揭幕，只影响视觉，不阻挡任何操作。
    try {
      if (document.body) {
        document.body.classList.add('lg-page-enter');
        setTimeout(function () { try { document.body.classList.remove('lg-page-enter'); } catch (e) {} }, 1150);
      }
    } catch (e) {}
    // 启动后向宿主读取一次，保证 localStorage 与 skin.txt 不一致时也能对齐
    setTimeout(function () {
      try { webPost('getskin'); } catch (e) {}
    }, 120);
  }
  function buildMenuOnlyIfNeeded() {
    // 菜单是懒加载的，创建后不影响其它皮肤；这里只提前把样式准备好。
  }

  // 顶部初始化：先加载 CSS 和根节点状态，避免首帧黑屏/闪白
  try {
    injectCoreCss();
    var early = readInitial();
    ensureCss(defFor(early));
    document.documentElement.setAttribute('data-skin', early);
    if (early === 'light') document.documentElement.classList.add('skin-light-html');
    if (early === 'frost') document.documentElement.classList.add('skin-frost-html');
    if (early === 'smoke') document.documentElement.classList.add('skin-smoke-html');
    if (early === 'aurora') document.documentElement.classList.add('skin-aurora-html');
  } catch (e) {}

  window.SkinCore = {
    skins: SKINS,
    apply: apply,
    current: function () { return current; },
    open: showMenu,
    close: hideMenu,
    toggle: toggleMenu,
    init: boot
  };
  window.toggleSkinMenu = toggleMenu;
  window.applySkin = function (id) { apply(id, { persist: true, broadcast: true, animate: true }); };

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
  else boot();
})();









