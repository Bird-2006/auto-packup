const $ = id => document.getElementById(id);
const statuses = { Succeeded: '成功', Running: '进行中', Failed: '失败', Unavailable: '已失效', Deleted: '已删除' };
let config, currentStatus, browseId, browsePath = '', restoreId, restorePath = '', polling = false;
let backupRuns = [];
const formatBytes = n => n == null ? '不限' : (n / 1073741824).toFixed(2) + ' GiB';
const duration = ms => { const seconds = Math.max(0, Math.floor(ms / 1000)); return seconds < 60 ? (ms / 1000).toFixed(1) + ' 秒' : Math.floor(seconds / 60) + ' 分 ' + seconds % 60 + ' 秒'; };
async function api(url, options) {
  const response = await fetch(url, options);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(data.error || data.message || response.statusText);
  return data;
}
const json = (method, value) => ({ method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
function button(label, action, secondary = true) {
  const element = document.createElement('button'); element.textContent = label;
  if (secondary) element.className = 'secondary';
  element.onclick = async () => { element.disabled = true; try { await action(); } catch (e) { showError(e); } finally { element.disabled = false; } };
  return element;
}
function showError(error) { $('error').textContent = error.message; $('error').hidden = false; }
async function refresh() {
  if (polling) return;
  polling = true;
  try {
    const [state, runs, logs] = await Promise.all([api('/api/status'), api('/api/backups'), api('/api/logs')]);
    currentStatus = state;
    backupRuns = runs;
    $('state').textContent = ({ Snapshot: '创建临时快照', Compressing: '多线程压缩中', Verifying: '校验压缩包', Restore: '正在恢复', FullRestore: '正在全量恢复', Replace: '正在替换文件' })[state.operation] || (state.isQueued ? '等待执行' : '空闲');
    $('run').disabled = state.isRunning || state.isQueued;
    $('cancel').disabled = !state.isRunning && !state.isQueued;
    $('next').textContent = state.nextRunAt ? new Date(state.nextRunAt).toLocaleString() : '--';
    $('processed').textContent = state.progressPercent != null ? state.progressPercent + '% · ' + formatBytes(state.archiveBytes || 0) : state.isRunning ? state.filesProcessed.toLocaleString() + ' 文件' : '--';
    $('progress').hidden = state.progressPercent == null;
    $('progress').value = state.progressPercent || 0;
    if (state.lastError) showError(new Error(state.lastError));
    $('runs').replaceChildren(...runs.map(run => {
      const row = document.createElement('tr');
      const values = [new Date(run.startedAt).toLocaleString(), run.kind === 'Vss' ? '历史 VSS' : run.snapshotPath.endsWith('.zip') ? 'ZIP 归档' : '目录归档', statuses[run.status] || run.status, run.status === 'Running' ? duration(Date.now() - Date.parse(run.startedAt)) : duration(run.durationMs), run.kind === 'Vss' ? '--' : formatBytes(run.bytesCopied), run.archiveBytes == null ? '--' : formatBytes(run.archiveBytes)];
      values.forEach((value, i) => { const cell = document.createElement('td'); cell.textContent = value; if (i === 2) { cell.className = run.status === 'Succeeded' ? 'good' : 'bad'; cell.title = run.error || ''; } row.append(cell); });
      const commands = document.createElement('td');
      if (run.status === 'Succeeded') {
        commands.append(button('浏览', () => openBrowser(run.id, '')), button('恢复', () => openRestore(run.id, '')));
        commands.append(button('全量恢复原目录', () => replaceSource(run.id)));
        commands.append(button('删除', async () => { if (confirm('删除这个快照？删除后无法恢复。')) { await api('/api/backups/' + run.id, { method: 'DELETE' }); await refresh(); } }));
      }
      if (run.error) commands.append(button('详情', () => showError(new Error(run.error))));
      row.append(commands); return row;
    }));
    $('logs').textContent = logs.map(l => '[' + new Date(l.timestamp).toLocaleString() + '] ' + l.message).join('\n');
  } catch (e) { showError(e); } finally { polling = false; }
}
async function storage() {
  try {
    const row = await api('/api/archive-storage');
    $('storage').textContent = row.directory + ' · 剩余 ' + formatBytes(row.freeBytes) + ' · ' + row.compressionThreads + ' 线程';
  } catch (e) { $('storage').textContent = '存储查询失败：' + e.message; }
}
async function openBrowser(id, path) {
  browseId = id; browsePath = path;
  $('browse-error').textContent = ''; $('browse-path').textContent = path || '/';
  $('parent').disabled = !path;
  if (!$('browser').open) $('browser').showModal();
  try {
    const entries = await api('/api/backups/' + id + '/files?path=' + encodeURIComponent(path));
    $('file-list').replaceChildren(...entries.map(entry => {
      const row = document.createElement('div'); row.className = 'file-row';
      if (entry.isDirectory) { const name = button(entry.name + '/', () => openBrowser(id, entry.relativePath)); name.className = 'name'; row.append(name); }
      else { const name = document.createElement('span'); name.textContent = entry.name; row.append(name); if (backupRuns.find(run => run.id === id)?.kind === 'Vss') row.append(button('替换原文件', () => replaceOriginal(id, entry.relativePath))); }
      row.append(button('恢复此项', () => openRestore(id, entry.relativePath)));
      return row;
    }));
    if (!entries.length) $('file-list').textContent = '空目录';
  } catch (e) { $('file-list').replaceChildren(); $('browse-error').textContent = e.message; }
}
function openRestore(id, path) {
  restoreId = id; restorePath = path; $('restore-source').textContent = '快照 #' + id + '：' + (path || '全部文件');
  $('restore-error').textContent = ''; $('destination').value = '';
  $('restore-dialog').showModal();
}
async function replaceOriginal(id, path) {
  if (!confirm('请先停止使用此数据库的服务。确认后当前原文件会被改名保留，再从快照替换。继续？')) return;
  try {
    const result = await api('/api/backups/' + id + '/restore', json('POST', { destination: '', path, replaceOriginal: true, databaseStopped: true }));
    if (!result.success) throw new Error(result.message);
    $('notice').textContent = result.message; await refresh();
  } catch (e) { showError(e); }
}
async function replaceSource(id) {
  if (!confirm('全量恢复会替换整个源目录。请先停止所有使用该目录的服务，当前目录会改名保留。继续？')) return;
  try {
    const result = await api('/api/backups/' + id + '/restore', json('POST', { destination: '', replaceSource: true, databaseStopped: true }));
    if (!result.success) throw new Error(result.message);
    $('notice').textContent = result.message; await refresh();
  } catch (e) { showError(e); }
}
$('restore-form').onsubmit = async event => {
  event.preventDefault(); const submit = event.submitter; submit.disabled = true;
  try {
    const result = await api('/api/backups/' + restoreId + '/restore', json('POST', { destination: $('destination').value, path: restorePath }));
    if (!result.success) throw new Error(result.message);
    $('notice').textContent = result.message; $('restore-dialog').close();
  } catch (e) { $('restore-error').textContent = e.message; } finally { submit.disabled = false; refresh(); }
};
$('close-browser').onclick = () => $('browser').close();
$('close-restore').onclick = () => $('restore-dialog').close();
$('parent').onclick = () => openBrowser(browseId, browsePath.replaceAll('\\', '/').split('/').slice(0, -1).join('/'));
$('run').onclick = async () => { try { await api('/api/backups/run', { method: 'POST' }); await refresh(); } catch (e) { showError(e); } };
$('cancel').onclick = async () => { try { await api('/api/backups/cancel', { method: 'POST' }); await refresh(); } catch (e) { showError(e); } };
$('config').onsubmit = async event => {
  event.preventDefault(); if (!config) return;
  try { config = await api('/api/config', json('PUT', { ...config, sourceDirectory: $('source').value, backupDirectory: $('backup-directory').value, compressionThreads: Number($('threads').value), compressionLevel: Number($('compression-level').value), intervalMinutes: Number($('interval').value), includeCrashDumps: $('include-crash-dumps').checked })); $('notice').textContent = '设置已保存'; $('error').hidden = true; await refresh(); await storage(); } catch (e) { showError(e); }
};
async function start() {
  try {
    config = await api('/api/config'); $('source').value = config.sourceDirectory; $('interval').value = config.intervalMinutes;
    $('backup-directory').value = config.backupDirectory; $('threads').value = config.compressionThreads; $('compression-level').value = config.compressionLevel;
    $('include-crash-dumps').checked = config.includeCrashDumps !== false;
    const recovery = await api('/api/recovery');
    $('recovery').textContent = recovery.previousShutdownWasClean ? '' : '检测到上次异常关机，恢复候选：' + (recovery.snapshot ? ('快照 #' + recovery.snapshot.id) : '暂无');
  } catch (e) { showError(e); }
  await refresh(); await storage();
}
setInterval(() => { $('elapsed').textContent = currentStatus?.startedAt ? duration(Date.now() - Date.parse(currentStatus.startedAt)) : '--'; }, 1000);
setInterval(refresh, 5000); setInterval(storage, 30000); start();
