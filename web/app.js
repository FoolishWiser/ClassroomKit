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

function renderState(s) {
  $('ssid').textContent = s.hotspot.ssid || '—';
  $('passphrase').textContent = s.hotspot.passphrase || '—';
  $('clients').textContent = s.hotspot.clients;
  $('clients').style.color = s.hotspot.clients >= s.hotspot.targetClients ? 'var(--amber)' : 'var(--green)';
  $('target').textContent = s.hotspot.targetClients;
  $('op-state').textContent = stateText[s.hotspot.state] || s.hotspot.state;
  $('max-clients').textContent = s.hotspot.maxClients ? s.hotspot.maxClients + ' 台' : '—';
  $('uplink').textContent = s.uplink.available ? (s.uplink.name || '已连接') + (s.uplink.isWifi ? '（Wi-Fi）' : '') : '未连接';
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

  if (document.activeElement !== $('in-ssid')) $('in-ssid').value = s.hotspot.ssid || '';
  if (document.activeElement !== $('in-pass')) $('in-pass').value = s.hotspot.passphrase || '';
  if (document.activeElement !== $('in-target')) $('in-target').value = s.hotspot.targetClients;
  $('in-wifi').checked = !!s.uplink.isWifi;
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

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, m => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[m]));
}

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
$('btn-bcu').onclick = () => act('bcu', '正在拉起强力卸载工具');
$('btn-logs').onclick = refreshLogs;
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
    allowWifiUplink: $('in-wifi').checked
  };
  try {
    await api('/api/settings', { method: 'POST', body: JSON.stringify(body) });
    toast('设置已保存');
    await refreshState();
  } catch (err) { toast(err.message, true); }
};

$('footer-link').href = location.origin + '/?t=' + encodeURIComponent(token);

refreshState();
refreshClients();
refreshLogs();
setInterval(refreshState, 2000);
setInterval(refreshClients, 5000);
setInterval(refreshLogs, 15000);
