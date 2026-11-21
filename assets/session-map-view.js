(() => {
  if (window !== window.top || location.origin !== "https://dayz-map.ru") return;
  if (!/^\/(chernarus|deer-isle|deadfall|nasdara)\/?$/.test(location.pathname)) return;

  const css = `
    html.companion-map, .companion-map body {
      width: 100%;
      height: 100%;
      overflow: hidden;
      margin: 0;
      background: #1e2428;
    }
    .companion-map body > .container,
    .companion-map .container > .map-container {
      position: fixed !important;
      inset: 0 !important;
      display: block !important;
      width: 100% !important;
      height: 100% !important;
      min-height: 0 !important;
      max-width: none !important;
      margin: 0 !important;
      padding: 0 !important;
      border: 0 !important;
      border-radius: 0 !important;
    }
    .companion-map .container > .map-container {
      display: grid !important;
      grid-template: 29px minmax(0, 1fr) / 34px minmax(0, 1fr);
      background: #22282c !important;
    }
    .companion-map #map { grid-area: 2 / 2; width: 100% !important; height: 100% !important; min-height: 0 !important; }
    .companion-map #map .leaflet-tile-pane { filter: brightness(1.22) saturate(1.1); }
    .companion-map #map .name-label .name-text { color: #f2f5f6 !important; }
    .companion-map :is(.container > .header, .container > .sidebar, .mobile-map-menu-button,
      .mobile-map-shade, .mobile-map-toolbar, .mobile-map-tool-host, .map-panel-toggle,
      .map-assistant, .leaflet-control-container) {
      display: none !important;
    }
    .companion-map #map-axes-container { display: none !important; }
    .companion-map .companion-ruler {
      position: relative;
      overflow: hidden;
      background: #22282c;
      color: #f2f5f6;
      font: 13px/19px "Segoe UI", sans-serif;
      pointer-events: none;
    }
    .companion-map .companion-ruler-x { grid-area: 1 / 2; border-bottom: 1px solid #46514f; }
    .companion-map .companion-ruler-y { grid-area: 2 / 1; border-right: 1px solid #46514f; }
    .companion-map .companion-ruler span { position: absolute; transform: translate(-50%, -50%); white-space: nowrap; }
    .companion-map .companion-ruler-x span { top: 50%; }
    .companion-map .companion-ruler-y span { left: 50%; }
    .companion-map #map .leaflet-overlay-pane path[stroke^="rgba(255, 255, 255"] {
      stroke: #b4c3bc;
      stroke-opacity: .26;
    }
  `;

  function mount() {
    const root = document.documentElement;
    if (!root) return false;
    root.classList.add("companion-map");
    const style = document.createElement("style");
    style.textContent = css;
    root.append(style);
    return true;
  }

  if (!mount()) {
    const observer = new MutationObserver(() => {
      if (mount()) observer.disconnect();
    });
    observer.observe(document, { childList: true });
  }

  function mountRulers(mapElement) {
    const xRuler = document.createElement("div");
    const yRuler = document.createElement("div");
    xRuler.className = "companion-ruler companion-ruler-x";
    yRuler.className = "companion-ruler companion-ruler-y";
    xRuler.setAttribute("aria-hidden", "true");
    yRuler.setAttribute("aria-hidden", "true");
    mapElement.after(xRuler, yRuler);

    let source;
    function syncRulers() {
      const copyLabels = (selector, axis) => Array.from(source?.querySelectorAll(selector) || [], label => {
        const tick = document.createElement("span");
        tick.textContent = label.textContent;
        tick.style[axis] = label.style[axis];
        return tick;
      });
      xRuler.replaceChildren(...copyLabels(".axis-label-x-top", "left"));
      yRuler.replaceChildren(...copyLabels(".axis-label-y-left", "top"));
    }

    const labels = new MutationObserver(syncRulers);
    function watchAxes() {
      const axes = mapElement.querySelector("#map-axes-container");
      if (axes === source) return;
      labels.disconnect();
      source = axes;
      if (source) labels.observe(source, { childList: true });
      syncRulers();
    }
    new MutationObserver(watchAxes).observe(mapElement, { childList: true });
    watchAxes();
  }

  function prepareOverviewMap(attempts = 100) {
    const controller = window.dayzMap;
    const map = controller?.map;
    if (!map || !Number.isFinite(map.getZoom())) {
      if (attempts > 0) setTimeout(() => prepareOverviewMap(attempts - 1), 100);
      return;
    }
    mountRulers(map.getContainer());
    if (typeof controller.getCanvasNameStyle === "function") {
      const getNameStyle = controller.getCanvasNameStyle.bind(controller);
      controller.getCanvasNameStyle = type => {
        const style = getNameStyle(type);
        return { color: "#f2f5f6", size: style.size + (type === "Capital" || type === "City" ? 2 : 0) };
      };
      controller.updateNamesVisibility();
    }
    if (!location.pathname.startsWith("/chernarus")) return;

    // Fill the preview with the southeast coast on first open, then leave
    // panning and position updates to the site's existing map and bridge.
    const area = window.L.latLngBounds(
      controller.gameToLeafletCoords(7300, 2800),
      controller.gameToLeafletCoords(13800, 9300)
    );
    map.setMaxBounds([[0, 0], [32, 32]]);
    map.setView(area.getCenter(), map.getBoundsZoom(area, true), { animate: false });
  }

  document.addEventListener("DOMContentLoaded", () => prepareOverviewMap(), { once: true });
})();
