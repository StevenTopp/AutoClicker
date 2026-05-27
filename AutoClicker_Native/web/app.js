/**
 * 自动点击器前端核心状态与交互逻辑 (C# WebView2 适配版)
 */

// 状态管理
const state = {
    points: [], // 点击位置列表
    isClicking: false, // 是否正在自动点击
    isCapturing: false, // 是否正在捕获坐标
    clickMode: 'sequential' // 多点点击执行模式: sequential / independent
};

// UI 元素缓存
const el = {
    btnCapture: document.getElementById('btn-capture'),
    btnClearAll: document.getElementById('btn-clear-all'),
    btnStart: document.getElementById('btn-start'),
    btnStop: document.getElementById('btn-stop'),
    clickModeSelect: document.getElementById('click-mode-select'),
    pointsCount: document.getElementById('points-count'),
    pointsContainer: document.getElementById('points-list-container'),
    emptyState: document.getElementById('empty-state'),
    statusPulse: document.getElementById('status-pulse'),
    statusBadge: document.getElementById('status-badge'),
    statusBarText: document.getElementById('status-bar-text')
};

// ==========================================================================
// INITIALIZATION
// ==========================================================================

document.addEventListener('DOMContentLoaded', () => {
    // 绑定基础事件监听
    el.btnCapture.addEventListener('click', capturePoint);
    el.btnClearAll.addEventListener('click', clearAllPoints);
    el.btnStart.addEventListener('click', startClicking);
    el.btnStop.addEventListener('click', stopClicking);
    
    el.clickModeSelect.addEventListener('change', (e) => {
        state.clickMode = e.target.value;
        updateStatusBar(`多点点击执行模式已更改为: ${state.clickMode === 'sequential' ? '顺序循环' : '独立并发'}`);
    });

    // 初始化渲染
    renderPoints();
    updateUIStates();

    // 注册 C# WebView2 主机消息接收 (采用循环重试防止异步加载时差)
    function registerWebView2() {
        if (window.chrome && window.chrome.webview) {
            window.chrome.webview.addEventListener('message', event => {
                try {
                    const msg = JSON.parse(event.data);
                    handleHostMessage(msg);
                } catch (e) {
                    console.error("解析主机消息失败:", e);
                }
            });
        } else {
            setTimeout(registerWebView2, 30);
        }
    }
    registerWebView2();
});

// ==========================================================================
// RENDER & UI UPDATES
// ==========================================================================

function renderPoints() {
    // 记录已有卡片的数量以决定是否渲染空状态
    if (state.points.length === 0) {
        el.pointsContainer.innerHTML = '';
        el.pointsContainer.appendChild(el.emptyState);
        el.pointsCount.textContent = '0';
        return;
    }

    // 移除空状态占位
    if (document.getElementById('empty-state')) {
        el.pointsContainer.innerHTML = '';
    }

    el.pointsContainer.innerHTML = '';
    
    state.points.forEach((pt, index) => {
        const card = document.createElement('div');
        card.className = 'point-card';
        card.dataset.id = pt.id;

        // 计算展示坐标
        const coordText = pt.clickMode === 'background' 
            ? `相对 (${pt.x_rel}, ${pt.y_rel})` 
            : `绝对 (${pt.x}, ${pt.y})`;

        card.innerHTML = `
            <div class="point-card-header">
                <div class="point-card-title">
                    <span class="point-number">${index + 1}</span>
                    <span class="window-name" title="${pt.title}">${pt.title}</span>
                    <span class="pos-coordinates">${coordText}</span>
                </div>
                <button class="btn-delete" title="删除此点" onclick="deletePoint(${pt.id})">
                    <svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <polyline points="3 6 5 6 21 6"></polyline>
                        <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                    </svg>
                </button>
            </div>
            <div class="point-card-body">
                <div class="input-item">
                    <label>点击间隔</label>
                    <div class="num-input-wrapper">
                        <input type="number" min="10" step="50" value="${pt.interval}" 
                            onchange="updatePointInterval(${pt.id}, this.value)">
                    </div>
                </div>
                <div class="input-item" style="flex: 0 0 auto;">
                    <div class="mini-select-wrapper">
                        <select onchange="updatePointMode(${pt.id}, this.value)">
                            <option value="background" ${pt.clickMode === 'background' ? 'selected' : ''}>后台消息</option>
                            <option value="active" ${pt.clickMode === 'active' ? 'selected' : ''}>前台物理</option>
                        </select>
                    </div>
                </div>
            </div>
        `;
        el.pointsContainer.appendChild(card);
    });

    el.pointsCount.textContent = state.points.length;
}

function updateUIStates() {
    // 根据自动点击状态更新按钮
    if (state.isClicking) {
        el.btnStart.disabled = true;
        el.btnStop.disabled = false;
        el.btnCapture.disabled = true;
        el.btnClearAll.disabled = true;
        el.clickModeSelect.disabled = true;
        
        // 呼吸灯和状态徽章
        el.statusPulse.className = 'pulse-indicator running';
        el.statusBadge.className = 'card-badge active';
        el.statusBadge.textContent = '运行中';
        updateStatusBar('自动点击任务正在运行中... 按 F9 或点击停止按钮可随时终止。', 'running');
    } else if (state.isCapturing) {
        el.btnStart.disabled = true;
        el.btnStop.disabled = true;
        el.btnCapture.disabled = true;
        el.btnClearAll.disabled = true;
        
        el.statusPulse.className = 'pulse-indicator capturing';
        el.statusBadge.className = 'card-badge';
        el.statusBadge.textContent = '捕获中';
        updateStatusBar('正在捕获：请把鼠标悬停在目标位置，然后按下键盘 [F7] 键。', 'capturing');
    } else {
        el.btnStart.disabled = state.points.length === 0;
        el.btnStop.disabled = true;
        el.btnCapture.disabled = false;
        el.btnClearAll.disabled = state.points.length === 0;
        el.clickModeSelect.disabled = false;
        
        el.statusPulse.className = 'pulse-indicator';
        el.statusBadge.className = 'card-badge';
        el.statusBadge.textContent = '已就绪';
        updateStatusBar('准备就绪。支持多个位置独立配置。');
    }
}

function updateStatusBar(text, statusClass = '') {
    el.statusBarText.textContent = text;
    el.statusBarText.className = 'status-text ' + statusClass;
}

// ==========================================================================
// C# WebView2 MESSAGE BRIDGE SENDER & RECEIVER
// ==========================================================================

function sendToHost(action, data = {}) {
    if (window.chrome && window.chrome.webview) {
        const payload = Object.assign({ action: action }, data);
        window.chrome.webview.postMessage(JSON.stringify(payload));
    } else {
        console.warn("未连接到 C# 容器，动作已被模拟:", action, data);
    }
}

function handleHostMessage(msg) {
    if (!msg || !msg.type) return;

    switch (msg.type) {
        case "captured":
            // 收到 C# 回传的 F7 坐标捕获结果
            state.isCapturing = false;
            if (msg.data) {
                const newPoint = {
                    id: Date.now(),
                    title: msg.data.title || '未知窗口',
                    hwnd: msg.data.hwnd,
                    x: msg.data.x,
                    y: msg.data.y,
                    x_rel: msg.data.x_rel,
                    y_rel: msg.data.y_rel,
                    interval: 500, // 默认 500ms
                    clickMode: 'background' // 默认后台点击
                };
                state.points.push(newPoint);
                updateStatusBar(`成功捕获位置: ${newPoint.title} (${newPoint.x}, ${newPoint.y})`);
            } else {
                updateStatusBar('捕获失败或已被取消。');
            }
            renderPoints();
            updateUIStates();
            break;
            
        case "hotkey_start":
            // 收到 F8 热键启动指令
            if (!state.isClicking && state.points.length > 0 && !state.isCapturing) {
                startClicking();
            }
            break;
            
        case "hotkey_stop":
            // 收到 F9 热键停止指令
            if (state.isClicking) {
                state.isClicking = false;
                updateUIStates();
                updateStatusBar('通过 F9 快捷键终止了自动点击。');
            }
            break;
            
        case "windows":
            // 收到可用窗口列表
            console.log("获取到活动窗口列表:", msg.data);
            break;
    }
}

// ==========================================================================
// ACTIONS & LOGIC
// ==========================================================================

// 1. 捕获坐标
function capturePoint() {
    if (state.isCapturing || state.isClicking) return;
    
    state.isCapturing = true;
    updateUIStates();
    
    // 向 C# 发起捕获请求
    sendToHost("start_capture");
}

// 2. 更改单个坐标的速度/间隔
window.updatePointInterval = function(id, val) {
    const parsedVal = parseInt(val, 10);
    const interval = isNaN(parsedVal) || parsedVal < 10 ? 10 : parsedVal;
    
    const pt = state.points.find(p => p.id === id);
    if (pt) {
        pt.interval = interval;
    }
};

// 3. 更改单个坐标的点击模式 (后台/前台)
window.updatePointMode = function(id, mode) {
    const pt = state.points.find(p => p.id === id);
    if (pt) {
        pt.clickMode = mode;
        renderPoints();
    }
};

// 4. 删除单个点击点
window.deletePoint = function(id) {
    if (state.isClicking) return;
    state.points = state.points.filter(p => p.id !== id);
    renderPoints();
    updateUIStates();
};

// 5. 全部清空
function clearAllPoints() {
    if (state.isClicking) return;
    state.points = [];
    renderPoints();
    updateUIStates();
    updateStatusBar('所有点击位置已被清空。');
}

// 6. 开始自动点击
function startClicking() {
    if (state.isClicking || state.points.length === 0) return;
    
    state.isClicking = true;
    updateUIStates();
    
    // 向 C# 主机发送启动点击请求
    const configsJson = JSON.stringify(state.points);
    sendToHost("start_clicking", {
        configs: configsJson,
        clickMode: state.clickMode
    });
}

// 7. 停止自动点击
function stopClicking() {
    if (!state.isClicking) return;
    
    state.isClicking = false;
    updateUIStates();
    
    // 向 C# 主机发送停止点击请求
    sendToHost("stop_clicking");
    updateStatusBar('已终止自动点击。');
}
