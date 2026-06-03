// --- APP STATE ---
let appState = {
    vpnState: 'DISCONNECTED', // DISCONNECTED, CONNECTING, CONNECTED, ERROR
    selectedConfig: '',
    configHistory: [], // list of config strings
    externalConfigs: [], // list of { link, latency }
    pingResults: {}, // map of link -> latency
    pingLoading: {}, // map of link -> boolean
    logs: [],
    settings: {
        killSwitch: false,
        autostart: false,
        autoreconnect: true,
        bypassLan: true,
        socksPort: 20808,
        httpPort: 20809,
        vpnMode: false
    },
    downloadProgress: 0,
    downloading: false,
    stats: {
        uploadSpeed: 0,
        downloadSpeed: 0,
        totalUpload: 0,
        totalDownload: 0,
        duration: 0
    },
    exitIp: null // { ip, country, countryCode, city }
};

// --- WEBVIEW2 COMMUNICATION BRIDGE ---
function sendToHost(action, data = {}) {
    if (window.chrome && window.chrome.webview && window.chrome.webview.postMessage) {
        window.chrome.webview.postMessage({ action, ...data });
    } else {
        console.log("Mock sending to host:", action, data);
    }
}

// Receive messages from C# host
if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener('message', event => {
        const msg = event.data;
        handleHostMessage(msg);
    });
}

function handleHostMessage(msg) {
    console.log("Received from host:", msg);
    switch (msg.action) {
        case 'updateState':
            updateVpnState(msg.state);
            break;
        case 'updateStats':
            updateTrafficStats(msg);
            break;
        case 'updateExitIp':
            updateExitIpCard(msg.exitIp);
            break;
        case 'updateHistory':
            appState.configHistory = msg.history || [];
            renderServersList();
            if (appState.configHistory.length === 0) {
                activateTab('tab-external');
            }
            break;
        case 'updateSelectedConfig':
            appState.selectedConfig = msg.link || '';
            updateActiveServerDisplay();
            break;
        case 'updatePing':
            appState.pingResults[msg.link] = msg.latency;
            appState.pingLoading[msg.link] = false;
            updateServerPingBadge(msg.link, msg.latency, false);
            break;
        case 'updatePingLoading':
            appState.pingLoading[msg.link] = msg.loading;
            updateServerPingBadge(msg.link, null, msg.loading);
            break;
        case 'updateExternalConfigs':
            appState.externalConfigs = msg.configs || [];
            renderExternalConfigs();
            break;
        case 'updateExternalStatus':
            document.getElementById('external-status').innerText = msg.statusText;
            break;
        case 'addLog':
            appendLogLine(msg.log);
            break;
        case 'setLogs':
            appState.logs = msg.logs || [];
            renderAllLogs();
            break;
        case 'downloadProgress':
            appState.downloading = msg.downloading;
            appState.downloadProgress = msg.progress;
            updateDownloadOverlay();
            break;
        case 'updateSettings':
            appState.settings = { ...appState.settings, ...msg.settings };
            syncSettingsUi();
            break;
        case 'showToast':
            alert(msg.message); // Simple native alert for feedback
            break;
        case 'updateAvailable':
            showUpdateDialog(msg.version, msg.url);
            break;
    }
}

// --- DOM ELEMENTS ---
const screens = {
    main: document.getElementById('screen-main'),
    settings: document.getElementById('screen-settings')
};

// --- INITIALIZATION ---
document.addEventListener('DOMContentLoaded', () => {
    // Navigations
    document.getElementById('btn-settings').addEventListener('click', () => switchScreen('settings'));
    document.getElementById('btn-settings-back').addEventListener('click', () => switchScreen('main'));

    // Action buttons
    document.getElementById('btn-paste').addEventListener('click', () => sendToHost('pasteFromClipboard'));
    document.getElementById('btn-import-sub').addEventListener('click', openImportDialog);
    document.getElementById('btn-connect').addEventListener('click', toggleConnection);

    // Refresh buttons
    document.getElementById('btn-refresh-pings').addEventListener('click', () => sendToHost('checkAllPings'));
    document.getElementById('btn-refresh-external').addEventListener('click', () => sendToHost('refreshExternal'));

    // Copy logs
    document.getElementById('btn-copy-logs').addEventListener('click', () => {
        const text = appState.logs.join('\n');
        sendToHost('copyToClipboard', { text });
    });

    // Tabs
    document.querySelectorAll('.tab-btn').forEach(btn => {
        btn.addEventListener('click', (e) => {
            const tabId = e.currentTarget.getAttribute('data-tab');
            switchTab(e.currentTarget, tabId);
        });
    });

    // Dialog buttons
    document.getElementById('btn-dialog-cancel').addEventListener('click', closeImportDialog);
    const dialogInput = document.getElementById('input-sub-url');
    const dialogImportBtn = document.getElementById('btn-dialog-import');
    dialogInput.addEventListener('input', () => {
        dialogImportBtn.disabled = !dialogInput.value.trim();
    });
    dialogImportBtn.addEventListener('click', () => {
        const url = dialogInput.value.trim();
        if (url) {
            sendToHost('importSubscription', { url });
            closeImportDialog();
        }
    });

    // Settings elements
    document.getElementById('chk-kill-switch').addEventListener('change', (e) => {
        sendToHost('saveSetting', { name: 'killSwitch', value: e.target.checked });
    });
    document.getElementById('chk-autostart').addEventListener('change', (e) => {
        sendToHost('saveSetting', { name: 'autostart', value: e.target.checked });
    });
    document.getElementById('chk-autoreconnect').addEventListener('change', (e) => {
        sendToHost('saveSetting', { name: 'autoreconnect', value: e.target.checked });
    });
    document.getElementById('chk-bypass-lan').addEventListener('change', (e) => {
        sendToHost('saveSetting', { name: 'bypassLan', value: e.target.checked });
    });
    document.getElementById('btn-split-tunnel').addEventListener('click', () => {
        const chk = document.getElementById('chk-bypass-lan');
        chk.checked = !chk.checked;
        sendToHost('saveSetting', { name: 'bypassLan', value: chk.checked });
    });

    // VPN mode toggle
    document.getElementById('chk-vpn-mode').addEventListener('change', (e) => {
        sendToHost('saveSetting', { name: 'vpnMode', value: e.target.checked });
        // Visually indicate system proxy is disabled when VPN mode is on
        updateVpnModeUi(e.target.checked);
    });

    // Port inputs (save on blur to avoid excessive messages)
    document.getElementById('input-socks-port').addEventListener('change', (e) => {
        const val = parseInt(e.target.value);
        if (val >= 1 && val <= 65535) {
            sendToHost('saveSetting', { name: 'socksPort', value: val });
        }
    });
    document.getElementById('input-http-port').addEventListener('change', (e) => {
        const val = parseInt(e.target.value);
        if (val >= 1 && val <= 65535) {
            sendToHost('saveSetting', { name: 'httpPort', value: val });
        }
    });

    // Update dialog buttons
    document.getElementById('btn-update-cancel').addEventListener('click', () => {
        document.getElementById('dialog-update').classList.add('hidden');
    });

    // Request initial state from host
    sendToHost('appReady');
});

// --- SCREEN SWITCHING ---
function switchScreen(screenName) {
    Object.keys(screens).forEach(key => {
        if (key === screenName) {
            screens[key].classList.add('active');
        } else {
            screens[key].classList.remove('active');
        }
    });
}

// --- TABS SYSTEM ---
function switchTab(clickedBtn, tabId) {
    document.querySelectorAll('.tab-btn').forEach(btn => btn.classList.remove('active'));
    document.querySelectorAll('.tab-pane').forEach(pane => pane.classList.remove('active'));

    clickedBtn.classList.add('active');
    document.getElementById(tabId).classList.add('active');

    // Trigger actions when entering specific tabs
    if (tabId === 'tab-external' && appState.externalConfigs.length === 0) {
        sendToHost('refreshExternal');
    }
}

function activateTab(tabId) {
    const btn = document.querySelector(`.tab-btn[data-tab="${tabId}"]`);
    if (btn) {
        switchTab(btn, tabId);
    }
}

// --- DIALOGS ---
function openImportDialog() {
    const dialog = document.getElementById('dialog-overlay-import') || document.getElementById('dialog-import');
    dialog.classList.remove('hidden');
    const input = document.getElementById('input-sub-url');
    input.value = '';
    input.focus();
    document.getElementById('btn-dialog-import').disabled = true;
}

function closeImportDialog() {
    const dialog = document.getElementById('dialog-overlay-import') || document.getElementById('dialog-import');
    dialog.classList.add('hidden');
}

function showUpdateDialog(version, url) {
    const dialog = document.getElementById('dialog-update');
    if (dialog) {
        document.getElementById('update-dialog-text').innerText = `Доступна новая версия приложения: v${version}. Хотите скачать её?`;
        
        const downloadBtn = document.getElementById('btn-update-download');
        const newDownloadBtn = downloadBtn.cloneNode(true);
        downloadBtn.parentNode.replaceChild(newDownloadBtn, downloadBtn);
        
        newDownloadBtn.addEventListener('click', () => {
            sendToHost('openBrowser', { url: url });
            dialog.classList.add('hidden');
        });
        
        dialog.classList.remove('hidden');
    }
}

// --- CONNECTION MANAGER ---
function toggleConnection() {
    if (appState.vpnState === 'CONNECTED') {
        sendToHost('disconnect');
    } else if (appState.vpnState === 'DISCONNECTED' || appState.vpnState === 'ERROR') {
        sendToHost('connect');
    }
}

function updateVpnState(state) {
    appState.vpnState = state;
    const label = document.getElementById('status-label');
    const btn = document.getElementById('btn-connect');

    label.className = 'status-text ' + state.toLowerCase();
    
    switch (state) {
        case 'DISCONNECTED':
            label.innerText = 'Отключено';
            btn.innerText = 'Подключиться';
            btn.className = 'connect-btn';
            hideStatsContainerDetails();
            break;
        case 'CONNECTING':
            label.innerText = 'Подключение...';
            btn.innerText = 'Прервать';
            btn.className = 'connect-btn';
            break;
        case 'CONNECTED':
            label.innerText = 'Подключено';
            btn.innerText = 'Отключиться';
            btn.className = 'connect-btn connected';
            showStatsContainerDetails();
            break;
        case 'ERROR':
            label.innerText = 'Ошибка';
            btn.innerText = 'Подключиться';
            btn.className = 'connect-btn';
            hideStatsContainerDetails();
            break;
    }
}

function showStatsContainerDetails() {
    document.getElementById('stat-total-upload-container').classList.remove('hidden');
    document.getElementById('stat-total-download-container').classList.remove('hidden');
    document.getElementById('connection-duration-card').classList.remove('hidden');
}

function hideStatsContainerDetails() {
    document.getElementById('stat-total-upload-container').classList.add('hidden');
    document.getElementById('stat-total-download-container').classList.add('hidden');
    document.getElementById('connection-duration-card').classList.add('hidden');
    document.getElementById('exit-ip-card').classList.add('hidden');
}

// --- ACTIVE SERVER DISPLAY ---
function updateActiveServerDisplay() {
    const label = document.getElementById('active-server-name');
    if (appState.selectedConfig) {
        label.innerText = getDisplayLabel(appState.selectedConfig);
    } else {
        label.innerText = 'Сервер не выбран';
    }
    // Re-render server list to highlight active configuration
    renderServersList();
}

// --- EXIT IP CARD ---
function updateExitIpCard(info) {
    const card = document.getElementById('exit-ip-card');
    if (info) {
        card.classList.remove('hidden');
        document.getElementById('exit-ip-addr').innerText = info.ip || '127.0.0.1';
        
        const flag = getFlagEmoji(info.countryCode);
        document.getElementById('exit-ip-flag').innerText = flag;
        
        let locText = info.country || 'Unknown Country';
        if (info.city) locText += `, ${info.city}`;
        document.getElementById('exit-ip-loc').innerText = locText;
    } else {
        card.classList.add('hidden');
    }
}

// --- STATISTICS ---
function updateTrafficStats(msg) {
    document.getElementById('stat-upload-speed').innerText = formatSpeed(msg.uploadSpeed);
    document.getElementById('stat-download-speed').innerText = formatSpeed(msg.downloadSpeed);
    document.getElementById('stat-total-upload').innerText = formatBytes(msg.totalUpload);
    document.getElementById('stat-total-download').innerText = formatBytes(msg.totalDownload);
    document.getElementById('stat-duration').innerText = formatDuration(msg.duration);
}

// --- DOWNLOAD OVERLAY ---
function updateDownloadOverlay() {
    const overlay = document.getElementById('overlay-download');
    if (appState.downloading) {
        overlay.classList.remove('hidden');
        document.getElementById('download-progress-fill').style.width = appState.downloadProgress + '%';
        document.getElementById('download-percentage').innerText = appState.downloadProgress + '%';
    } else {
        overlay.classList.add('hidden');
    }
}

// --- SETTINGS SYNC ---
function syncSettingsUi() {
    document.getElementById('chk-kill-switch').checked = appState.settings.killSwitch;
    document.getElementById('chk-autostart').checked = appState.settings.autostart;
    document.getElementById('chk-autoreconnect').checked = appState.settings.autoreconnect;
    document.getElementById('chk-bypass-lan').checked = appState.settings.bypassLan;
    document.getElementById('chk-vpn-mode').checked = appState.settings.vpnMode;
    document.getElementById('input-socks-port').value = appState.settings.socksPort || 20808;
    document.getElementById('input-http-port').value = appState.settings.httpPort || 20809;
    updateVpnModeUi(appState.settings.vpnMode);
}

function updateVpnModeUi(isVpn) {
    const proxyCard = document.getElementById('card-system-proxy');
    if (proxyCard) {
        proxyCard.style.opacity = isVpn ? '0.4' : '1';
        proxyCard.style.pointerEvents = isVpn ? 'none' : 'auto';
    }
}

// --- RENDER LISTS ---
function renderServersList() {
    const list = document.getElementById('servers-list');
    list.innerHTML = '';

    if (appState.configHistory.length === 0) {
        list.innerHTML = '<div class="empty-state">Список серверов пуст</div>';
        return;
    }

    appState.configHistory.forEach(link => {
        const isSelected = (link.trim() === appState.selectedConfig.trim());
        const ping = appState.pingResults[link];
        const loading = appState.pingLoading[link] === true;

        const card = createServerCard(link, isSelected, ping, loading, true);
        list.appendChild(card);
    });
}

function renderExternalConfigs() {
    const list = document.getElementById('external-list');
    list.innerHTML = '';

    if (appState.externalConfigs.length === 0) {
        list.innerHTML = '<div class="empty-state">Нет доступных серверов</div>';
        return;
    }

    appState.externalConfigs.forEach(item => {
        const link = item.link;
        const ping = item.latency;
        const isSelected = (link.trim() === appState.selectedConfig.trim());

        const card = createServerCard(link, isSelected, ping, false, false);
        list.appendChild(card);
    });
}

function createServerCard(link, isSelected, ping, loading, showDelete = true) {
    const card = document.createElement('div');
    card.className = 'server-card' + (isSelected ? ' selected' : '');
    card.addEventListener('click', (e) => {
        // Prevent click when deleting
        if (e.target.closest('.btn-delete-server')) return;
        
        sendToHost('selectConfig', { link });
        if (appState.vpnState !== 'CONNECTED' && appState.vpnState !== 'CONNECTING') {
            sendToHost('connect');
        }
    });

    // Server Icon SVG
    const iconDiv = document.createElement('div');
    iconDiv.className = 'server-icon';
    iconDiv.innerHTML = `
        <svg viewBox="0 0 24 24" width="20" height="20">
            <path fill="currentColor" d="M19,15c-1.66,0-3-1.34-3-3s1.34-3,3-3s3,1.34,3,3S20.66,15,19,15z M12,11c-0.55,0-1,0.45-1,1s0.45,1,1,1s1-0.45,1-1 S12.55,11,12,11z M5,12c0-0.55-0.45-1-1-1s-1,0.45-1,1s0.45,1,1,1S5,12.55,5,12z M12,2C6.48,2,2,6.48,2,12s4.48,10,10,10 s10-4.48,10-10S17.52,2,12,2z M12,20c-4.41,0-8-3.59-8-8s3.59-8,8-8s8,3.59,8,8S16.41,20,12,20z"/>
        </svg>
    `;
    card.appendChild(iconDiv);

    // Details
    const detailsDiv = document.createElement('div');
    detailsDiv.className = 'server-info';
    
    const nameSpan = document.createElement('span');
    nameSpan.className = 'server-name';
    nameSpan.innerText = getDisplayLabel(link);
    detailsDiv.appendChild(nameSpan);

    const metaSpan = document.createElement('span');
    metaSpan.className = 'server-meta';
    metaSpan.innerText = getProtocolHost(link);
    detailsDiv.appendChild(metaSpan);
    card.appendChild(detailsDiv);

    // Ping area
    const pingArea = document.createElement('div');
    pingArea.className = 'server-ping-area';

    if (loading) {
        const spinner = document.createElement('div');
        spinner.className = 'ping-loading-spinner';
        pingArea.appendChild(spinner);
    } else if (ping !== undefined) {
        const badge = document.createElement('span');
        if (ping < 0) {
            badge.className = 'ping-badge high';
            badge.innerText = 'timeout';
        } else {
            badge.className = 'ping-badge' + (ping > 300 ? ' high' : (ping > 150 ? ' medium' : ''));
            badge.innerText = `${ping} ms`;
        }
        pingArea.appendChild(badge);
    } else {
        // Simple clickable text to measure ping on click
        const pingBtn = document.createElement('span');
        pingBtn.className = 'text-link-btn';
        pingBtn.style.fontSize = '11px';
        pingBtn.innerText = 'ping';
        pingBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            sendToHost('checkServerPing', { link });
        });
        pingArea.appendChild(pingBtn);
    }
    card.appendChild(pingArea);

    // Delete Button
    if (showDelete) {
        const delBtn = document.createElement('button');
        delBtn.className = 'btn-delete-server';
        delBtn.title = 'Удалить сервер';
        delBtn.innerHTML = `
            <svg viewBox="0 0 24 24" width="16" height="16">
                <path fill="currentColor" d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"/>
            </svg>
        `;
        delBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (confirm("Удалить этот сервер из списка?")) {
                sendToHost('deleteConfig', { link });
            }
        });
        card.appendChild(delBtn);
    }

    return card;
}

function updateServerPingBadge(link, latency, loading) {
    // Re-render server list to update badges cleanly
    renderServersList();
}

// --- LOGS MANAGEMENT ---
function appendLogLine(line) {
    appState.logs.push(line);
    if (appState.logs.length > 300) {
        appState.logs.shift();
    }
    
    const view = document.getElementById('logs-view');
    const emptyLog = view.querySelector('.empty');
    if (emptyLog) emptyLog.remove();

    const lineDiv = document.createElement('div');
    lineDiv.className = 'log-line';
    lineDiv.innerText = line;
    view.appendChild(lineDiv);
    view.scrollTop = view.scrollHeight;
}

function renderAllLogs() {
    const view = document.getElementById('logs-view');
    view.innerHTML = '';

    if (appState.logs.length === 0) {
        view.innerHTML = '<div class="log-line empty">Логи отсутствуют</div>';
        return;
    }

    appState.logs.forEach(line => {
        const lineDiv = document.createElement('div');
        lineDiv.className = 'log-line';
        lineDiv.innerText = line;
        view.appendChild(lineDiv);
    });
    view.scrollTop = view.scrollHeight;
}

// --- FORMATTER UTILS ---
function getDisplayLabel(link) {
    try {
        let decoded = link;
        if (link.startsWith("vmess://")) {
            const raw = link.replace("vmess://", "");
            const jsonStr = atob(raw);
            const data = JSON.parse(jsonStr);
            return data.ps || data.add || "VMess Server";
        }
        
        const hashIdx = link.indexOf('#');
        if (hashIdx !== -1) {
            return decodeURIComponent(link.substring(hashIdx + 1));
        }

        // Fallback to parsing Host
        const url = new URL(link.replace("naive+", ""));
        return url.hostname;
    } catch (e) {
        return "VPN Server";
    }
}

function getProtocolHost(link) {
    try {
        let protocol = "vless";
        let host = "";

        if (link.startsWith("vmess://")) {
            const raw = link.replace("vmess://", "");
            const jsonStr = atob(raw);
            const data = JSON.parse(jsonStr);
            return `vmess | ${data.add}:${data.port}`;
        }

        if (link.startsWith("vless://")) protocol = "vless";
        else if (link.startsWith("naive+https://")) protocol = "naive";
        else if (link.startsWith("hysteria2://") || link.startsWith("hy2://")) protocol = "hysteria2";

        const cleanLink = link.replace("naive+", "");
        const url = new URL(cleanLink);
        return `${protocol} | ${url.hostname}:${url.port || 443}`;
    } catch (e) {
        return "Unknown Server";
    }
}

function formatBytes(bytes) {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

function formatSpeed(bytesPerSec) {
    return formatBytes(bytesPerSec) + '/s';
}

function formatDuration(sec) {
    const hrs = Math.floor(sec / 3600).toString().padStart(2, '0');
    const mins = Math.floor((sec % 3600) / 60).toString().padStart(2, '0');
    const secs = (sec % 60).toString().padStart(2, '0');
    return `${hrs}:${mins}:${secs}`;
}

function getFlagEmoji(countryCode) {
    if (!countryCode || countryCode.length !== 2) return '🌐';
    const codePoints = countryCode
        .toUpperCase()
        .split('')
        .map(char =>  127397 + char.charCodeAt(0));
    try {
        return String.fromCodePoint(...codePoints);
    } catch (e) {
        return '🌐';
    }
}
