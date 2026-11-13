const params = new URLSearchParams(location.search);
const tokenFromUrl = params.get('t');
if (tokenFromUrl) {
  sessionStorage.setItem('ck-token', tokenFromUrl);
  history.replaceState(null, '', location.pathname);
}
const token = tokenFromUrl || sessionStorage.getItem('ck-token') || '';

async function api(path, options = {}) {
  const res = await fetch(path, {
    ...options,
    headers: { 'X-Token': token, 'Content-Type': 'application/json', ...(options.headers || {}) }
  });
  if (res.status === 401) throw new Error('令牌无效：请从托盘「打开控制面板」重新进入');
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.json();
}

const $ = (id) => document.getElementById(id);

function setBadge(text, cls) {
  const el = $('badge');
  el.textContent = text;
  el.className = 'badge ' + (cls || '');
}

function fmtUptime(sec) {
  if (typeof sec !== 'number') return '—';
  const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60);
  return h > 0 ? h + ' 小时 ' + m + ' 分' : m + ' 分';
}

function fmtDuration(sec) {
  if (!sec) return '—';
  if (sec < 60) return sec + ' 秒';
  if (sec < 3600) return Math.round(sec / 60) + ' 分';
  return (sec / 3600).toFixed(1) + ' 小时';
}

const stateText = {
  On: '运行中', Off: '已关闭', Starting: '正在启动', Unknown: '未知'
};

// ---- 表单脏标记 ----
// 面板每 2 秒拉一次 /api/state 并回写表单。以前只跳过「当前有焦点的那个输入框」，
// 于是用户点一下空白处（输入框失焦）下一拍就被服务端旧值覆盖 —— 看上去像"自动回滚"。
// 现在只要用户动过表单就记为"脏"，在保存/放弃之前一律不再回写表单。
const FORM_FIELDS = ['in-ssid', 'in-pass', 'in-target', 'in-uplink', 'in-fallback', 'in-wifi'];
let formDirty = false;
let lastState = null;

function setDirty(v) {
  if (formDirty === v) return;
  formDirty = v;
  $('btn-discard').style.display = v ? '' : 'none';
  $('dirty-note').style.display = v ? '' : 'none';
}

function fillForm(s) {
  $('in-ssid').value = s.hotspot.ssid || '';
  $('in-pass').value = s.hotspot.passphrase || '';
  $('in-target').value = s.hotspot.targetClients;
  $('in-fallback').checked = s.uplink.fallbackToLan !== false;
  $('in-wifi').checked = !!s.hotspot.allowWifiUplink;
  $('in-uplink').value = s.uplink.mode === 'auto' ? 'auto' : (s.uplink.mode || 'auto');
  if ($('in-uplink').selectedIndex < 0) $('in-uplink').value = 'auto';
}

for (const id of FORM_FIELDS) {
  const el = $(id);
  if (!el) continue;
  el.addEventListener('input', () => setDirty(true));
  el.addEventListener('change', () => setDirty(true));
}

function renderState(s) {
  lastState = s;
  $('ssid').textContent = s.hotspot.ssid || '—';
  $('passphrase').textContent = s.hotspot.passphrase || '—';
  $('clients').textContent = s.hotspot.clients;
  $('clients').style.color = s.hotspot.clients >= s.hotspot.targetClients ? 'var(--amber)' : 'var(--green)';
  $('target').textContent = s.hotspot.targetClients;
  $('op-state').textContent = stateText[s.hotspot.state] || s.hotspot.state;
  $('max-clients').textContent = s.hotspot.maxClients ? s.hotspot.maxClients + ' 台' : '—';
  $('uplink').textContent = s.uplink.available
    ? (s.uplink.name || '已连接') + (s.uplink.hasInternet ? '' : '（无外网）')
    : '未连接';
  $('timeout').textContent = s.hotspot.noConnectionsTimeout === true ? '已开启（会自动关）' : '已关闭';
  $('registry-peers').textContent = s.hotspot.registryPeers ? '目标 ' + s.hotspot.registryPeers : '—';
  $('uptime').textContent = fmtUptime(s.uptimeSec);
  $('footer-version').textContent = 'v' + s.version;

  let hint = '待机';
  let cls = '';
  if (!s.enabled) { setBadge('已暂停', ''); hint = '守护已暂停，不会自动开热点'; }
  else if (s.guardState === 'On') { setBadge('热点已开', 'ok'); hint = s.hotspot.clients > 0 ? '正常服务中' : '已就绪，等待设备接入'; }
  else if (s.guardState === 'WaitUplink') { setBadge('等待上行', 'warn'); hint = '网线没插或上行无网络'; cls = 'warn'; }
  else if (s.guardState === 'BlockedWifiUplink') { setBadge('上行是 Wi-Fi', 'err'); hint = '当前用 Wi-Fi 上网，开热点会冲突'; cls = 'err'; }
  else if (s.guardState === 'Backoff') { setBadge('重试中', 'warn'); hint = '开启失败，稍后自动重试'; cls = 'warn'; }
  else if (s.guardState === 'Failed') { setBadge('异常', 'err'); hint = s.lastError || '查看日志'; cls = 'err'; }
  else { setBadge('启动中', 'warn'); hint = '正在准备热点'; cls = 'warn'; }

  $('state-hint').textContent = hint;

  const alerts = [];
  if (s.capacityWarning) alerts.push('⚠️ ' + s.capacityWarning);
  else if (s.hotspot.maxClients && s.hotspot.maxClients < s.hotspot.targetClients)
    alerts.push('⚠️ 系统上限 ' + s.hotspot.maxClients + ' 台，低于目标 ' + s.hotspot.targetClients + ' 台，点「修复设备上限」');
  if (s.capability === 'Disabled') alerts.push('❌ 本机热点能力被禁用（组策略 / 硬件 / 驱动）');
  if (s.lastError) alerts.push('ℹ️ ' + s.lastError);

  const box = $('alert');
  if (alerts.length) { box.textContent = alerts.join('\n'); box.classList.remove('hidden'); }
  else box.classList.add('hidden');

  // 旧版本的默认热点名（CLASSROOM-302）会让人推着车换教室时还粘在旧热点上，提示一次改成电脑名
  $('legacy-banner').classList.toggle('hidden', !s.legacyDefaults);
  renderHosts(s.hosts);

  // 表单只在"没被用户改过"时回写，否则保持用户正在编辑的内容
  if (!formDirty) fillForm(s);
  if (s.uplink.reason) $('uplink-note').textContent = s.uplink.reason + (s.uplink.name ? '：' + s.uplink.name : '');

  // 主按钮跟随当前状态：开着就突出「关闭热点」，关着就突出「开启热点」
  const running = s.hotspot.state === 'On' || s.guardState === 'On';
  $('btn-on').className = running ? 'btn' : 'btn primary';
  $('btn-off').className = running ? 'btn primary' : 'btn';
  if (s.uplink.isWifi && !s.hotspot.allowWifiUplink) {
    $('uplink').textContent = (s.uplink.name || 'Wi-Fi') + '（拒绝开热点）';
  }
}

function renderClients(data) {
  const body = $('clients-body');
  $('client-count').textContent = data.count ? '（' + data.count + ' 台在线）' : '';
  if (!data.count) {
    body.innerHTML = '<tr><td colspan="3" class="empty">暂无设备</td></tr>';
    return;
  }
  body.innerHTML = data.clients.map(c =>
    '<tr><td>' + escapeHtml(c.hostName || '未知设备') + '</td>' +
    '<td class="mono">' + escapeHtml(c.mac || '—') + '</td>' +
    '<td class="mono">' + escapeHtml(c.ip || '—') + '</td></tr>'
  ).join('');
}

function renderHosts(h) {
  const badge = $('hosts-badge');
  const text = $('hosts-status');
  if (!h) { badge.textContent = ''; text.textContent = '未启用'; return; }
  if (h.enabled === false) {
    badge.textContent = '已关闭';
    badge.style.color = 'var(--muted)';
    text.textContent = '配置里关闭了 hosts 加速，守护不会改动 hosts。';
    return;
  }
  if (h.applied) {
    badge.textContent = '已生效';
    badge.style.color = 'var(--green)';
    text.textContent = 'hosts 里已有 ' + h.count + ' 条记录' + (h.source ? '（来源 ' + h.source + '）' : '') +
      (h.lastSuccess ? '，最近成功 ' + new Date(h.lastSuccess).toLocaleString() : '');
  } else {
    badge.textContent = '未修改';
    badge.style.color = 'var(--muted)';
    text.textContent = h.lastError ? h.lastError : '当前没有写入记录：能直连 GitHub 就不需要它。';
  }
  if (h.serviceInstalled === false) text.textContent += '（护航任务未注册：管理员运行 --install-service 才能自动写 hosts）';
  else if (h.writable === false && h.applied === false) text.textContent += '（守护没有写权限，会交给护航任务执行）';
}

$('btn-hosts-refresh').onclick = async () => {
  const btn = $('btn-hosts-refresh');
  btn.disabled = true; btn.textContent = '刷新中…';
  try {
    const r = await api('/api/action', { method: 'POST', body: JSON.stringify({ action: 'hosts-refresh' }) });
    toast(r.message || '已刷新');
    setDirty(false);
    await refreshState();
  } catch (e) { toast(e.message, true); }
  finally { btn.disabled = false; btn.textContent = '刷新 hosts'; }
};

$('btn-hosts-restore').onclick = async () => {
  if (!confirm('删除 ClassroomKit 写进 hosts 的加速记录？（其它行不受影响）')) return;
  try {
    const r = await api('/api/action', { method: 'POST', body: JSON.stringify({ action: 'hosts-restore' }) });
    toast(r.message || '已还原');
    await refreshState();
  } catch (e) { toast(e.message, true); }
};

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
}

// ---- 版本与升级 ----
let updateBusy = false;
let lastUpdateState = null;

function renderUpdate(u) {
  if (!u) return;
  lastUpdateState = u;
  if (u.phase === 'installing') setBadge('升级中…', 'warn');
  $('update-current').textContent = 'v' + (u.current || '?');
  $('btn-update-page').href = u.htmlUrl || '#';
  $('btn-update-page').classList.toggle('hidden', !u.htmlUrl);

  const bar = $('update-progress');
  const fill = $('update-progress-bar');
  const notes = $('update-notes');
  const runBtn = $('btn-update-run');

  const downloading = u.phase === 'downloading';
  bar.classList.toggle('hidden', !downloading);
  fill.style.width = (downloading ? Math.max(2, u.progress || 0) : 0) + '%';

  if (u.notes && (u.phase === 'available' || u.phase === 'ready' || u.phase === 'downloading')) {
    notes.textContent = u.notes;
    notes.classList.remove('hidden');
  } else {
    notes.classList.add('hidden');
  }

  runBtn.classList.toggle('hidden', !(u.hasUpdate && u.phase !== 'installing'));
  runBtn.disabled = updateBusy || downloading;
  runBtn.textContent = downloading
    ? '下载中 ' + Math.round(u.progress || 0) + '%'
    : (u.phase === 'ready' ? '安装新版本' : '下载并升级');

  let text = u.phaseText || '未知状态';
  if (u.phase === 'downloading') text += '（' + Math.round(u.progress || 0) + '%）';
  if (u.latest && u.phase !== 'up_to_date' && u.phase !== 'idle') text += ' → v' + u.latest;
  if (u.error) text += '：' + u.error;
  const el = $('update-status');
  el.textContent = text;
  el.style.color = u.phase === 'failed' ? 'var(--red)' : (u.hasUpdate ? 'var(--amber)' : 'var(--muted)');
}

async function refreshUpdate() {
  try { renderUpdate(await api('/api/update')); } catch { }
}

$('btn-update-check').onclick = async () => {
  const btn = $('btn-update-check');
  btn.disabled = true;
  btn.textContent = '检查中…';
  try {
    renderUpdate(await api('/api/update', { method: 'POST', body: JSON.stringify({ action: 'check' }) }));
  } catch (e) {
    toast(e.message, true);
  } finally {
    btn.disabled = false;
    btn.textContent = '检查更新';
  }
};

$('btn-update-run').onclick = async () => {
  const u = lastUpdateState;
  if (u && u.phase === 'ready') {
    toast('安装会由后台立刻开始：请在随后弹出的窗口里允许管理员授权');
    return;
  }
  const tips = '将从 GitHub 下载 v' + ((u && u.latest) || '?') + ' 的安装包，然后弹一次管理员授权安装。\n\n' +
    '安装时 ClassroomKit 会退出并自动重启，热点会中断几秒。继续吗？';
  if (!confirm(tips)) return;
  updateBusy = true;
  try {
    renderUpdate(await api('/api/update', { method: 'POST', body: JSON.stringify({ action: 'upgrade' }) }));
  } catch (e) {
    toast(e.message, true);
  } finally {
    updateBusy = false;
  }
};

async function refreshState() {
  try {
    renderState(await api('/api/state'));
  } catch (e) {
    setBadge('离线', 'err');
    $('state-hint').textContent = e.message;
  }
}

async function refreshClients() {
  try { renderClients(await api('/api/clients')); } catch { }
}

async function refreshLogs() {
  try {
    const data = await api('/api/logs?take=120');
    $('logs').textContent = data.lines.join('\n') || '（无日志）';
  } catch (e) {
    $('logs').textContent = e.message;
  }
}

async function act(action, okText) {
  try {
    const r = await api('/api/action', { method: 'POST', body: JSON.stringify({ action }) });
    if (r.message) toast(okText || r.message);
    await refreshState();
  } catch (e) { toast(e.message, true); }
}

function toast(text, isError) {
  const box = $('alert');
  box.textContent = text;
  box.classList.remove('hidden');
  if (isError) box.style.borderColor = 'rgba(200,60,60,.6)';
  setTimeout(() => { box.classList.add('hidden'); box.style.borderColor = ''; }, 4000);
}

$('btn-on').onclick = () => act('start', '已开启热点');
$('btn-off').onclick = () => act('stop', '已关闭热点');
$('btn-fix').onclick = () => act('fix-peers', '已尝试修复设备上限');
$('btn-rename-ssid').onclick = async () => {
  try {
    const r = await api('/api/action', { method: 'POST', body: JSON.stringify({ action: 'rename-ssid', ssid: '' }) });
    toast(r.message || '已改用电脑名');
    setDirty(false);
    await refreshState();
  } catch (e) { toast(e.message, true); }
};
$('btn-bcu').onclick = () => act('bcu', '正在拉起强力卸载工具');
$('btn-logs').onclick = refreshLogs;
$('btn-rescan').onclick = () => refreshUplinks(true);
$('btn-copy').onclick = async () => {
  const text = $('passphrase').textContent;
  try { await navigator.clipboard.writeText(text); toast('密码已复制'); }
  catch { toast('复制失败，请手动记下：' + text, true); }
};

$('settings').onsubmit = async (e) => {
  e.preventDefault();
  const body = {
    ssid: $('in-ssid').value.trim(),
    passphrase: $('in-pass').value,
    targetClients: parseInt($('in-target').value, 10) || 10,
    allowWifiUplink: $('in-wifi').checked,
    uplinkMode: $('in-uplink').value || 'auto',
    fallbackToLan: $('in-fallback').checked
  };
  try {
    await api('/api/settings', { method: 'POST', body: JSON.stringify(body) });
    setDirty(false);
    toast('设置已保存');
    await refreshState();
    await refreshUplinks(true);
  } catch (err) { toast(err.message, true); }
};

$('btn-discard').onclick = () => {
  setDirty(false);
  if (lastState) fillForm(lastState);
  refreshUplinks(true);
  toast('已放弃未保存的修改');
};

$('footer-link').href = location.origin + '/?t=' + encodeURIComponent(token);

async function refreshUplinks(force) {
  if (!force && (formDirty || document.activeElement === $('in-uplink'))) return;
  try {
    const data = await api('/api/uplinks');
    const sel = $('in-uplink');
    const keep = sel.value || 'auto';
    const opts = ['<option value="auto">自动（推荐：先挑有外网的网卡）</option>'];
    for (const c of data.candidates || []) {
      const tags = [];
      if (c.isTethering) tags.push('本机热点');
      else if (c.hasInternet) tags.push('有外网');
      else if (c.hasGateway) tags.push('无外网');
      else tags.push('无网关');
      if (c.isWifi) tags.push('Wi-Fi');
      const disabled = c.usable ? '' : ' disabled';
      const label = c.label + ' · ' + (c.ipv4 || '—') + '（' + tags.join('、') + '）';
      opts.push('<option value="' + c.id + '"' + disabled + '>' + escapeHtml(label) + '</option>');
    }
    sel.innerHTML = opts.join('');
    sel.value = keep;
    if (sel.selectedIndex < 0) sel.value = 'auto';

    const chosen = data.chosen;
    $('uplink-note').textContent = chosen
      ? data.reason + '：' + chosen.label + '（' + (chosen.hasInternet ? '有外网' : '无外网·仅局域网') + '）'
      : data.reason;
  } catch (e) {
    $('uplink-note').textContent = '网卡检测失败：' + e.message;
  }
}

async function uninstall() {
  const purge = $('in-purge').checked;
  const tail = purge ? '\n\n⚠️ 配置与日志也会删除，无法恢复。' : '\n\n配置与日志会保留，重装后仍是现在的名称/密码。';
  if (!confirm('确定要卸载 ClassroomKit 吗？热点会立即关闭。' + tail)) return;
  if (!confirm('最后确认：卸载后需要重新安装才能再用热点。继续？')) return;
  try {
    const r = await api('/api/action', { method: 'POST', body: JSON.stringify({ action: 'uninstall', keepData: purge ? 'false' : 'true' }) });
    setBadge('卸载中…', 'warn');
    toast(r.message || '已开始卸载');
    setTimeout(() => { setBadge('已卸载', 'err'); $('state-hint').textContent = '程序已卸载，可关闭此页面'; }, 6000);
  } catch (e) { toast(e.message, true); }
}

$('btn-uninstall').onclick = uninstall;

refreshState();
refreshUpdate();
refreshUplinks(true);
refreshClients();
refreshLogs();
setInterval(refreshState, 2000);
setInterval(() => refreshUplinks(false), 15000);
setInterval(refreshClients, 5000);
setInterval(refreshLogs, 15000);
setInterval(refreshUpdate, 3000);
