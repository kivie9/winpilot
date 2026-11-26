(() => {
  const pending = new Map();
  let reqSeq = 0;
  let state = null;
  let logFilter = 'ALL';
  let installedCache = [];
  let selectedInstalled = new Set();

  function rpc(action, payload = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++reqSeq);
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, action, payload });
    });
  }

  window.chrome.webview.addEventListener('message', (ev) => {
    const msg = ev.data;
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.ok) resolve(msg.data);
      else reject(new Error(msg.error || 'Lỗi không xác định'));
      return;
    }

    if (msg.event === 'logsUpdated') {
      if (state) state.logs = msg.data;
      renderLogs();
    } else if (msg.event === 'stateUpdated') {
      state = msg.data;
      renderAll();
    }
  });

  function toast(title, message = '', type = 'info') {
    const container = document.getElementById('toast-container');
    const el = document.createElement('div');
    el.className = 'toast';
    const color = type === 'error' ? '#f87171' : type === 'success' ? '#34d399' : '#e4e4e7';
    el.innerHTML = `
      <span class="dot" style="margin-top:6px;background:${color}"></span>
      <div style="flex:1">
        <div class="toast-title">${escapeHtml(title)}</div>
        ${message ? `<div class="toast-msg">${escapeHtml(message)}</div>` : ''}
      </div>
    `;
    container.appendChild(el);
    setTimeout(() => {
      el.style.opacity = '0';
      el.style.transition = 'opacity 140ms ease';
      setTimeout(() => el.remove(), 150);
    }, 3600);
  }

  function escapeHtml(str) {
    return String(str ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  // ---- Navigation ----
  document.querySelectorAll('.nav-item').forEach((btn) => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('.nav-item').forEach((b) => b.classList.remove('active'));
      document.querySelectorAll('.view').forEach((v) => v.classList.remove('active'));
      btn.classList.add('active');
      document.getElementById(`tab-${btn.dataset.tab}`).classList.add('active');
    });
  });

  // ---- Segmented Controls ----
  function initSegmented(containerId, onChange) {
    const root = document.getElementById(containerId);
    root.querySelectorAll('.seg-btn').forEach((btn) => {
      btn.addEventListener('click', () => {
        root.querySelectorAll('.seg-btn').forEach((b) => b.classList.remove('active'));
        btn.classList.add('active');
        onChange?.(btn.dataset.value);
      });
    });
  }
  function setSegmentedValue(containerId, value) {
    document.querySelectorAll(`#${containerId} .seg-btn`).forEach((b) => {
      b.classList.toggle('active', b.dataset.value === value);
    });
  }
  function getSegmentedValue(containerId) {
    const active = document.querySelector(`#${containerId} .seg-btn.active`);
    return active ? active.dataset.value : null;
  }

  // ---- Custom Select ----
  function initCustomSelect(selectId) {
    const el = document.getElementById(selectId);
    el.querySelector('.custom-select-trigger').addEventListener('click', (e) => {
      e.stopPropagation();
      document.querySelectorAll('.custom-select.open').forEach((s) => { if (s !== el) s.classList.remove('open'); });
      el.classList.toggle('open');
    });
    el.querySelectorAll('.custom-select-option').forEach((opt) => {
      opt.addEventListener('click', () => {
        setCustomSelectValue(selectId, opt.dataset.value);
        el.classList.remove('open');
      });
    });
  }
  function setCustomSelectValue(selectId, val) {
    const el = document.getElementById(selectId);
    el.dataset.value = val;
    el.querySelectorAll('.custom-select-option').forEach((o) => {
      const match = o.dataset.value === val;
      o.classList.toggle('selected', match);
      if (match) el.querySelector('.custom-select-label').textContent = o.textContent;
    });
  }
  initCustomSelect('edit-app-window-select');
  initCustomSelect('wh-type-select');
  document.addEventListener('click', () => {
    document.querySelectorAll('.custom-select.open').forEach((s) => s.classList.remove('open'));
  });

  // ---- Render State ----
  function renderAll() {
    if (!state) return;
    renderDashboard();
    renderProfilesAndConditions();
    renderApps();
    renderVmware();
    renderTelegram();
    renderWebhooks();
    renderLogs();
    renderSettings();
  }

  function renderDashboard() {
    document.getElementById('dash-device-pill').textContent = `${state.deviceName} · ${state.localIp}`;
    document.getElementById('dash-autostart-toggle').checked = state.registryEnabled;
    document.getElementById('dash-startup-status').textContent = state.registryEnabled ? 'Đang bật' : 'Đang tắt';
    document.getElementById('sidebar-startup-text').textContent = state.registryEnabled
      ? 'Startup Windows: Đang bật'
      : 'Startup Windows: Chưa bật';

    const activeProf = state.profiles.find((p) => p.id === state.activeProfileId);
    const enabledCount = state.apps.filter((a) => a.enabled).length;
    document.getElementById('nav-apps-count').textContent = state.apps.length;
    document.getElementById('dash-apps-pill').textContent = activeProf ? activeProf.name : 'Mặc định';
    document.getElementById('dash-apps-count').textContent = `${enabledCount} / ${state.apps.length} ứng dụng`;
    document.getElementById('dash-last-startup').textContent = state.stats.lastStartupSummary;

    const tgPill = document.getElementById('dash-tg-pill');
    tgPill.className = 'pill ' + (state.telegram.isConfigured ? 'pill-emerald' : 'pill-amber');
    tgPill.textContent = state.telegram.isConfigured ? 'Sẵn sàng' : 'Chưa đủ thông tin';
    document.getElementById('dash-tg-status').textContent = state.telegram.isConfigured ? 'Đã kết nối' : 'Chưa cấu hình';
    document.getElementById('dash-last-tg').textContent = state.stats.lastTelegramStatus;
    document.getElementById('dash-last-shutdown').textContent = state.stats.lastShutdownStatus;
  }

  function renderProfilesAndConditions() {
    const pillsEl = document.getElementById('profile-pills');
    pillsEl.innerHTML = state.profiles.map((p) => `
      <button type="button" class="profile-pill ${p.id === state.activeProfileId ? 'active' : ''}" data-pid="${escapeHtml(p.id)}">
        ${escapeHtml(p.name)}
      </button>
    `).join('');

    document.getElementById('btn-delete-profile').disabled = state.profiles.length <= 1;

    const cond = state.conditions || { enabled: false, allowedDays: [1, 2, 3, 4, 5], allowedWifiSsids: '' };
    document.getElementById('cond-enabled').checked = !!cond.enabled;
    document.getElementById('cond-wifi').value = cond.allowedWifiSsids || '';
    const days = new Set(cond.allowedDays || []);
    document.querySelectorAll('#cond-days .day-pill').forEach((btn) => {
      btn.classList.toggle('active', days.has(parseInt(btn.dataset.day, 10)));
    });
  }

  function renderLogRows(logs) {
    if (!logs || logs.length === 0) {
      return `<div class="empty-state" style="padding:24px"><div class="empty-desc">Chưa có nhật ký nào.</div></div>`;
    }
    return logs.map((l) => {
      const pillClass = l.level === 'Success' ? 'pill-emerald' : l.level === 'Warning' ? 'pill-amber' : l.level === 'Error' ? 'pill-red' : 'pill-neutral';
      const label = l.level === 'Success' ? 'OK' : l.level === 'Warning' ? 'WARN' : l.level === 'Error' ? 'ERR' : 'INFO';
      return `
        <div class="log-row">
          <span class="log-time">${escapeHtml(l.time)}</span>
          <span class="pill ${pillClass}">${label}</span>
          <span class="log-cat">${escapeHtml(l.category)}</span>
          <span class="log-msg">${escapeHtml(l.message)}</span>
        </div>
      `;
    }).join('');
  }

  function renderLogs() {
    if (!state) return;
    document.getElementById('dash-recent-logs').innerHTML = renderLogRows(state.logs.slice(0, 12));

    let filtered = state.logs;
    if (logFilter === 'Success') filtered = state.logs.filter((l) => l.level === 'Success');
    else if (logFilter === 'Issues') filtered = state.logs.filter((l) => l.level === 'Warning' || l.level === 'Error');
    document.getElementById('full-logs-list').innerHTML = renderLogRows(filtered);
  }

  function renderApps() {
    const query = (document.getElementById('apps-search').value || '').trim().toLowerCase();
    const listEl = document.getElementById('apps-list');
    const emptyEl = document.getElementById('apps-empty');

    const filtered = state.apps.filter((a) =>
      !query || a.name.toLowerCase().includes(query) || a.path.toLowerCase().includes(query));

    emptyEl.classList.toggle('hidden', filtered.length > 0);
    listEl.innerHTML = filtered.map((app, idx) => {
      const iconHtml = app.iconDataUrl
        ? `<img src="${app.iconDataUrl}" alt="" />`
        : `<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="#71717a" stroke-width="1.8"><rect x="3" y="3" width="18" height="18" rx="3"/></svg>`;

      const badges = [
        !app.exists ? `<span class="pill pill-red">Không tìm thấy file</span>` : '',
        app.delaySeconds > 0 ? `<span class="pill pill-neutral">+${app.delaySeconds}s</span>` : '',
        app.runAsAdmin ? `<span class="pill pill-amber">Admin</span>` : ''
      ].join('');

      return `
        <div class="app-row" data-id="${escapeHtml(app.id)}">
          <label class="switch">
            <input type="checkbox" class="app-toggle" ${app.enabled ? 'checked' : ''} />
            <span class="slider"></span>
          </label>
          <div class="app-icon-box">${iconHtml}</div>
          <div class="app-main">
            <div class="app-title-row">
              <span class="app-name">${escapeHtml(app.name)}</span>
              ${badges}
            </div>
            <div class="app-path">${escapeHtml(app.path)}${app.arguments ? ' ' + escapeHtml(app.arguments) : ''}</div>
          </div>
          <div class="app-actions">
            <button class="icon-btn" data-act="up" title="Lên" ${idx === 0 ? 'disabled' : ''}>↑</button>
            <button class="icon-btn" data-act="down" title="Xuống" ${idx === filtered.length - 1 ? 'disabled' : ''}>↓</button>
            <button class="icon-btn" data-act="run" title="Mở thử">▶</button>
            <button class="icon-btn" data-act="edit" title="Sửa">✎</button>
            <button class="icon-btn danger" data-act="delete" title="Xóa">✕</button>
          </div>
        </div>
      `;
    }).join('');
  }

  function renderTelegram() {
    const tg = state.telegram;
    document.getElementById('tg-enabled').checked = tg.enabled;
    document.getElementById('tg-token').value = tg.botToken || '';
    document.getElementById('tg-chat-ids').value = tg.chatIds || '';
    document.getElementById('tg-notify-startup').checked = tg.notifyOnStartup;
    setSegmentedValue('startup-mode-segmented', tg.startupSendMode);
    document.getElementById('tg-startup-template').value = tg.startupTemplate || '';
    document.getElementById('tg-notify-shutdown').checked = tg.notifyOnShutdown;
    document.getElementById('tg-notify-logoff').checked = tg.notifyOnLogoff;
    document.getElementById('tg-notify-sleep').checked = tg.notifyOnSleep;
    document.getElementById('tg-notify-wake').checked = tg.notifyOnWake;
    document.getElementById('tg-notify-lock').checked = tg.notifyOnLock;
    document.getElementById('tg-notify-unlock').checked = tg.notifyOnUnlock;
    document.getElementById('tg-shutdown-timeout').value = tg.shutdownTimeoutMs || 3500;
    document.getElementById('tg-shutdown-template').value = tg.shutdownTemplate || '';
    document.getElementById('tg-remote-enabled').checked = !!tg.enableRemoteCommands;
    document.getElementById('tg-remote-chats').value = tg.allowedCommandChatIds || '';
    updatePreviews();
  }

  function renderWebhooks() {
    const list = state.extraWebhooks || [];
    const el = document.getElementById('webhooks-list');
    if (list.length === 0) {
      el.innerHTML = `<div class="muted-xs" style="padding:8px 0">Chưa có Webhook ngoài nào.</div>`;
      return;
    }
    el.innerHTML = list.map((w) => `
      <div class="webhook-row" data-wid="${escapeHtml(w.id)}">
        <div class="webhook-info">
          <div class="webhook-name">${escapeHtml(w.name)} <span class="pill pill-neutral">${escapeHtml(w.channelType)}</span></div>
          <div class="webhook-url">${escapeHtml(w.url)}</div>
        </div>
        <div style="display:flex;gap:4px">
          <button class="btn btn-secondary btn-sm" data-wh="test" type="button">Test</button>
          <button class="icon-btn danger" data-wh="delete" type="button">✕</button>
        </div>
      </div>
    `).join('');
  }

  function renderSettings() {
    document.getElementById('set-autostart').checked = state.registryEnabled;
    document.getElementById('settings-reg-cmd').textContent = state.registryCommand || 'Chưa đăng ký trong HKCU\\...\\Run';
    document.getElementById('set-start-minimized').checked = state.general.startMinimizedToTray;
    document.getElementById('set-minimize-close').checked = state.general.minimizeToTrayOnClose;
    document.getElementById('set-only-startup-arg').checked = state.general.runStartupTasksOnlyWithStartupArg;
    document.getElementById('set-startup-delay').value = state.general.startupDelaySeconds;
    document.getElementById('set-network-wait').value = state.general.networkWaitTimeoutSeconds;
    document.getElementById('settings-config-path').textContent = `Tệp cấu hình: ${state.configPath}`;
  }

  async function updatePreviews() {
    const st = document.getElementById('tg-startup-template').value;
    const sd = document.getElementById('tg-shutdown-template').value;
    try {
      const [r1, r2] = await Promise.all([
        rpc('renderPreview', { template: st, isShutdown: false }),
        rpc('renderPreview', { template: sd, isShutdown: true })
      ]);
      document.getElementById('preview-startup').innerHTML = r1.html;
      document.getElementById('preview-shutdown').innerHTML = r2.html;
    } catch { /* ignore */ }
  }

  function collectTelegramPayload() {
    return {
      enabled: document.getElementById('tg-enabled').checked,
      botToken: document.getElementById('tg-token').value.trim(),
      chatIds: document.getElementById('tg-chat-ids').value.trim(),
      notifyOnStartup: document.getElementById('tg-notify-startup').checked,
      startupSendMode: getSegmentedValue('startup-mode-segmented') || 'ParallelImmediate',
      startupTemplate: document.getElementById('tg-startup-template').value,
      notifyOnShutdown: document.getElementById('tg-notify-shutdown').checked,
      notifyOnLogoff: document.getElementById('tg-notify-logoff').checked,
      notifyOnSleep: document.getElementById('tg-notify-sleep').checked,
      notifyOnWake: document.getElementById('tg-notify-wake').checked,
      notifyOnLock: document.getElementById('tg-notify-lock').checked,
      notifyOnUnlock: document.getElementById('tg-notify-unlock').checked,
      shutdownTimeoutMs: parseInt(document.getElementById('tg-shutdown-timeout').value, 10) || 3500,
      shutdownTemplate: document.getElementById('tg-shutdown-template').value,
      enableRemoteCommands: document.getElementById('tg-remote-enabled').checked,
      allowedCommandChatIds: document.getElementById('tg-remote-chats').value.trim()
    };
  }

  // ---- Event Wiring ----
  document.getElementById('dash-autostart-toggle').addEventListener('change', async (e) => {
    try {
      state = await rpc('toggleAutoStart', { enabled: e.target.checked });
      renderAll();
      toast(e.target.checked ? 'Đã bật khởi động cùng Windows' : 'Đã tắt khởi động cùng Windows', '', 'success');
    } catch (err) {
      toast('Lỗi Registry', err.message, 'error');
    }
  });

  document.getElementById('btn-run-startup').addEventListener('click', runStartupNow);
  document.getElementById('btn-run-apps-tab').addEventListener('click', runStartupNow);
  async function runStartupNow() {
    toast('Đang chạy quy trình khởi động...', 'Đang mở ứng dụng và gửi thông báo song song.');
    try {
      state = await rpc('runStartupNow');
      renderAll();
      toast('Hoàn tất chạy khởi động', state.stats.lastStartupSummary, 'success');
    } catch (err) {
      toast('Lỗi chạy khởi động', err.message, 'error');
    }
  }

  document.getElementById('btn-test-startup-tg').addEventListener('click', testStartupTg);
  document.getElementById('btn-test-startup-inline').addEventListener('click', testStartupTg);
  async function testStartupTg() {
    try {
      const res = await rpc('testStartupTelegram', collectTelegramPayload());
      state = res.state;
      renderAll();
      toast(res.allSucceeded ? 'Gửi Telegram thành công' : 'Gửi Telegram thất bại', res.summary, res.allSucceeded ? 'success' : 'error');
    } catch (err) {
      toast('Lỗi Telegram', err.message, 'error');
    }
  }

  document.getElementById('btn-test-shutdown-tg').addEventListener('click', testShutdownTg);
  document.getElementById('btn-test-shutdown-inline').addEventListener('click', testShutdownTg);
  async function testShutdownTg() {
    try {
      const res = await rpc('testShutdownTelegram', collectTelegramPayload());
      state = res.state;
      renderAll();
      toast(res.allSucceeded ? 'Giả lập tắt máy thành công' : 'Giả lập tắt máy thất bại', res.summary, res.allSucceeded ? 'success' : 'error');
    } catch (err) {
      toast('Lỗi Telegram', err.message, 'error');
    }
  }

  // Profiles & Conditions
  document.getElementById('profile-pills').addEventListener('click', async (e) => {
    const pill = e.target.closest('.profile-pill');
    if (!pill) return;
    state = await rpc('setActiveProfile', { profileId: pill.dataset.pid });
    renderAll();
  });

  document.getElementById('btn-new-profile').addEventListener('click', () => {
    document.getElementById('new-profile-name').value = '';
    document.getElementById('modal-profile').classList.remove('hidden');
  });
  document.getElementById('btn-close-profile-modal').addEventListener('click', () => document.getElementById('modal-profile').classList.add('hidden'));
  document.getElementById('btn-cancel-profile-modal').addEventListener('click', () => document.getElementById('modal-profile').classList.add('hidden'));
  document.getElementById('btn-save-profile-modal').addEventListener('click', async () => {
    const name = document.getElementById('new-profile-name').value.trim();
    if (!name) return;
    try {
      state = await rpc('createProfile', { name });
      document.getElementById('modal-profile').classList.add('hidden');
      renderAll();
      toast('Đã tạo Profile mới', name, 'success');
    } catch (err) {
      toast('Lỗi tạo Profile', err.message, 'error');
    }
  });

  document.getElementById('btn-delete-profile').addEventListener('click', async () => {
    try {
      state = await rpc('deleteProfile', { profileId: state.activeProfileId });
      renderAll();
      toast('Đã xóa Profile', '', 'info');
    } catch (err) {
      toast('Không thể xóa', err.message, 'error');
    }
  });

  document.getElementById('cond-days').addEventListener('click', (e) => {
    const btn = e.target.closest('.day-pill');
    if (btn) btn.classList.toggle('active');
  });

  document.getElementById('btn-fill-wifi').addEventListener('click', async () => {
    const res = await rpc('getCurrentWifi');
    if (!res.ssid) {
      toast('Không tìm thấy Wi-Fi đang kết nối', 'Có thể máy đang dùng dây LAN.', 'info');
      return;
    }
    const inp = document.getElementById('cond-wifi');
    const existing = inp.value.split(',').map((s) => s.trim()).filter(Boolean);
    if (!existing.includes(res.ssid)) existing.push(res.ssid);
    inp.value = existing.join(', ');
  });

  document.getElementById('btn-save-conditions').addEventListener('click', async () => {
    const days = Array.from(document.querySelectorAll('#cond-days .day-pill.active'))
      .map((b) => parseInt(b.dataset.day, 10));
    state = await rpc('saveConditions', {
      enabled: document.getElementById('cond-enabled').checked,
      allowedDays: days,
      allowedWifiSsids: document.getElementById('cond-wifi').value.trim()
    });
    renderAll();
    toast('Đã lưu điều kiện tự động chạy', '', 'success');
  });

  // Apps Actions
  document.getElementById('apps-search').addEventListener('input', renderApps);
  document.getElementById('btn-add-browse').addEventListener('click', async () => {
    state = await rpc('browseAppFiles');
    renderAll();
  });
  document.getElementById('btn-add-manual').addEventListener('click', () => openAppModal(null));

  document.getElementById('apps-list').addEventListener('click', async (e) => {
    const row = e.target.closest('.app-row');
    if (!row) return;
    const id = row.dataset.id;

    if (e.target.classList.contains('app-toggle')) {
      state = await rpc('toggleAppEnabled', { id, enabled: e.target.checked });
      renderDashboard();
      return;
    }

    const btn = e.target.closest('button[data-act]');
    if (!btn) return;
    const act = btn.dataset.act;

    if (act === 'edit') {
      const app = state.apps.find((a) => a.id === id);
      if (app) openAppModal(app);
    } else if (act === 'delete') {
      state = await rpc('deleteApp', { id });
      renderAll();
      toast('Đã xóa ứng dụng', '', 'info');
    } else if (act === 'run') {
      try {
        const res = await rpc('launchSingleApp', { id });
        const ok = res.outcome === 'Launched' || res.outcome === 'SkippedAlreadyRunning';
        toast(res.appName, res.detail, ok ? 'success' : 'error');
      } catch (err) {
        toast('Không thể mở ứng dụng', err.message, 'error');
      }
    } else if (act === 'up' || act === 'down') {
      const ids = state.apps.map((a) => a.id);
      const idx = ids.indexOf(id);
      const swap = act === 'up' ? idx - 1 : idx + 1;
      if (swap >= 0 && swap < ids.length) {
        [ids[idx], ids[swap]] = [ids[swap], ids[idx]];
        state = await rpc('reorderApps', { orderedIds: ids });
        renderApps();
      }
    }
  });

  // App Modal
  function openAppModal(app) {
    document.getElementById('modal-app-title').textContent = app ? 'Chỉnh sửa ứng dụng' : 'Thêm ứng dụng thủ công';
    document.getElementById('edit-app-id').value = app ? app.id : '';
    document.getElementById('edit-app-name').value = app ? app.name : '';
    document.getElementById('edit-app-path').value = app ? app.path : '';
    document.getElementById('edit-app-args').value = app ? (app.arguments || '') : '';
    document.getElementById('edit-app-workdir').value = app ? (app.workingDirectory || '') : '';
    document.getElementById('edit-app-delay').value = app ? app.delaySeconds : 0;
    setCustomSelectValue('edit-app-window-select', app ? app.windowMode : 'Normal');
    document.getElementById('edit-app-skip').checked = app ? app.skipIfAlreadyRunning : true;
    document.getElementById('edit-app-admin').checked = app ? app.runAsAdmin : false;
    document.getElementById('modal-app-editor').classList.remove('hidden');
  }
  function closeAppModal() { document.getElementById('modal-app-editor').classList.add('hidden'); }
  document.getElementById('btn-close-app-modal').addEventListener('click', closeAppModal);
  document.getElementById('btn-cancel-app-modal').addEventListener('click', closeAppModal);
  document.getElementById('btn-browse-workdir').addEventListener('click', async () => {
    const res = await rpc('browseWorkDir');
    if (res.path) document.getElementById('edit-app-workdir').value = res.path;
  });

  document.getElementById('btn-save-app-modal').addEventListener('click', async () => {
    const path = document.getElementById('edit-app-path').value.trim();
    if (!path) {
      toast('Thiếu đường dẫn', 'Vui lòng nhập đường dẫn file hoặc URL.', 'error');
      return;
    }
    try {
      state = await rpc('saveApp', {
        id: document.getElementById('edit-app-id').value || '',
        profileId: state.activeProfileId,
        name: document.getElementById('edit-app-name').value.trim(),
        path,
        arguments: document.getElementById('edit-app-args').value.trim(),
        workingDirectory: document.getElementById('edit-app-workdir').value.trim(),
        delaySeconds: Math.max(0, parseInt(document.getElementById('edit-app-delay').value, 10) || 0),
        windowMode: document.getElementById('edit-app-window-select').dataset.value || 'Normal',
        skipIfAlreadyRunning: document.getElementById('edit-app-skip').checked,
        runAsAdmin: document.getElementById('edit-app-admin').checked,
        enabled: true
      });
      closeAppModal();
      renderAll();
      toast('Đã lưu ứng dụng', '', 'success');
    } catch (err) {
      toast('Lỗi lưu ứng dụng', err.message, 'error');
    }
  });

  // Installed Apps Picker Modal
  document.getElementById('btn-add-installed').addEventListener('click', async () => {
    selectedInstalled.clear();
    document.getElementById('picker-selected-count').textContent = 'Đã chọn 0';
    document.getElementById('picker-search').value = '';
    document.getElementById('picker-list').innerHTML = `<div class="empty-state" style="padding:24px"><div class="empty-desc">Đang quét ứng dụng trên máy...</div></div>`;
    document.getElementById('modal-installed-picker').classList.remove('hidden');

    try {
      installedCache = await rpc('scanInstalledApps') || [];
      renderPickerList();
      loadPickerIconsAsync(installedCache);
    } catch (err) {
      document.getElementById('picker-list').innerHTML = `<div class="empty-state" style="padding:24px"><div class="empty-desc">Không thể quét danh sách ứng dụng: ${escapeHtml(err.message)}</div></div>`;
    }
  });

  async function loadPickerIconsAsync(items) {
    const missing = items.filter((i) => !i.iconDataUrl).map((i) => i.filePath);
    for (let i = 0; i < missing.length; i += 35) {
      if (document.getElementById('modal-installed-picker').classList.contains('hidden')) return;
      const chunk = missing.slice(i, i + 35);
      try {
        const iconMap = await rpc('getAppIcons', { paths: chunk });
        if (!iconMap) continue;
        for (const item of installedCache) {
          if (iconMap[item.filePath]) item.iconDataUrl = iconMap[item.filePath];
        }
        document.querySelectorAll('#picker-list .picker-item').forEach((row) => {
          const p = row.dataset.path;
          if (iconMap[p]) {
            const box = row.querySelector('.app-icon-box');
            if (box) box.innerHTML = `<img src="${iconMap[p]}" alt="" />`;
          }
        });
      } catch { /* ignore */ }
    }
  }

  document.getElementById('picker-search').addEventListener('input', renderPickerList);
  function renderPickerList() {
    const q = (document.getElementById('picker-search').value || '').trim().toLowerCase();
    const list = installedCache.filter((i) => !q || i.name.toLowerCase().includes(q) || i.filePath.toLowerCase().includes(q));
    const el = document.getElementById('picker-list');
    if (list.length === 0) {
      el.innerHTML = `<div class="empty-state" style="padding:24px"><div class="empty-desc">Không tìm thấy ứng dụng phù hợp.</div></div>`;
      return;
    }
    el.innerHTML = list.map((item) => {
      const isSel = selectedInstalled.has(item.filePath);
      const iconHtml = item.iconDataUrl
        ? `<img src="${item.iconDataUrl}" alt="" />`
        : `<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#a1a1aa" stroke-width="1.8"><rect x="3" y="3" width="18" height="18" rx="3"/></svg>`;
      return `
        <div class="picker-item ${isSel ? 'selected' : ''}" data-path="${escapeHtml(item.filePath)}" data-name="${escapeHtml(item.name)}">
          <div class="app-icon-box">${iconHtml}</div>
          <div class="app-main">
            <div class="app-name">${escapeHtml(item.name)}</div>
            <div class="app-path">${escapeHtml(item.filePath)}</div>
          </div>
          <span class="pill pill-neutral">${escapeHtml(item.sourceLabel)}</span>
        </div>
      `;
    }).join('');
  }

  document.getElementById('picker-list').addEventListener('click', (e) => {
    const item = e.target.closest('.picker-item');
    if (!item) return;
    const p = item.dataset.path;
    if (selectedInstalled.has(p)) selectedInstalled.delete(p);
    else selectedInstalled.add(p);
    item.classList.toggle('selected', selectedInstalled.has(p));
    document.getElementById('picker-selected-count').textContent = `Đã chọn ${selectedInstalled.size}`;
  });

  function closePickerModal() { document.getElementById('modal-installed-picker').classList.add('hidden'); }
  document.getElementById('btn-close-picker-modal').addEventListener('click', closePickerModal);
  document.getElementById('btn-cancel-picker-modal').addEventListener('click', closePickerModal);
  document.getElementById('btn-confirm-picker-modal').addEventListener('click', async () => {
    if (selectedInstalled.size === 0) return;
    const toAdd = installedCache.filter((i) => selectedInstalled.has(i.filePath)).map((i) => ({ name: i.name, filePath: i.filePath }));
    state = await rpc('addInstalledApps', toAdd);
    closePickerModal();
    renderAll();
    toast(`Đã thêm ${toAdd.length} ứng dụng`, '', 'success');
  });

  // Webhooks Modal & Actions
  document.getElementById('btn-add-webhook').addEventListener('click', () => {
    document.getElementById('wh-name').value = '';
    document.getElementById('wh-url').value = '';
    setCustomSelectValue('wh-type-select', 'Discord');
    document.getElementById('modal-webhook').classList.remove('hidden');
  });
  document.getElementById('btn-close-wh-modal').addEventListener('click', () => document.getElementById('modal-webhook').classList.add('hidden'));
  document.getElementById('btn-cancel-wh-modal').addEventListener('click', () => document.getElementById('modal-webhook').classList.add('hidden'));
  document.getElementById('btn-save-wh-modal').addEventListener('click', async () => {
    try {
      state = await rpc('saveWebhook', {
        name: document.getElementById('wh-name').value.trim(),
        channelType: document.getElementById('wh-type-select').dataset.value || 'Discord',
        url: document.getElementById('wh-url').value.trim(),
        notifyOnStartup: document.getElementById('wh-startup').checked,
        notifyOnShutdown: document.getElementById('wh-shutdown').checked,
        enabled: true
      });
      document.getElementById('modal-webhook').classList.add('hidden');
      renderWebhooks();
      toast('Đã thêm Webhook', '', 'success');
    } catch (err) {
      toast('Lỗi lưu Webhook', err.message, 'error');
    }
  });

  document.getElementById('webhooks-list').addEventListener('click', async (e) => {
    const row = e.target.closest('.webhook-row');
    const btn = e.target.closest('button[data-wh]');
    if (!row || !btn) return;
    const id = row.dataset.wid;
    if (btn.dataset.wh === 'delete') {
      state = await rpc('deleteWebhook', { id });
      renderWebhooks();
    } else if (btn.dataset.wh === 'test') {
      try {
        const res = await rpc('testWebhook', { id });
        toast(res.ok ? 'Webhook OK' : 'Webhook lỗi', res.detail, res.ok ? 'success' : 'error');
      } catch (err) {
        toast('Lỗi Webhook', err.message, 'error');
      }
    }
  });

  // Telegram Controls
  initSegmented('startup-mode-segmented');
  initSegmented('log-filter-segmented', (val) => {
    logFilter = val;
    renderLogs();
  });

  document.getElementById('btn-toggle-token').addEventListener('click', () => {
    const inp = document.getElementById('tg-token');
    const btn = document.getElementById('btn-toggle-token');
    const isPass = inp.type === 'password';
    inp.type = isPass ? 'text' : 'password';
    btn.textContent = isPass ? 'Ẩn' : 'Hiện';
  });

  document.getElementById('btn-test-bot').addEventListener('click', async () => {
    const badge = document.getElementById('bot-verify-badge');
    badge.className = 'inline-feedback';
    badge.textContent = 'Đang kiểm tra kết nối...';
    badge.classList.remove('hidden');

    try {
      const info = await rpc('testBot', { token: document.getElementById('tg-token').value.trim() });
      if (info.success) {
        badge.className = 'inline-feedback ok';
        badge.textContent = `Kết nối thành công: ${info.firstName} (@${info.username})`;
      } else {
        badge.className = 'inline-feedback err';
        badge.textContent = `Lỗi: ${info.errorMessage}`;
      }
    } catch (err) {
      badge.className = 'inline-feedback err';
      badge.textContent = err.message;
    }
  });

  document.getElementById('btn-discover-chats').addEventListener('click', async () => {
    const wrap = document.getElementById('discovered-chats-wrap');
    try {
      const chats = await rpc('discoverChats', { token: document.getElementById('tg-token').value.trim() });
      if (!chats || chats.length === 0) {
        toast('Chưa có tin nhắn gần đây', 'Hãy nhắn /start cho Bot trên Telegram rồi bấm lại.', 'info');
        return;
      }
      wrap.classList.remove('hidden');
      wrap.innerHTML = chats.map((c) => `
        <button type="button" class="discovered-chip" data-id="${escapeHtml(c.chatId)}">
          + ${escapeHtml(c.title)} (${escapeHtml(c.chatId)})
        </button>
      `).join('');
    } catch (err) {
      toast('Không tìm được Chat ID', err.message, 'error');
    }
  });

  document.getElementById('discovered-chats-wrap').addEventListener('click', (e) => {
    const chip = e.target.closest('.discovered-chip');
    if (!chip) return;
    const id = chip.dataset.id;
    const inp = document.getElementById('tg-chat-ids');
    const existing = inp.value.split(',').map((s) => s.trim()).filter(Boolean);
    if (!existing.includes(id)) {
      existing.push(id);
      inp.value = existing.join(', ');
      toast('Đã thêm Chat ID', id, 'success');
    }
  });

  document.querySelectorAll('.chips-row').forEach((row) => {
    row.addEventListener('click', (e) => {
      const chip = e.target.closest('.chip');
      if (!chip) return;
      const ta = document.getElementById(row.dataset.target);
      const start = ta.selectionStart ?? ta.value.length;
      const end = ta.selectionEnd ?? ta.value.length;
      ta.value = ta.value.slice(0, start) + chip.dataset.var + ta.value.slice(end);
      ta.focus();
      ta.selectionStart = ta.selectionEnd = start + chip.dataset.var.length;
      updatePreviews();
    });
  });

  document.getElementById('tg-startup-template').addEventListener('input', updatePreviews);
  document.getElementById('tg-shutdown-template').addEventListener('input', updatePreviews);

  document.getElementById('btn-save-telegram').addEventListener('click', async () => {
    try {
      state = await rpc('saveTelegram', collectTelegramPayload());
      renderAll();
      toast('Đã lưu cấu hình Thông báo', 'Bot Token được mã hóa DPAPI.', 'success');
    } catch (err) {
      toast('Lỗi lưu cấu hình', err.message, 'error');
    }
  });

  // Logs & Settings
  document.getElementById('btn-open-logs-folder').addEventListener('click', () => rpc('openLogsFolder'));
  document.getElementById('btn-clear-logs').addEventListener('click', async () => {
    state.logs = await rpc('clearLogs');
    renderLogs();
  });

  document.getElementById('btn-save-settings').addEventListener('click', async () => {
    try {
      state = await rpc('saveSettings', {
        autoStartWithWindows: document.getElementById('set-autostart').checked,
        startMinimizedToTray: document.getElementById('set-start-minimized').checked,
        minimizeToTrayOnClose: document.getElementById('set-minimize-close').checked,
        runStartupTasksOnlyWithStartupArg: document.getElementById('set-only-startup-arg').checked,
        startupDelaySeconds: parseInt(document.getElementById('set-startup-delay').value, 10) || 0,
        networkWaitTimeoutSeconds: parseInt(document.getElementById('set-network-wait').value, 10) || 25
      });
      renderAll();
      toast('Đã lưu cài đặt hệ thống', '', 'success');
    } catch (err) {
      toast('Lỗi lưu cài đặt', err.message, 'error');
    }
  });

  document.getElementById('btn-export-config').addEventListener('click', async () => {
    const res = await rpc('exportConfig');
    if (res.exported) toast('Đã xuất cấu hình', 'Bot Token được ẩn để bảo mật.', 'success');
  });
  document.getElementById('btn-import-config').addEventListener('click', async () => {
    try {
      const res = await rpc('importConfig');
      if (res.imported) {
        state = res.state;
        renderAll();
        toast('Đã nhập cấu hình thành công', '', 'success');
      }
    } catch (err) {
      toast('Lỗi nhập cấu hình', err.message, 'error');
    }
  });
  document.getElementById('btn-open-data-folder').addEventListener('click', () => rpc('openDataFolder'));

  // ---- VMware VMs ----
  function renderVmware() {
    const vmw = state.vmware || { vmrunFound: false, resolvedVmrunPath: '', autoStartEnabled: true, stopVmsOnShutdown: false, vms: [] };
    const vms = vmw.vms || [];

    document.getElementById('nav-vms-count').textContent = vms.length;
    document.getElementById('vm-autostart-enabled').checked = !!vmw.autoStartEnabled;
    document.getElementById('vm-stop-shutdown').checked = !!vmw.stopVmsOnShutdown;

    const statusEl = document.getElementById('vm-vmrun-status');
    if (vmw.vmrunFound) {
      statusEl.innerHTML = `<span class="pill pill-emerald" style="margin-right:6px">Sẵn sàng</span> <code>${escapeHtml(vmw.resolvedVmrunPath)}</code>`;
    } else {
      statusEl.innerHTML = `<span class="pill pill-red" style="margin-right:6px">Chưa tìm thấy vmrun.exe</span> Hãy cài VMware Workstation hoặc chọn file vmrun.exe thủ công.`;
    }

    const listEl = document.getElementById('vms-list');
    const emptyEl = document.getElementById('vms-empty');
    emptyEl.classList.toggle('hidden', vms.length > 0);

    listEl.innerHTML = vms.map((vm, idx) => {
      const badges = [
        vm.isRunning
          ? `<span class="pill pill-emerald">Đang chạy</span>`
          : `<span class="pill pill-neutral">Đã tắt</span>`,
        vm.headless
          ? `<span class="pill pill-neutral">Chạy ngầm (nogui)</span>`
          : `<span class="pill pill-amber">Hiện cửa sổ (GUI)</span>`,
        vm.delaySeconds > 0 ? `<span class="pill pill-neutral">+${vm.delaySeconds}s</span>` : '',
        !vm.exists ? `<span class="pill pill-red">Không thấy .vmx</span>` : ''
      ].join('');

      const runBtn = vm.isRunning
        ? `<button class="icon-btn danger" data-vact="stop" title="Tắt êm (soft stop)">■</button>`
        : `<button class="icon-btn" data-vact="start" title="Khởi động ngay">▶</button>`;

      return `
        <div class="app-row" data-id="${escapeHtml(vm.id)}">
          <label class="switch">
            <input type="checkbox" class="vm-toggle" ${vm.enabled ? 'checked' : ''} />
            <span class="slider"></span>
          </label>
          <div class="app-icon-box">
            <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="${vm.isRunning ? '#34d399' : '#a1a1aa'}" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="3" width="20" height="7" rx="2"/><rect x="2" y="14" width="20" height="7" rx="2"/><line x1="6" y1="6.5" x2="6.01" y2="6.5"/><line x1="6" y1="17.5" x2="6.01" y2="17.5"/></svg>
          </div>
          <div class="app-main">
            <div class="app-title-row">
              <span class="app-name">${escapeHtml(vm.name)}</span>
              ${badges}
            </div>
            <div class="app-path">${escapeHtml(vm.vmxPath)}${vm.notes ? ' · ' + escapeHtml(vm.notes) : ''}</div>
          </div>
          <div class="app-actions">
            <button class="icon-btn" data-vact="up" title="Lên" ${idx === 0 ? 'disabled' : ''}>↑</button>
            <button class="icon-btn" data-vact="down" title="Xuống" ${idx === vms.length - 1 ? 'disabled' : ''}>↓</button>
            ${runBtn}
            <button class="icon-btn" data-vact="edit" title="Sửa">✎</button>
            <button class="icon-btn danger" data-vact="delete" title="Xóa">✕</button>
          </div>
        </div>
      `;
    }).join('');
  }

  function openVmModal(vm = null) {
    document.getElementById('modal-vm-title').textContent = vm ? 'Chỉnh sửa máy ảo' : 'Thêm máy ảo VMware';
    document.getElementById('edit-vm-id').value = vm ? vm.id : '';
    document.getElementById('edit-vm-name').value = vm ? vm.name : '';
    document.getElementById('edit-vm-path').value = vm ? vm.vmxPath : '';
    document.getElementById('edit-vm-delay').value = vm ? (vm.delaySeconds || 0) : 0;
    document.getElementById('edit-vm-notes').value = vm ? (vm.notes || '') : '';
    document.getElementById('edit-vm-headless').checked = vm ? !!vm.headless : true;
    document.getElementById('modal-vm-editor').classList.remove('hidden');
  }

  function closeVmModal() {
    document.getElementById('modal-vm-editor').classList.add('hidden');
  }

  document.getElementById('btn-close-vm-modal').addEventListener('click', closeVmModal);
  document.getElementById('btn-cancel-vm-modal').addEventListener('click', closeVmModal);

  document.getElementById('btn-save-vm-modal').addEventListener('click', async () => {
    try {
      const id = document.getElementById('edit-vm-id').value;
      const existing = id ? (state.vmware?.vms || []).find((v) => v.id === id) : null;
      state = await rpc('saveVm', {
        id: id || undefined,
        name: document.getElementById('edit-vm-name').value.trim(),
        vmxPath: document.getElementById('edit-vm-path').value.trim(),
        delaySeconds: parseInt(document.getElementById('edit-vm-delay').value, 10) || 0,
        notes: document.getElementById('edit-vm-notes').value.trim(),
        headless: document.getElementById('edit-vm-headless').checked,
        enabled: existing ? existing.enabled : true
      });
      closeVmModal();
      renderAll();
      toast('Đã lưu cấu hình máy ảo', '', 'success');
    } catch (err) {
      toast('Lỗi lưu máy ảo', err.message, 'error');
    }
  });

  document.getElementById('btn-vm-refresh').addEventListener('click', async () => {
    state = await rpc('refreshVms');
    renderVmware();
    toast('Đã cập nhật trạng thái máy ảo', '', 'info');
  });

  document.getElementById('btn-vm-scan').addEventListener('click', async () => {
    try {
      const res = await rpc('scanVmwareVms');
      state = res.state;
      renderAll();
      if (res.added > 0) {
        toast(`Đã thêm ${res.added} máy ảo mới`, `Tìm thấy tổng ${res.total} máy ảo trong VMware.`, 'success');
      } else if (res.total > 0) {
        toast('Không có máy ảo mới', `Tất cả ${res.total} máy ảo đã có trong danh sách.`, 'info');
      } else {
        toast('Không tìm thấy máy ảo', 'Hãy thử bấm "+ Thêm file .vmx" để chọn thủ công.', 'info');
      }
    } catch (err) {
      toast('Lỗi quét máy ảo', err.message, 'error');
    }
  });

  document.getElementById('btn-vm-browse').addEventListener('click', async () => {
    state = await rpc('browseVmFile');
    renderAll();
  });

  document.getElementById('btn-vm-browse-vmrun').addEventListener('click', async () => {
    state = await rpc('browseVmrunFile');
    renderVmware();
  });

  document.getElementById('btn-vm-run-all').addEventListener('click', async () => {
    toast('Đang khởi động các máy ảo...', 'Vui lòng chờ giây lát.', 'info');
    try {
      const res = await rpc('runAllVmsNow');
      state = res.state;
      renderAll();
      toast('Hoàn tất khởi chạy VM', `Đã bật: ${res.startedCount}, Đang chạy sẵn: ${res.skippedCount}, Lỗi: ${res.failedCount}`, res.failedCount > 0 ? 'error' : 'success');
    } catch (err) {
      toast('Lỗi khởi chạy VM', err.message, 'error');
    }
  });

  async function saveVmSettingsFromUi() {
    state = await rpc('saveVmwareSettings', {
      autoStartEnabled: document.getElementById('vm-autostart-enabled').checked,
      stopVmsOnShutdown: document.getElementById('vm-stop-shutdown').checked,
      vmrunPath: state.vmware?.vmrunPath || ''
    });
    renderVmware();
  }
  document.getElementById('vm-autostart-enabled').addEventListener('change', saveVmSettingsFromUi);
  document.getElementById('vm-stop-shutdown').addEventListener('change', saveVmSettingsFromUi);

  document.getElementById('vms-list').addEventListener('change', async (e) => {
    if (!e.target.classList.contains('vm-toggle')) return;
    const id = e.target.closest('.app-row').dataset.id;
    state = await rpc('toggleVmEnabled', { id, enabled: e.target.checked });
    renderVmware();
  });

  document.getElementById('vms-list').addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-vact]');
    if (!btn) return;
    const row = btn.closest('.app-row');
    const id = row.dataset.id;
    const act = btn.dataset.vact;
    const vms = state.vmware?.vms || [];

    if (act === 'edit') {
      const vm = vms.find((v) => v.id === id);
      if (vm) openVmModal(vm);
    } else if (act === 'delete') {
      state = await rpc('deleteVm', { id });
      renderVmware();
      toast('Đã xóa máy ảo khỏi danh sách', '', 'info');
    } else if (act === 'start') {
      toast('Đang bật máy ảo...', '', 'info');
      try {
        const res = await rpc('startSingleVm', { id });
        state = res.state;
        renderAll();
        toast(res.vmName, res.detail, res.outcome === 'Failed' ? 'error' : 'success');
      } catch (err) {
        toast('Lỗi bật máy ảo', err.message, 'error');
      }
    } else if (act === 'stop') {
      toast('Đang gửi lệnh tắt máy ảo...', '', 'info');
      try {
        const res = await rpc('stopSingleVm', { id });
        state = res.state;
        renderAll();
        toast(res.vmName, res.detail, res.ok ? 'success' : 'error');
      } catch (err) {
        toast('Lỗi tắt máy ảo', err.message, 'error');
      }
    } else if (act === 'up' || act === 'down') {
      const ids = vms.map((v) => v.id);
      const idx = ids.indexOf(id);
      const swapIdx = act === 'up' ? idx - 1 : idx + 1;
      if (idx < 0 || swapIdx < 0 || swapIdx >= ids.length) return;
      [ids[idx], ids[swapIdx]] = [ids[swapIdx], ids[idx]];
      state = await rpc('reorderVms', { orderedIds: ids });
      renderVmware();
    }
  });

  // Boot
  rpc('getState').then((s) => {
    state = s;
    renderAll();
  });
})();
