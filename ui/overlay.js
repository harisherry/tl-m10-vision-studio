(function () {
  if (window.__TLM10_OVERLAY_INSTALLED__) return;
  window.__TLM10_OVERLAY_INSTALLED__ = true;

  let state = {
    hudMode: 'side-wings', // 'off', 'floating-pills', 'bottom-bar', 'side-wings', 'cyber-brackets', 'glass-cards', 'full-dashboard'
    hudOpacity: 0.92,
    hudScale: 1.0,
    accentColor: '#00f2fe',
    secondaryColor: '#4facfe',
    showCpu: true,
    showGpu: true,
    showRam: true,
    showNet: true,
    showClock: true,
    tempUnit: 'C',
    bannerText: '',
    vizMode: 'spectrum-bars', // 'off', 'spectrum-bars', 'mirror-eq', 'neon-wave', 'bass-glow'
    vizGain: 1.4,
    vizHeight: 90,
    vizOpacity: 0.85,
    webZoom: 1.0,
    webOffsetX: 0,
    webOffsetY: 0,
    webAutoScroll: 0,
    webBrightness: 100,
    webContrast: 100,
    webSaturate: 100,
    webInvert: false
  };

  let telemetry = {
    cpuTemp: 45,
    cpuUsage: 12,
    cpuClock: 4650,
    cpuPower: 42,
    gpuTemp: 48,
    gpuUsage: 8,
    gpuClock: 1850,
    gpuPower: 55,
    ramUsage: 42,
    ramUsedGb: 13.4,
    netDownKbs: 120,
    netUpKbs: 24,
    spectrum: new Array(64).fill(0),
    bass: 0
  };

  let smoothSpectrum = new Array(64).fill(0);
  let rootHost = null;
  let shadow = null;
  let hudContainer = null;
  let vizCanvas = null;
  let vizCtx = null;
  let autoScrollPos = 0;
  let autoScrollDir = 1;
  let lastHudRenderMs = 0;

  function isBuiltInEnginePage() {
    return location.hostname === 'vision.local' && location.pathname.endsWith('screen.html');
  }

  function ensureOverlayDom() {
    if (rootHost && document.documentElement && document.documentElement.contains(rootHost)) return;
    if (!document.documentElement) return;

    rootHost = document.createElement('div');
    rootHost.id = 'tlm10-vision-overlay-host';
    rootHost.style.cssText = 'position:fixed !important;inset:0 !important;width:100vw !important;height:100vh !important;pointer-events:none !important;z-index:2147483647 !important;overflow:hidden !important;margin:0 !important;padding:0 !important;border:0 !important;background:transparent !important;transform:none !important;zoom:1 !important;';
    shadow = rootHost.attachShadow({ mode: 'open' });

    const style = document.createElement('style');
    style.textContent = `
      * { box-sizing: border-box; margin: 0; padding: 0; user-select: none; font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, sans-serif; }
      :host {
        --accent: #00f2fe;
        --accent2: #4facfe;
        --hud-opacity: 0.92;
        --hud-scale: 1;
      }
      #viz-canvas {
        position: absolute;
        inset: 0;
        width: 100%;
        height: 100%;
        pointer-events: none;
        z-index: 1;
      }
      #hud-layer {
        position: absolute;
        top: 0;
        left: 0;
        width: 1920px;
        height: 462px;
        transform-origin: top left;
        pointer-events: none;
        z-index: 2;
        opacity: var(--hud-opacity);
        color: #f8fafc;
      }
      .pill {
        background: rgba(9, 13, 24, 0.78);
        backdrop-filter: blur(12px);
        -webkit-backdrop-filter: blur(12px);
        border: 1px solid rgba(255, 255, 255, 0.14);
        border-radius: 14px;
        padding: 8px 16px;
        display: flex;
        align-items: center;
        gap: 12px;
        box-shadow: 0 8px 24px rgba(0, 0, 0, 0.45), inset 0 1px 0 rgba(255,255,255,0.1);
      }
      .tag {
        font-size: 11px;
        font-weight: 800;
        letter-spacing: 1.2px;
        text-transform: uppercase;
        padding: 3px 8px;
        border-radius: 6px;
        background: linear-gradient(135deg, var(--accent), var(--accent2));
        color: #040814;
      }
      .val-lg {
        font-size: 22px;
        font-weight: 800;
        font-variant-numeric: tabular-nums;
        letter-spacing: -0.5px;
        color: #ffffff;
        text-shadow: 0 2px 10px rgba(0,242,254,0.35);
      }
      .val-sub {
        font-size: 12px;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
        color: #94a3b8;
      }
      .bar-track {
        width: 70px;
        height: 6px;
        background: rgba(255,255,255,0.12);
        border-radius: 99px;
        overflow: hidden;
      }
      .bar-fill {
        height: 100%;
        background: linear-gradient(90deg, var(--accent), var(--accent2));
        border-radius: 99px;
        transition: width 0.25s ease;
      }

      /* Layout 1: Floating Pills */
      .layout-floating-pills {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        padding: 14px 22px;
        width: 100%;
        height: 100%;
      }
      .pills-group {
        display: flex;
        gap: 12px;
        align-items: center;
        transform: scale(var(--hud-scale));
        transform-origin: top left;
      }
      .pills-group.right {
        transform-origin: top right;
      }

      /* Layout 2: Bottom Status Strip */
      .layout-bottom-bar {
        position: absolute;
        bottom: 0;
        left: 0;
        right: 0;
        height: 54px;
        background: linear-gradient(90deg, rgba(6,10,20,0.92), rgba(12,19,36,0.88), rgba(6,10,20,0.92));
        backdrop-filter: blur(14px);
        border-top: 1px solid rgba(0, 242, 254, 0.35);
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 0 28px;
        box-shadow: 0 -6px 25px rgba(0,0,0,0.5);
      }
      .strip-items {
        display: flex;
        align-items: center;
        gap: 28px;
      }
      .strip-item {
        display: flex;
        align-items: center;
        gap: 10px;
      }
      .strip-divider {
        width: 1px;
        height: 24px;
        background: rgba(255,255,255,0.14);
      }

      /* Layout 3: Side Wings (Left CPU/RAM, Right GPU/Clock, Middle 1350px clear!) */
      .layout-side-wings {
        display: flex;
        justify-content: space-between;
        align-items: center;
        width: 100%;
        height: 100%;
        padding: 16px 20px;
      }
      .wing-card {
        width: 245px;
        background: linear-gradient(145deg, rgba(8, 12, 24, 0.84), rgba(15, 23, 42, 0.72));
        backdrop-filter: blur(16px);
        border: 1px solid rgba(255, 255, 255, 0.14);
        border-left: 3px solid var(--accent);
        border-radius: 16px;
        padding: 14px 16px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        box-shadow: 0 12px 32px rgba(0,0,0,0.55);
        transform: scale(var(--hud-scale));
        transform-origin: center left;
      }
      .wing-card.right {
        border-left: 1px solid rgba(255, 255, 255, 0.14);
        border-right: 3px solid var(--accent2);
        transform-origin: center right;
      }
      .wing-row {
        display: flex;
        justify-content: space-between;
        align-items: baseline;
      }
      .wing-title {
        font-size: 11px;
        font-weight: 800;
        letter-spacing: 1.5px;
        color: var(--accent);
        text-transform: uppercase;
      }

      /* Layout 4: Cyber Brackets */
      .layout-cyber-brackets {
        width: 100%;
        height: 100%;
        padding: 12px 18px;
        display: flex;
        flex-direction: column;
        justify-content: space-between;
      }
      .cyber-top, .cyber-bottom {
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      .cyber-box {
        background: rgba(5, 8, 18, 0.85);
        border: 1px solid var(--accent);
        clip-path: polygon(0 0, calc(100% - 14px) 0, 100% 14px, 100% 100%, 14px 100%, 0 calc(100% - 14px));
        padding: 8px 20px;
        display: flex;
        align-items: center;
        gap: 16px;
        box-shadow: 0 0 18px rgba(0, 242, 254, 0.2);
      }

      /* Layout 5: Glass Cards */
      .layout-glass-cards {
        position: absolute;
        bottom: 14px;
        left: 20px;
        right: 20px;
        display: flex;
        justify-content: center;
        gap: 16px;
        transform: scale(var(--hud-scale));
        transform-origin: bottom center;
      }
      .glass-card {
        min-width: 210px;
        background: rgba(15, 23, 42, 0.72);
        backdrop-filter: blur(16px);
        border: 1px solid rgba(255,255,255,0.16);
        border-radius: 16px;
        padding: 10px 18px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 14px;
        box-shadow: 0 10px 30px rgba(0,0,0,0.45);
      }

      /* Layout 6: Full Hardware Dashboard */
      .layout-full-dashboard {
        width: 100%;
        height: 100%;
        background: radial-gradient(circle at 50% 0%, rgba(0, 242, 254, 0.14), rgba(5, 8, 17, 0.94) 75%);
        backdrop-filter: blur(18px);
        display: grid;
        grid-template-columns: 1.2fr 1.2fr 1fr 1fr;
        gap: 18px;
        padding: 20px 26px;
        align-items: stretch;
      }
      .dash-panel {
        background: rgba(13, 20, 38, 0.85);
        border: 1px solid rgba(255,255,255,0.12);
        border-top: 2px solid var(--accent);
        border-radius: 18px;
        padding: 16px 20px;
        display: flex;
        flex-direction: column;
        justify-content: space-between;
        box-shadow: 0 14px 34px rgba(0,0,0,0.5);
      }
      .dash-big {
        font-size: 46px;
        font-weight: 900;
        letter-spacing: -1.5px;
        line-height: 1;
        background: linear-gradient(135deg, #ffffff, var(--accent));
        -webkit-background-clip: text;
        -webkit-text-fill-color: transparent;
      }
      .dash-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 10px;
        margin-top: 8px;
      }
      .dash-stat {
        background: rgba(255,255,255,0.04);
        border-radius: 10px;
        padding: 8px 10px;
      }
      .dash-stat-label {
        font-size: 10px;
        color: #94a3b8;
        text-transform: uppercase;
        letter-spacing: 0.8px;
      }
      .dash-stat-val {
        font-size: 16px;
        font-weight: 700;
        color: #f8fafc;
        margin-top: 2px;
      }

      /* Banner Text */
      #banner-badge {
        position: absolute;
        top: 14px;
        left: 50%;
        transform: translateX(-50%);
        background: rgba(7, 11, 22, 0.82);
        backdrop-filter: blur(12px);
        border: 1px solid rgba(0, 242, 254, 0.35);
        padding: 6px 22px;
        border-radius: 99px;
        font-size: 14px;
        font-weight: 700;
        letter-spacing: 1.5px;
        color: #ffffff;
        text-shadow: 0 0 12px var(--accent);
        box-shadow: 0 6px 20px rgba(0,0,0,0.4);
        white-space: nowrap;
        z-index: 10;
      }
    `;

    vizCanvas = document.createElement('canvas');
    vizCanvas.id = 'viz-canvas';
    vizCanvas.width = 1920;
    vizCanvas.height = 462;
    vizCtx = vizCanvas.getContext('2d');

    hudContainer = document.createElement('div');
    hudContainer.id = 'hud-layer';

    shadow.appendChild(style);
    shadow.appendChild(vizCanvas);
    shadow.appendChild(hudContainer);
    document.documentElement.appendChild(rootHost);
    updateHudLayerScale();
  }

  function updateHudLayerScale() {
    if (!hudContainer) return;
    const vw = window.innerWidth || 1920;
    const vh = window.innerHeight || 462;
    const sx = vw / 1920;
    const sy = vh / 462;
    hudContainer.style.transform = `scale(${sx.toFixed(4)}, ${sy.toFixed(4)})`;
  }

  window.addEventListener('resize', updateHudLayerScale);

  function formatTemp(c) {
    const val = Math.round(Number(c) || 0);
    if (state.tempUnit === 'F') {
      return Math.round(val * 9 / 5 + 32) + '°F';
    }
    return val + '°C';
  }

  function getClockStrings() {
    const now = new Date();
    const time = now.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
    const date = now.toLocaleDateString([], { weekday: 'short', month: 'short', day: 'numeric' });
    return { time, date };
  }

  function applyWebViewportTransforms() {
    if (isBuiltInEnginePage()) {
      return;
    }

    ensureOverlayDom();
    updateHudLayerScale();

    const ox = Number(state.webOffsetX) || 0;
    const oy = (Number(state.webOffsetY) || 0) + Math.round(autoScrollPos);
    const b = Number(state.webBrightness) || 100;
    const c = Number(state.webContrast) || 100;
    const s = Number(state.webSaturate) || 100;
    const inv = Boolean(state.webInvert);

    let styleEl = document.getElementById('tlm10-web-transform-style');
    if (!styleEl) {
      styleEl = document.createElement('style');
      styleEl.id = 'tlm10-web-transform-style';
      if (document.head) document.head.appendChild(styleEl);
      else if (document.documentElement) document.documentElement.appendChild(styleEl);
    }

    const needsFilter = (b !== 100 || c !== 100 || s !== 100 || inv);
    const filterRule = needsFilter
      ? `filter: brightness(${b}%) contrast(${c}%) saturate(${s}%) ${inv ? 'invert(1) hue-rotate(180deg)' : ''} !important;`
      : '';

    const isYouTubeWatch = location.hostname.includes('youtube.com') && location.pathname.startsWith('/watch');
    const ytCinemaCss = isYouTubeWatch ? `
      #masthead-container, ytd-masthead { display: none !important; }
      #page-manager.ytd-app { margin-top: 0 !important; }
      #player-theater-container, #full-bleed-container { max-height: 100vh !important; height: 100vh !important; }
      video.html5-main-video { object-fit: contain !important; }
    ` : '';

    // Apply both native scroll and CSS transform fallback so 100vh single-page apps also pan cleanly
    let cssTranslateRule = '';
    try {
      window.scrollTo(Math.max(0, ox), Math.max(0, oy));
      const actualY = window.scrollY || document.documentElement.scrollTop || 0;
      const actualX = window.scrollX || document.documentElement.scrollLeft || 0;
      const remY = oy - actualY;
      const remX = ox - actualX;
      if (Math.abs(remY) > 4 || Math.abs(remX) > 4) {
        cssTranslateRule = `
          transform: translate(${-remX}px, ${-remY}px) !important;
          transform-origin: top left !important;
          min-height: calc(100vh + ${Math.max(0, remY)}px) !important;
          min-width: calc(100vw + ${Math.max(0, remX)}px) !important;
        `;
      }
    } catch (e) {
      if (ox !== 0 || oy !== 0) {
        cssTranslateRule = `
          transform: translate(${-ox}px, ${-oy}px) !important;
          transform-origin: top left !important;
          min-height: calc(100vh + ${Math.max(0, oy)}px) !important;
        `;
      }
    }

    styleEl.textContent = `
      ::-webkit-scrollbar { width: 0px !important; height: 0px !important; display: none !important; }
      html { scrollbar-width: none !important; }
      ${ytCinemaCss}
      body {
        ${filterRule}
        ${cssTranslateRule}
      }
    `;
  }

  function renderHud(forceNow = false) {
    const nowMs = performance.now();
    if (!forceNow && nowMs - lastHudRenderMs < 350) {
      return;
    }
    lastHudRenderMs = nowMs;

    ensureOverlayDom();
    if (!hudContainer) return;
    updateHudLayerScale();

    rootHost.style.setProperty('--accent', state.accentColor || '#00f2fe');
    rootHost.style.setProperty('--accent2', state.secondaryColor || '#4facfe');
    rootHost.style.setProperty('--hud-opacity', String(state.hudOpacity ?? 0.92));
    rootHost.style.setProperty('--hud-scale', String(state.hudScale ?? 1.0));

    const { time, date } = getClockStrings();
    const bannerHtml = state.bannerText
      ? `<div id="banner-badge">${escapeHtml(state.bannerText)}</div>`
      : '';

    if (state.hudMode === 'off') {
      if (hudContainer.innerHTML !== bannerHtml) {
        hudContainer.innerHTML = bannerHtml;
      }
      return;
    }

    const cpuTempStr = formatTemp(telemetry.cpuTemp);
    const gpuTempStr = formatTemp(telemetry.gpuTemp);
    const cpuLoad = Math.round(telemetry.cpuUsage || 0);
    const gpuLoad = Math.round(telemetry.gpuUsage || 0);
    const ramLoad = Math.round(telemetry.ramUsage || 0);

    if (state.hudMode === 'floating-pills') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-floating-pills">
          <div class="pills-group">
            ${state.showCpu ? `
              <div class="pill">
                <span class="tag">CPU</span>
                <span class="val-lg">${cpuTempStr}</span>
                <div>
                  <div class="val-sub">${cpuLoad}% · ${Math.round(telemetry.cpuClock)} MHz</div>
                  <div class="bar-track"><div class="bar-fill" style="width:${cpuLoad}%"></div></div>
                </div>
              </div>` : ''}
            ${state.showGpu ? `
              <div class="pill">
                <span class="tag">GPU</span>
                <span class="val-lg">${gpuTempStr}</span>
                <div>
                  <div class="val-sub">${gpuLoad}% · ${Math.round(telemetry.gpuPower)}W</div>
                  <div class="bar-track"><div class="bar-fill" style="width:${gpuLoad}%"></div></div>
                </div>
              </div>` : ''}
          </div>
          <div class="pills-group right">
            ${state.showRam ? `
              <div class="pill">
                <span class="tag">RAM</span>
                <span class="val-lg">${ramLoad}%</span>
                <span class="val-sub">${Number(telemetry.ramUsedGb || 0).toFixed(1)} GB</span>
              </div>` : ''}
            ${state.showClock ? `
              <div class="pill">
                <span class="val-lg">${time}</span>
                <span class="val-sub">${date}</span>
              </div>` : ''}
          </div>
        </div>
      `;
    } else if (state.hudMode === 'bottom-bar') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-bottom-bar">
          <div class="strip-items">
            ${state.showCpu ? `
              <div class="strip-item">
                <span class="tag">CPU</span>
                <span class="val-lg">${cpuTempStr}</span>
                <span class="val-sub">${cpuLoad}% · ${Math.round(telemetry.cpuClock)} MHz · ${Math.round(telemetry.cpuPower)}W</span>
                <div class="bar-track"><div class="bar-fill" style="width:${cpuLoad}%"></div></div>
              </div>` : ''}
            ${state.showGpu ? `
              <div class="strip-divider"></div>
              <div class="strip-item">
                <span class="tag">GPU</span>
                <span class="val-lg">${gpuTempStr}</span>
                <span class="val-sub">${gpuLoad}% · ${Math.round(telemetry.gpuPower)}W</span>
                <div class="bar-track"><div class="bar-fill" style="width:${gpuLoad}%"></div></div>
              </div>` : ''}
            ${state.showRam ? `
              <div class="strip-divider"></div>
              <div class="strip-item">
                <span class="tag">RAM</span>
                <span class="val-lg">${ramLoad}%</span>
                <span class="val-sub">${Number(telemetry.ramUsedGb || 0).toFixed(1)} GB</span>
              </div>` : ''}
            ${state.showNet ? `
              <div class="strip-divider"></div>
              <div class="strip-item">
                <span class="tag">NET</span>
                <span class="val-sub">↓ ${formatNet(telemetry.netDownKbs)} &nbsp; ↑ ${formatNet(telemetry.netUpKbs)}</span>
              </div>` : ''}
          </div>
          ${state.showClock ? `
            <div class="strip-item">
              <span class="val-lg">${time}</span>
              <span class="val-sub">${date}</span>
            </div>` : ''}
        </div>
      `;
    } else if (state.hudMode === 'side-wings') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-side-wings">
          <div class="wing-card">
            ${state.showCpu ? `
              <div class="wing-title">CPU TELEMETRY</div>
              <div class="wing-row">
                <span class="val-lg" style="font-size:30px">${cpuTempStr}</span>
                <span class="val-sub">${cpuLoad}% LOAD</span>
              </div>
              <div class="bar-track" style="width:100%"><div class="bar-fill" style="width:${cpuLoad}%"></div></div>
              <div class="wing-row">
                <span class="val-sub">${Math.round(telemetry.cpuClock)} MHz</span>
                <span class="val-sub">${Math.round(telemetry.cpuPower)} W</span>
              </div>` : ''}
            ${state.showRam ? `
              <div style="height:1px;background:rgba(255,255,255,0.1);margin:4px 0"></div>
              <div class="wing-row">
                <span class="wing-title">MEMORY</span>
                <span class="val-sub">${ramLoad}% (${Number(telemetry.ramUsedGb || 0).toFixed(1)} GB)</span>
              </div>
              <div class="bar-track" style="width:100%"><div class="bar-fill" style="width:${ramLoad}%"></div></div>` : ''}
          </div>

          <div class="wing-card right">
            ${state.showGpu ? `
              <div class="wing-title" style="color:var(--accent2)">GPU TELEMETRY</div>
              <div class="wing-row">
                <span class="val-lg" style="font-size:30px">${gpuTempStr}</span>
                <span class="val-sub">${gpuLoad}% LOAD</span>
              </div>
              <div class="bar-track" style="width:100%"><div class="bar-fill" style="width:${gpuLoad}%"></div></div>
              <div class="wing-row">
                <span class="val-sub">${Math.round(telemetry.gpuClock)} MHz</span>
                <span class="val-sub">${Math.round(telemetry.gpuPower)} W</span>
              </div>` : ''}
            ${state.showClock ? `
              <div style="height:1px;background:rgba(255,255,255,0.1);margin:4px 0"></div>
              <div class="wing-row">
                <span class="val-lg" style="font-size:20px">${time}</span>
                <span class="val-sub">${date}</span>
              </div>` : ''}
          </div>
        </div>
      `;
    } else if (state.hudMode === 'cyber-brackets') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-cyber-brackets">
          <div class="cyber-top">
            ${state.showCpu ? `
              <div class="cyber-box">
                <span class="tag">SYS // CPU</span>
                <span class="val-lg">${cpuTempStr}</span>
                <span class="val-sub">${cpuLoad}% · ${Math.round(telemetry.cpuClock)} MHz · ${Math.round(telemetry.cpuPower)}W</span>
              </div>` : '<div></div>'}
            ${state.showGpu ? `
              <div class="cyber-box">
                <span class="tag">SYS // GPU</span>
                <span class="val-lg">${gpuTempStr}</span>
                <span class="val-sub">${gpuLoad}% · ${Math.round(telemetry.gpuPower)}W</span>
              </div>` : '<div></div>'}
          </div>
          <div class="cyber-bottom">
            ${state.showRam ? `
              <div class="cyber-box">
                <span class="tag">MEM // NET</span>
                <span class="val-sub">RAM ${ramLoad}% (${Number(telemetry.ramUsedGb || 0).toFixed(1)}G) &nbsp;|&nbsp; ↓ ${formatNet(telemetry.netDownKbs)}</span>
              </div>` : '<div></div>'}
            ${state.showClock ? `
              <div class="cyber-box">
                <span class="val-lg">${time}</span>
                <span class="val-sub">${date}</span>
              </div>` : '<div></div>'}
          </div>
        </div>
      `;
    } else if (state.hudMode === 'glass-cards') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-glass-cards">
          ${state.showCpu ? `
            <div class="glass-card">
              <div>
                <div class="wing-title">CPU</div>
                <div class="val-lg">${cpuTempStr}</div>
                <div class="val-sub">${Math.round(telemetry.cpuClock)} MHz</div>
              </div>
              <div style="text-align:right">
                <div class="val-lg" style="color:var(--accent)">${cpuLoad}%</div>
                <div class="val-sub">${Math.round(telemetry.cpuPower)} W</div>
              </div>
            </div>` : ''}
          ${state.showGpu ? `
            <div class="glass-card">
              <div>
                <div class="wing-title" style="color:var(--accent2)">GPU</div>
                <div class="val-lg">${gpuTempStr}</div>
                <div class="val-sub">${Math.round(telemetry.gpuClock)} MHz</div>
              </div>
              <div style="text-align:right">
                <div class="val-lg" style="color:var(--accent2)">${gpuLoad}%</div>
                <div class="val-sub">${Math.round(telemetry.gpuPower)} W</div>
              </div>
            </div>` : ''}
          ${state.showRam ? `
            <div class="glass-card">
              <div>
                <div class="wing-title">MEMORY</div>
                <div class="val-lg">${ramLoad}%</div>
                <div class="val-sub">${Number(telemetry.ramUsedGb || 0).toFixed(1)} GB Used</div>
              </div>
              <div class="bar-track" style="width:65px"><div class="bar-fill" style="width:${ramLoad}%"></div></div>
            </div>` : ''}
          ${state.showClock ? `
            <div class="glass-card">
              <div>
                <div class="wing-title">LOCAL TIME</div>
                <div class="val-lg">${time}</div>
                <div class="val-sub">${date}</div>
              </div>
            </div>` : ''}
        </div>
      `;
    } else if (state.hudMode === 'full-dashboard') {
      hudContainer.innerHTML = `
        ${bannerHtml}
        <div class="layout-full-dashboard">
          <div class="dash-panel">
            <div class="wing-row">
              <span class="tag">PROCESSOR // CPU</span>
              <span class="val-sub">${cpuLoad}% UTILIZATION</span>
            </div>
            <div class="wing-row" style="margin: 10px 0">
              <div class="dash-big">${cpuTempStr}</div>
              <div style="width:45%">
                <div class="bar-track" style="width:100%;height:10px"><div class="bar-fill" style="width:${cpuLoad}%"></div></div>
                <div class="val-sub" style="margin-top:6px;text-align:right">LOAD ${cpuLoad}%</div>
              </div>
            </div>
            <div class="dash-grid">
              <div class="dash-stat">
                <div class="dash-stat-label">Core Clock</div>
                <div class="dash-stat-val">${Math.round(telemetry.cpuClock)} MHz</div>
              </div>
              <div class="dash-stat">
                <div class="dash-stat-label">Package Power</div>
                <div class="dash-stat-val">${Math.round(telemetry.cpuPower)} W</div>
              </div>
            </div>
          </div>

          <div class="dash-panel" style="border-top-color:var(--accent2)">
            <div class="wing-row">
              <span class="tag">GRAPHICS // GPU</span>
              <span class="val-sub">${gpuLoad}% UTILIZATION</span>
            </div>
            <div class="wing-row" style="margin: 10px 0">
              <div class="dash-big">${gpuTempStr}</div>
              <div style="width:45%">
                <div class="bar-track" style="width:100%;height:10px"><div class="bar-fill" style="width:${gpuLoad}%"></div></div>
                <div class="val-sub" style="margin-top:6px;text-align:right">LOAD ${gpuLoad}%</div>
              </div>
            </div>
            <div class="dash-grid">
              <div class="dash-stat">
                <div class="dash-stat-label">GPU Clock</div>
                <div class="dash-stat-val">${Math.round(telemetry.gpuClock)} MHz</div>
              </div>
              <div class="dash-stat">
                <div class="dash-stat-label">GPU Power</div>
                <div class="dash-stat-val">${Math.round(telemetry.gpuPower)} W</div>
              </div>
            </div>
          </div>

          <div class="dash-panel">
            <div class="wing-row">
              <span class="tag">SYSTEM MEMORY</span>
              <span class="val-sub">DDR</span>
            </div>
            <div class="dash-big" style="margin:8px 0">${ramLoad}%</div>
            <div class="bar-track" style="width:100%;height:10px"><div class="bar-fill" style="width:${ramLoad}%"></div></div>
            <div class="dash-grid">
              <div class="dash-stat">
                <div class="dash-stat-label">Used RAM</div>
                <div class="dash-stat-val">${Number(telemetry.ramUsedGb || 0).toFixed(1)} GB</div>
              </div>
              <div class="dash-stat">
                <div class="dash-stat-label">Net Traffic</div>
                <div class="dash-stat-val">↓ ${formatNet(telemetry.netDownKbs)}</div>
              </div>
            </div>
          </div>

          <div class="dash-panel">
            <div class="wing-row">
              <span class="tag">TL-M10 VISION</span>
              <span class="val-sub">1920×462</span>
            </div>
            <div class="dash-big" style="font-size:40px;margin:8px 0">${time}</div>
            <div class="val-sub" style="font-size:15px;color:#e2e8f0">${date}</div>
            <div class="dash-grid">
              <div class="dash-stat">
                <div class="dash-stat-label">Upload</div>
                <div class="dash-stat-val">↑ ${formatNet(telemetry.netUpKbs)}</div>
              </div>
              <div class="dash-stat">
                <div class="dash-stat-label">Status</div>
                <div class="dash-stat-val" style="color:#4ade80">ONLINE</div>
              </div>
            </div>
          </div>
        </div>
      `;
    }
  }

  function formatNet(kbs) {
    const v = Number(kbs) || 0;
    if (v >= 1024) return (v / 1024).toFixed(1) + ' MB/s';
    return Math.round(v) + ' KB/s';
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function renderVisualizer() {
    ensureOverlayDom();
    if (!vizCtx || !vizCanvas) return;

    const w = vizCanvas.width;
    const h = vizCanvas.height;
    vizCtx.clearRect(0, 0, w, h);

    if (state.vizMode === 'off') return;

    const gain = Number(state.vizGain) || 1.4;
    const maxH = Number(state.vizHeight) || 95;
    const n = 64;

    for (let i = 0; i < n; i++) {
      const target = Math.min(1.0, (telemetry.spectrum[i] || 0) * gain);
      smoothSpectrum[i] = smoothSpectrum[i] * 0.68 + target * 0.32;
    }

    vizCtx.save();
    vizCtx.globalAlpha = Number(state.vizOpacity ?? 0.85);

    const grad = vizCtx.createLinearGradient(0, h, w, h - maxH);
    grad.addColorStop(0, state.accentColor || '#00f2fe');
    grad.addColorStop(0.5, state.secondaryColor || '#4facfe');
    grad.addColorStop(1, '#f43f5e');

    if (state.vizMode === 'spectrum-bars') {
      const barW = (w - 40) / n;
      for (let i = 0; i < n; i++) {
        const val = smoothSpectrum[i];
        const bh = Math.max(3, val * maxH);
        const x = 20 + i * barW;
        const y = h - bh - (state.hudMode === 'bottom-bar' ? 54 : 0);
        vizCtx.fillStyle = grad;
        vizCtx.shadowColor = state.accentColor || '#00f2fe';
        vizCtx.shadowBlur = 10;
        vizCtx.beginPath();
        vizCtx.roundRect(x + 2, y, Math.max(4, barW - 5), bh, [4, 4, 0, 0]);
        vizCtx.fill();
      }
    } else if (state.vizMode === 'mirror-eq') {
      const barW = (w - 80) / (n * 2);
      const center = w / 2;
      const baseY = state.hudMode === 'bottom-bar' ? h - 54 : h;
      vizCtx.fillStyle = grad;
      vizCtx.shadowColor = state.accentColor || '#00f2fe';
      vizCtx.shadowBlur = 12;
      for (let i = 0; i < n; i++) {
        const val = smoothSpectrum[i];
        const bh = Math.max(3, val * maxH);
        const xRight = center + i * barW;
        const xLeft = center - (i + 1) * barW;
        vizCtx.fillRect(xRight + 1, baseY - bh, Math.max(3, barW - 2), bh);
        vizCtx.fillRect(xLeft + 1, baseY - bh, Math.max(3, barW - 2), bh);
      }
    } else if (state.vizMode === 'neon-wave') {
      const baseY = h - (state.hudMode === 'bottom-bar' ? 58 : 6);
      vizCtx.beginPath();
      vizCtx.moveTo(0, baseY);
      for (let i = 0; i < n; i++) {
        const x = (i / (n - 1)) * w;
        const y = baseY - smoothSpectrum[i] * maxH;
        if (i === 0) vizCtx.lineTo(x, y);
        else {
          const prevX = ((i - 1) / (n - 1)) * w;
          const prevY = baseY - smoothSpectrum[i - 1] * maxH;
          const cx = (prevX + x) / 2;
          vizCtx.quadraticCurveTo(prevX, prevY, cx, (prevY + y) / 2);
        }
      }
      vizCtx.lineTo(w, baseY);
      vizCtx.closePath();
      vizCtx.fillStyle = grad;
      vizCtx.globalAlpha = 0.35;
      vizCtx.fill();

      vizCtx.globalAlpha = 0.95;
      vizCtx.strokeStyle = state.accentColor || '#00f2fe';
      vizCtx.lineWidth = 3;
      vizCtx.shadowColor = state.accentColor || '#00f2fe';
      vizCtx.shadowBlur = 14;
      vizCtx.stroke();
    } else if (state.vizMode === 'bass-glow') {
      let bassSum = 0;
      for (let i = 0; i < 10; i++) bassSum += smoothSpectrum[i];
      const bass = Math.min(1, bassSum / 6);
      if (bass > 0.02) {
        const borderGrad = vizCtx.createRadialGradient(w / 2, h / 2, h * 0.3, w / 2, h / 2, w * 0.55);
        borderGrad.addColorStop(0, 'rgba(0,0,0,0)');
        borderGrad.addColorStop(1, state.accentColor || '#00f2fe');
        vizCtx.fillStyle = borderGrad;
        vizCtx.globalAlpha = bass * 0.65;
        vizCtx.fillRect(0, 0, w, h);
      }
    }
    vizCtx.restore();
  }

  function tick() {
    if (state.webAutoScroll > 0 && !isBuiltInEnginePage()) {
      autoScrollPos += state.webAutoScroll * 0.45 * autoScrollDir;
      const maxScroll = Math.max(800, (document.documentElement.scrollHeight || 1800) - window.innerHeight);
      if (autoScrollPos >= maxScroll) {
        autoScrollDir = -1;
      } else if (autoScrollPos <= 0) {
        autoScrollPos = 0;
        autoScrollDir = 1;
      }
      applyWebViewportTransforms();
    }
    renderVisualizer();
    requestAnimationFrame(tick);
  }

  window.__TLM10_UPDATE__ = function (payload) {
    if (!payload) return;
    let stateChanged = false;
    if (payload.state) {
      stateChanged = true;
      Object.assign(state, payload.state);
      if (!state.webAutoScroll || state.webAutoScroll === 0) autoScrollPos = 0;
      applyWebViewportTransforms();
      if (typeof window.__TLM10_ON_STATE__ === 'function') {
        window.__TLM10_ON_STATE__(state);
      }
    }
    if (payload.telemetry) {
      Object.assign(telemetry, payload.telemetry);
      if (Array.isArray(payload.spectrum)) {
        telemetry.spectrum = payload.spectrum;
      }
      if (typeof window.__TLM10_ON_TELEMETRY__ === 'function') {
        window.__TLM10_ON_TELEMETRY__(telemetry);
      }
    } else if (Array.isArray(payload.spectrum)) {
      telemetry.spectrum = payload.spectrum;
      if (typeof window.__TLM10_ON_TELEMETRY__ === 'function') {
        window.__TLM10_ON_TELEMETRY__(telemetry);
      }
    }
    renderHud(stateChanged);
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
      ensureOverlayDom();
      applyWebViewportTransforms();
      renderHud(true);
      requestAnimationFrame(tick);
    });
  } else {
    ensureOverlayDom();
    applyWebViewportTransforms();
    renderHud(true);
    requestAnimationFrame(tick);
  }
})();

