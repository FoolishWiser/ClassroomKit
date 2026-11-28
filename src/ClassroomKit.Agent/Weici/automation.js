(function () {
  var LOG = [];
  var STOP = false;
  function log(s) { LOG.push(String(s)); }
  function root() { var el = document.querySelector('#app'); return el && el.__vue__; }
  function walkFind(pred) {
    var f = null;
    (function w(v, d) {
      if (!v || d > 18) return;
      try { if (!f && pred(v)) f = v; } catch (e) {}
      var kids = v.$children || [];
      for (var i = 0; i < kids.length; i++) w(kids[i], d + 1);
    })(root(), 0);
    return f;
  }
  function parentVm() { return walkFind(function (v) { return v.$options && v.$options.name === 'word' && v.tabList; }); }
  function wordVm() { return walkFind(function (v) { return v.$options && v.$options.name === 'Word' && v.allDataList; }); }
  function sleep(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  async function waitUntil(fn, ms) {
    var t0 = Date.now();
    while (Date.now() - t0 < ms) { if (STOP) return null; var v = fn(); if (v) return v; await sleep(200); }
    return null;
  }
  function hashQuery() {
    var q = {}, s = location.hash.split('?')[1] || '';
    s.split('&').forEach(function (p) { if (!p) return; var kv = p.split('='); q[kv[0]] = decodeURIComponent(kv[1] || ''); });
    return q;
  }
  function norm(s) { return String(s == null ? '' : s).trim().toLowerCase(); }
  function barText() {
    var m = /\u5df2\u9009\u4e2d\s*\d+\s*\u8bcd/.exec(document.body.innerText || '');
    return m ? m[0].replace(/\s+/g, ' ') : '';
  }
  function unitLabel(name) { return String(name || '').split('|')[0]; }

  var ROW_LABELS = ['\u9009\u62e9\u6a21\u5f0f', '\u9ed8\u8ba4\u53d1\u97f3', '\u64ad\u653e\u987a\u5e8f', '\u64ad\u653e\u6b21\u6570', '\u6574\u4f53\u5faa\u73af\u64ad\u653e', '\u8ddf\u8bfb\u65f6\u957f'];
  function radioRows(dlg) {
    return Array.prototype.slice.call(dlg.querySelectorAll('div')).filter(function (d) {
      if (!d.querySelectorAll('.el-radio').length) return false;
      var t = (d.innerText || '').replace(/\s+/g, ' ').trim();
      for (var i = 0; i < ROW_LABELS.length; i++) if (t.indexOf(ROW_LABELS[i]) === 0) return true;
      return false;
    });
  }
  function setRadio(dlg, label, text) {
    var rows = radioRows(dlg);
    for (var i = 0; i < rows.length; i++) {
      var t = rows[i].innerText.replace(/\s+/g, ' ').trim();
      if (t.indexOf(label) === 0) {
        var rs = Array.prototype.slice.call(rows[i].querySelectorAll('.el-radio'));
        for (var j = 0; j < rs.length; j++) {
          if (rs[j].innerText.trim() === text) { rs[j].click(); return true; }
        }
      }
    }
    return false;
  }
  function checkedTexts(dlg) {
    var out = {};
    var rows = radioRows(dlg);
    rows.forEach(function (r) {
      var head = (r.innerText.replace(/\s+/g, ' ').split(/[:\uff1a]/)[0] || '').trim();
      var rs = Array.prototype.slice.call(r.querySelectorAll('.el-radio'));
      for (var i = 0; i < rs.length; i++) {
        var inp = rs[i].querySelector('.el-radio__input');
        if (inp && inp.className.indexOf('is-checked') >= 0) out[head] = rs[i].innerText.trim();
      }
    });
    return out;
  }

  function loginBtn() {
    var btns = Array.prototype.slice.call(document.querySelectorAll('a.login-Btn'));
    for (var i = 0; i < btns.length; i++) {
      var r = btns[i].getBoundingClientRect();
      if (r.width > 2 && r.height > 2 && r.top > 0) return btns[i];
    }
    return null;
  }
  async function doLogin() {
    var btn = loginBtn();
    if (!btn) return 'NO_BUTTON';
    var ins = Array.prototype.slice.call(document.querySelectorAll('input')).filter(function (e) { return e.offsetParent !== null; });
    var phone = null, pwd = null;
    for (var i = 0; i < ins.length; i++) {
      var ph = ins[i].placeholder || '';
      if (!phone && ph.indexOf('\u624b\u673a\u53f7') >= 0) phone = ins[i];
      if (!pwd && ph.indexOf('\u5BC6\u7801') >= 0) pwd = ins[i];
    }
    if (!phone || !pwd) return 'NO_FORM';
    if (!phone.value || !pwd.value) return 'EMPTY_FIELDS';
    btn.click();
    var ok = await waitUntil(function () { return !loginBtn(); }, 20000);
    return ok ? 'OK' : 'FAILED';
  }

  async function openVictor() {
    if (location.hash.indexOf('/victor') < 0) { location.hash = '#/victor?index=0'; }
    return await waitUntil(parentVm, 25000);
  }

  function readCtx(p) {
    var q = hashQuery();
    return {
      productId: q.productId || p.activeItemId,
      bookId: +(q.activeBook || p.activeBook),
      contentId: +(q.activeContent || p.activeContentId || 0),
      ctype: +(q.activeContentType || p.activeContentType || 1)
    };
  }
  function unitHash(ctx, unitId) {
    return '#/victor/weici/word?productId=' + ctx.productId + '&activeBook=' + ctx.bookId + '&activeUnit=' + unitId +
      '&activeContent=' + ctx.contentId + '&activeContentType=' + ctx.ctype;
  }
  async function openUnit(ctx, unitId) {
    var prev = wordVm();
    var prevList = prev && prev.allDataList;
    if (prev && String(prev.catalogId) === String(unitId) && prevList && prevList.length) return prev;
    location.hash = unitHash(ctx, unitId);
    return await waitUntil(function () {
      var c = wordVm();
      if (!c || String(c.catalogId) !== String(unitId)) return null;
      if (!c.allDataList || !c.allDataList.length) return null;
      if (prevList && c.allDataList === prevList) return null;
      return c;
    }, 12000);
  }

  async function scanAll(p, ctx) {
    var units = (p.unitList || []).map(function (u) { return { id: u.catalog_id, name: unitLabel(u.catalog_name) }; });
    log('bookId=' + ctx.bookId + ' productId=' + ctx.productId + ' contentId=' + ctx.contentId + ' units=' + units.length);
    if (!units.length) return { ok: false, code: 'NO_UNITS' };
    var scan = [];
    for (var k = 0; k < units.length; k++) {
      if (STOP) return { ok: false, code: 'STOPPED' };
      var u = units[k];
      var w = await openUnit(ctx, u.id);
      if (!w) { scan.push({ unitId: u.id, unitName: u.name, words: [] }); log('unit ' + u.name + ' = 读取失败'); continue; }
      var key = w.idKey || 'word';
      var words = (w.allDataList || []).map(function (x) { return x[key]; });
      scan.push({ unitId: u.id, unitName: u.name, words: words });
      log('unit ' + u.name + ' = ' + words.length + ' words (' + (words[0] || '-') + ' .. ' + (words[words.length - 1] || '-') + ')');
      await sleep(120);
    }
    return { ok: true, scan: scan };
  }

  async function start(from, to, opts) {
    opts = opts || {};
    LOG.length = 0;
    STOP = false;
    var pron = opts.pron || '\u82f1\u7f8e\u6df7\u97f3';
    var loop = opts.loop || '1\u6b21';
    var dryRun = !!opts.dryRun;

    if (loginBtn()) {
      var lr = await doLogin();
      if (lr === 'EMPTY_FIELDS' || lr === 'NO_FORM') return { ok: false, code: 'NEED_CREDENTIALS', detail: lr, log: LOG };
      if (lr !== 'OK') return { ok: false, code: 'LOGIN_FAILED', detail: lr, log: LOG };
      log('已模拟点击登录按钮');
      await sleep(1500);
      if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
    }

    var p = await openVictor();
    if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
    if (!p) return { ok: false, code: loginBtn() ? 'NEED_LOGIN' : 'NO_VICTOR_PAGE', hash: location.hash, log: LOG };
    var ctx = readCtx(p);

    var sc = await scanAll(p, ctx);
    if (!sc.ok) return { ok: false, code: sc.code, log: LOG };
    if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
    var scan = sc.scan;
    var total = 0;
    for (var t = 0; t < scan.length; t++) total += scan[t].words.length;
    log('已读取 ' + scan.length + ' 个单元 / ' + total + ' 个单词');

    var fromN = norm(from);
    if (!fromN) return { ok: false, code: 'NO_FIRST_WORD', log: LOG };
    // 尾词留空表示「只领第一个词」，不能拿空串去匹配。
    var toN = norm(to) || fromN;
    var flat = [], a = -1, b = -1;
    for (var i = 0; i < scan.length; i++) {
      for (var j = 0; j < scan[i].words.length; j++) {
        var idx = flat.length;
        flat.push({ u: scan[i], w: scan[i].words[j] });
        if (a < 0 && norm(scan[i].words[j]) === fromN) a = idx;
        if (norm(scan[i].words[j]) === toN) b = idx;
      }
    }
    var summary = scan.map(function (x) { return { name: x.unitName, count: x.words.length, first: x.words[0] || '', last: x.words[x.words.length - 1] || '' }; });
    if (a < 0 || b < 0) {
      return { ok: false, code: 'WORD_NOT_FOUND', missing: a < 0 ? from : to, units: summary, log: LOG };
    }
    if (a > b) return { ok: false, code: 'ORDER_REVERSED', units: summary, log: LOG };
    var picked = flat.slice(a, b + 1);
    var words = picked.map(function (x) { return x.w; });
    var firstU = picked[0].u, lastU = picked[picked.length - 1].u;
    var cross = String(firstU.unitId) !== String(lastU.unitId);
    log('\u533a\u95f4: ' + firstU.unitName + ' \u7b2c' + (a + 1) + ' \u2192 ' + lastU.unitName + ' \u7b2c' + (b + 1) + '\uff0c\u5171 ' + words.length + ' \u8bcd' + (cross ? '\uff08\u8de8\u5355\u5143\uff09' : ''));

    var wv = await openUnit(ctx, firstU.unitId);
    if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
    if (!wv) return { ok: false, code: 'UNIT_NOT_LOADED', unit: firstU.unitName, log: LOG };
    wv.selectWord = words.slice();
    wv.setSelectRow();
    await sleep(600);
    var chk = wordVm();
    var bar = barText();
    if (!chk || (chk.selectWord || []).length !== words.length) {
      return { ok: false, code: 'SELECT_MISMATCH', want: words.length, got: chk ? (chk.selectWord || []).length : -1, bar: bar, log: LOG };
    }
    log('selected ' + words.length + ' words, bar=' + bar);

    var startBtn = Array.prototype.slice.call(document.querySelectorAll('div.start'))
      .filter(function (e) { return e.innerText.trim() === '\u5f00\u59cb'; })[0];
    if (!startBtn) return { ok: false, code: 'NO_START_BUTTON', log: LOG };
    startBtn.click();
    if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
    var dlg = await waitUntil(function () {
      return Array.prototype.slice.call(document.querySelectorAll('.el-dialog')).filter(function (d) {
        return d.innerText && d.innerText.indexOf('\u9886\u8bfb/\u9ed8\u5199\u8bbe\u7f6e') >= 0;
      })[0] || null;
    }, 6000);
    if (!dlg) return { ok: false, code: STOP ? 'STOPPED' : 'NO_DIALOG', log: LOG };
    var setP = setRadio(dlg, '\u9ed8\u8ba4\u53d1\u97f3', pron);
    var setL = setRadio(dlg, '\u6574\u4f53\u5faa\u73af\u64ad\u653e', loop);
    if (!setP || !setL) return { ok: false, code: 'SETTING_NOT_APPLIED', setP: setP, setL: setL, log: LOG };
    await sleep(300);
    var checked = checkedTexts(dlg);
    log('dialog pron=' + checked['\u9ed8\u8ba4\u53d1\u97f3'] + ' loop=' + checked['\u6574\u4f53\u5faa\u73af\u64ad\u653e']);

    var started = false;
    if (!dryRun) {
      if (STOP) return { ok: false, code: 'STOPPED', log: LOG };
      var go = dlg.querySelector('.el-dialog__footer div.start');
      // 正式运行必须真的点到「开始」；没点到不能报 READY，否则老师会以为已经在领读。
      if (!go) return { ok: false, code: 'NO_START_BUTTON', detail: 'footer', log: LOG };
      go.click(); started = true; await sleep(1500);
    }
    return {
      ok: true, code: started ? 'STARTED' : 'READY',
      unit: cross ? (firstU.unitName + ' ~ ' + lastU.unitName) : firstU.unitName,
      unitId: firstU.unitId, cross: cross, count: words.length, words: words, bar: bar,
      totalWords: total, units: summary,
      dialog: checked, setP: setP, setL: setL, started: started, hash: location.hash, log: LOG
    };
  }

  function probe() {
    var p = parentVm();
    var h = location.hash || '#/';
    var btn = loginBtn();
    return JSON.stringify({
      hash: h,
      hasParent: !!p,
      units: p ? (p.unitList || []).length : 0,
      atLogin: !!btn || h === '#/' || h === '' || h.indexOf('#/container') === 0,
      w: window.innerWidth,
      h2: window.innerHeight
    });
  }

  function stop() { STOP = true; log('收到取消，正在停止'); }
  window.__WAR = { start: start, stop: stop, probe: probe, log: function () { return LOG.slice(); } };
  return 'installed';
})()