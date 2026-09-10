// <goji-globe> — three.js globe with real country outlines (world-atlas 110m).
// Attributes:
//   status  "off" | "connecting" | "on"   — dim / blink / locked & lit
//   node    node id (see GOJI_NODES)
//   theme   "dark" (default) | "light"
//   label   "on" (default) — floating country label at the connected node
const GOJI_NODES = {
  nl: { lat: 52.37, lon: 4.9, country: 'Netherlands', city: 'Амстердам', title: 'Нидерланды' },
  de: { lat: 50.11, lon: 8.68, country: 'Germany', city: 'Франкфурт', title: 'Германия' },
  fi: { lat: 60.17, lon: 24.94, country: 'Finland', city: 'Хельсинки', title: 'Финляндия' },
  spb: { lat: 59.94, lon: 30.31, country: 'Russia', city: 'Санкт-Петербург', title: 'Россия' },
  tr: { lat: 41.01, lon: 28.98, country: 'Turkey', city: 'Стамбул', title: 'Турция' },
  us: { lat: 40.71, lon: -74.01, country: 'United States of America', city: 'Нью-Йорк', title: 'США' },
  jp: { lat: 35.68, lon: 139.77, country: 'Japan', city: 'Токио', title: 'Япония' },
  auto: { lat: 60.17, lon: 24.94, country: 'Finland', city: 'Хельсинки', title: 'Финляндия' }
};
const HOME = { lat: 55.75, lon: 37.62 };
// Раньше это были живые CDN-URL (cdn.jsdelivr.net/esm.sh) — глобус (заглавная картинка на
// логине и на главном экране) молча не рисовал береговые линии/границы без интернета или при
// блокировке этих доменов. В Android-версии геометрия целиком офлайновая (assets/geo_globe.json,
// без единого сетевого запроса) — здесь та же идея: three.js/topojson-client/world-atlas лежат
// локально в vendor/ рядом с этим файлом, отдаются тем же виртуальным хостом godji.local, что и
// сам globe.html (см. GlobeHost.xaml.cs → SetVirtualHostNameToFolderMapping).
const ATLAS = './vendor/countries-110m.json';

const THEMES = {
  dark: { ocean: 0x0a201d, oceanOp: 0.9, land: 0x2f6f66, landOp: 0.75, grid: 0x00d4c4, gridOp: 0.07, hi: 0x00e7d4, arc: 0x00e7d4, home: 0x8b7cf6, atmo: 0x00d4c4, dot: 0x4a625d, labelBg: 'rgba(10,20,18,.82)', labelFg: '#EAF4F2', labelBd: 'rgba(0,231,212,.45)' },
  light: { ocean: 0xe7e0cf, oceanOp: 1, land: 0x0f4d45, landOp: 0.55, grid: 0x0f4d45, gridOp: 0.06, hi: 0x00897e, arc: 0xd9714b, home: 0xd9714b, atmo: 0x00a79b, dot: 0xa9a08a, labelBg: 'rgba(255,253,247,.94)', labelFg: '#12312C', labelBd: 'rgba(0,167,155,.5)' }
};

let atlasPromise = null;
const loadAtlas = async () => {
  if (!atlasPromise) {
    atlasPromise = (async () => {
      const [topo, tj] = await Promise.all([
        fetch(ATLAS).then(r => r.json()),
        import('./vendor/topojson-client.js')
      ]);
      return {
        borders: tj.mesh(topo, topo.objects.countries, (a, b) => a !== b),
        coast: tj.mesh(topo, topo.objects.countries, (a, b) => a === b),
        features: tj.feature(topo, topo.objects.countries).features
      };
    })().catch(() => null);
  }
  return atlasPromise;
};

class GojiGlobe extends HTMLElement {
  static get observedAttributes() { return ['status', 'node', 'theme']; }

  connectedCallback() {
    this.style.display = 'block';
    this.style.position = 'absolute';
    this.style.inset = '0';
    this.style.width = '100%';
    this.style.height = '100%';
    if (!this._booted) { this._booted = true; this.boot(); }
  }

  attributeChangedCallback() { if (this._apply) this._apply(); }

  // ── real subscription node (Windows port addition) ────────────
  // Оригинальный компонент знал только фиксированный набор GOJI_NODES по id — здесь
  // подключаем реальный узел из подписки пользователя (lat/lon/country/city/title),
  // country обязательно должен совпадать с properties.name слоя world-atlas (для подсветки
  // полигона), city/title — русские подписи для плавающей метки. Вызывается из C# через
  // CoreWebView2.ExecuteScriptAsync после каждого выбора/переподключения к узлу.
  setNode(data) {
    this._dynamicNode = data;
    this._dynamicNodeChanged = true;
    if (this._apply) this._apply();
  }

  disconnectedCallback() {
    cancelAnimationFrame(this._raf);
    this._ro && this._ro.disconnect();
    this._renderer && this._renderer.dispose();
  }

  size() {
    const r = this.getBoundingClientRect();
    let w = Math.round(r.width), h = Math.round(r.height);
    if (!w || !h) {
      const p = this.parentElement && this.parentElement.getBoundingClientRect();
      if (p) { w = w || Math.round(p.width); h = h || Math.round(p.height); }
    }
    return { w: w || 380, h: h || 260 };
  }

  async boot() {
    let THREE;
    try { THREE = await import('./vendor/three.module.js'); } catch (e) { return; }
    if (!this.isConnected) return;

    const R = 1.28;
    const themeName = () => (this.getAttribute('theme') === 'light' ? 'light' : 'dark');
    let T = THEMES[themeName()];

    const { w, h } = this.size();
    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(38, w / h, 0.1, 100);
    // Камера "подъезжает" ближе при подключении/на связи, крупнее и нагляднее показывая
    // маршрут дом → узел, и плавно отъезжает обратно на общий план при отключении — портировано
    // 1:1 из camDist/targetCamDist в GojiGlobeRenderer.kt (Android), масштаб под здешний
    // радиус сферы (R=1.28 против Android-овского RADIUS=1).
    let camDist = 5.1, targetCamDist = 5.1;
    camera.position.set(0, 0, camDist);

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
    renderer.setSize(w, h);
    Object.assign(renderer.domElement.style, { display: 'block', width: '100%', height: '100%' });
    this.appendChild(renderer.domElement);
    this._renderer = renderer;

    const globe = new THREE.Group();
    scene.add(globe);

    const toVec = (lat, lon, r = R) => {
      const p = (90 - lat) * Math.PI / 180, t = (lon + 180) * Math.PI / 180;
      return new THREE.Vector3(-r * Math.sin(p) * Math.cos(t), r * Math.cos(p), r * Math.sin(p) * Math.sin(t));
    };

    const ocean = new THREE.Mesh(
      new THREE.SphereGeometry(R, 64, 48),
      new THREE.MeshBasicMaterial({ color: T.ocean, transparent: true, opacity: T.oceanOp })
    );
    globe.add(ocean);

    const grid = new THREE.LineSegments(
      new THREE.WireframeGeometry(new THREE.SphereGeometry(R * 1.001, 24, 12)),
      new THREE.LineBasicMaterial({ color: T.grid, transparent: true, opacity: T.gridOp })
    );
    globe.add(grid);

    const atmo = new THREE.Mesh(
      new THREE.SphereGeometry(R * 1.16, 40, 28),
      new THREE.MeshBasicMaterial({ color: T.atmo, transparent: true, opacity: 0.06, side: THREE.BackSide })
    );
    scene.add(atmo);

    // ── real geography ───────────────────────────────────────────
    const lineFromCoords = (lines, color, opacity, r) => {
      const pts = [];
      lines.forEach(line => {
        for (let i = 0; i < line.length - 1; i++) {
          pts.push(toVec(line[i][1], line[i][0], r), toVec(line[i + 1][1], line[i + 1][0], r));
        }
      });
      const g = new THREE.BufferGeometry().setFromPoints(pts);
      return new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color, transparent: true, opacity }));
    };

    let coastLines = null, borderLines = null, highlight = null, atlas = null;
    loadAtlas().then(a => {
      if (!a || !this.isConnected) return;
      atlas = a;
      coastLines = lineFromCoords(a.coast.coordinates, T.land, T.landOp, R * 1.004);
      borderLines = lineFromCoords(a.borders.coordinates, T.land, T.landOp * 0.5, R * 1.004);
      globe.add(coastLines, borderLines);
      this._apply();
    });

    const setHighlight = name => {
      if (highlight) { globe.remove(highlight); highlight.geometry.dispose(); highlight = null; }
      if (!atlas || !name) return;
      const f = atlas.features.find(x => x.properties && x.properties.name === name);
      if (!f) return;
      const polys = f.geometry.type === 'Polygon' ? [f.geometry.coordinates] : f.geometry.coordinates;
      const rings = [];
      polys.forEach(p => p.forEach(ring => rings.push(ring)));
      highlight = lineFromCoords(rings, T.hi, 1, R * 1.012);
      highlight.material.linewidth = 2;
      globe.add(highlight);
    };

    // ── pins ─────────────────────────────────────────────────────
    const pins = {};
    Object.keys(GOJI_NODES).forEach(id => {
      if (id === 'auto') return;
      const m = new THREE.Mesh(
        new THREE.SphereGeometry(0.028, 12, 12),
        new THREE.MeshBasicMaterial({ color: T.dot })
      );
      m.position.copy(toVec(GOJI_NODES[id].lat, GOJI_NODES[id].lon, R * 1.012));
      globe.add(m);
      pins[id] = m;
    });

    const home = new THREE.Mesh(
      new THREE.SphereGeometry(0.032, 14, 14),
      new THREE.MeshBasicMaterial({ color: T.home })
    );
    home.position.copy(toVec(HOME.lat, HOME.lon, R * 1.012));
    globe.add(home);

    // connection marker: solid core + two expanding rings, laid flat on the surface
    const marker = new THREE.Group();
    const core = new THREE.Mesh(
      new THREE.SphereGeometry(0.052, 16, 16),
      new THREE.MeshBasicMaterial({ color: 0xffffff })
    );
    const halo = new THREE.Mesh(
      new THREE.SphereGeometry(0.085, 16, 16),
      new THREE.MeshBasicMaterial({ color: T.hi, transparent: true, opacity: 0.4 })
    );
    const ringGeo = new THREE.RingGeometry(0.09, 0.105, 40);
    const rings = [0, 1].map(() => new THREE.Mesh(
      ringGeo, new THREE.MeshBasicMaterial({ color: T.hi, transparent: true, opacity: 0.7, side: THREE.DoubleSide })
    ));
    marker.add(core, halo, rings[0], rings[1]);
    marker.visible = false;
    globe.add(marker);

    let arc = null, curve = null;
    const buildArc = id => {
      if (arc) { globe.remove(arc); arc.geometry.dispose(); arc = null; }
      const n = this._dynamicNode || GOJI_NODES[id] || GOJI_NODES.auto;
      const a = toVec(HOME.lat, HOME.lon, R * 1.012), b = toVec(n.lat, n.lon, R * 1.012);
      const mid = a.clone().add(b).multiplyScalar(0.5).normalize().multiplyScalar(R * 1.5);
      curve = new THREE.QuadraticBezierCurve3(a, mid, b);
      arc = new THREE.Mesh(
        new THREE.TubeGeometry(curve, 64, 0.011, 8, false),
        new THREE.MeshBasicMaterial({ color: T.arc, transparent: true, opacity: 0.9 })
      );
      globe.add(arc);
    };

    const packet = new THREE.Mesh(
      new THREE.SphereGeometry(0.042, 12, 12),
      new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.95 })
    );
    packet.visible = false;
    globe.add(packet);

    // ── floating label ───────────────────────────────────────────
    const tag = document.createElement('div');
    Object.assign(tag.style, {
      position: 'absolute', left: '0', top: '0', transform: 'translate(-50%,-140%)',
      padding: '5px 9px', borderRadius: '10px', font: '700 11px/1.2 Manrope, system-ui, sans-serif',
      whiteSpace: 'nowrap', pointerEvents: 'none', opacity: '0', transition: 'opacity .3s ease',
      display: 'flex', alignItems: 'center', gap: '6px', backdropFilter: 'blur(8px)'
    });
    this.appendChild(tag);

    let status = 'off', nodeId = 'auto', targetY = 0, targetX = -0.2, t = 0, locked = false;

    this._apply = () => {
      const nt = THEMES[themeName()];
      if (nt !== T) {
        T = nt;
        ocean.material.color.setHex(T.ocean); ocean.material.opacity = T.oceanOp;
        grid.material.color.setHex(T.grid); grid.material.opacity = T.gridOp;
        atmo.material.color.setHex(T.atmo);
        if (coastLines) { coastLines.material.color.setHex(T.land); coastLines.material.opacity = T.landOp; }
        if (borderLines) { borderLines.material.color.setHex(T.land); borderLines.material.opacity = T.landOp * 0.5; }
        home.material.color.setHex(T.home);
        halo.material.color.setHex(T.hi); rings.forEach(r => r.material.color.setHex(T.hi));
        if (arc) arc.material.color.setHex(T.arc);
      }
      const prevStatus = status;
      status = this.getAttribute('status') || 'off';
      const n = this.getAttribute('node') || 'auto';
      if (n !== nodeId || !arc || this._dynamicNodeChanged) { nodeId = n; buildArc(nodeId); this._dynamicNodeChanged = false; }
      const nd = this._dynamicNode || GOJI_NODES[nodeId] || GOJI_NODES.auto;

      Object.keys(pins).forEach(id => {
        const active = id === nodeId || (nodeId === 'auto' && id === 'fi');
        pins[id].visible = !(active && status !== 'off');
        pins[id].material.color.setHex(T.dot);
      });

      const at = toVec(nd.lat, nd.lon, R * 1.014);
      marker.position.copy(at);
      marker.lookAt(at.clone().multiplyScalar(2));
      marker.visible = status !== 'off';
      setHighlight(status === 'off' ? null : nd.country);

      // frame the node (biased toward it, home still in view) and lock once connected
      const framing = at.clone().multiplyScalar(0.72)
        .add(toVec(HOME.lat, HOME.lon).multiplyScalar(0.28)).normalize();
      targetY = -Math.atan2(framing.x, framing.z);
      const y0 = framing.y, z0 = Math.hypot(framing.x, framing.z);
      targetX = Math.atan2(y0, z0) - 0.18;
      if (status !== 'on') locked = false;
      if (prevStatus !== status && status === 'off') { tag.style.opacity = '0'; }

      tag.innerHTML = '<span style="width:6px;height:6px;border-radius:50%;background:' +
        (status === 'on' ? '#00E7D4' : '#E8B84B') + '"></span>' + nd.title + ' · ' + nd.city;
      tag.style.background = T.labelBg;
      tag.style.color = T.labelFg;
      tag.style.border = '1px solid ' + T.labelBd;
    };
    this._apply();
    globe.rotation.y = targetY;
    globe.rotation.x = targetX;

    const v = new THREE.Vector3();
    let lastW = 0, lastH = 0, frame = 0;
    const fit = () => {
      const s = this.size();
      if (!s.w || !s.h || (s.w === lastW && s.h === lastH)) return;
      lastW = s.w; lastH = s.h;
      camera.aspect = s.w / s.h; camera.updateProjectionMatrix(); renderer.setSize(s.w, s.h);
    };
    const loop = () => {
      this._raf = requestAnimationFrame(loop);
      if ((frame++ % 10) === 0) fit();
      t += 0.016;
      const on = status === 'on', connecting = status === 'connecting';

      if (on) {
        const dy = targetY - globe.rotation.y, dx = targetX - globe.rotation.x;
        if (Math.abs(dy) < 0.002 && Math.abs(dx) < 0.002) { locked = true; }
        if (!locked) { globe.rotation.y += dy * 0.06; globe.rotation.x += dx * 0.06; }
      } else if (connecting) {
        globe.rotation.y += (targetY - globe.rotation.y) * 0.05 + 0.001;
        globe.rotation.x += (targetX - globe.rotation.x) * 0.05;
      } else {
        globe.rotation.y += 0.0013;
        globe.rotation.x += (-0.16 - globe.rotation.x) * 0.02;
      }

      targetCamDist = on ? 2.94 : connecting ? 3.52 : 5.1;
      camDist += (targetCamDist - camDist) * 0.045;
      camera.position.set(0, 0, camDist);

      if (arc) arc.material.opacity = on ? 0.9 : connecting ? 0.3 + Math.sin(t * 5) * 0.22 : 0.05;
      if (curve) {
        packet.visible = on || connecting;
        const p = (t * (on ? 0.4 : 0.22)) % 1;
        packet.position.copy(curve.getPoint(p));
        packet.material.opacity = 0.3 + Math.sin(p * Math.PI) * 0.65;
      }
      rings.forEach((r, i) => {
        const p = ((t * 0.55) + i * 0.5) % 1;
        r.scale.setScalar(0.6 + p * 1.9);
        r.material.opacity = (on ? 0.75 : 0.5) * (1 - p);
      });
      halo.material.opacity = (on ? 0.42 : 0.25) + Math.sin(t * 2.4) * 0.08;
      if (highlight) highlight.material.opacity = on ? 1 : 0.45 + Math.sin(t * 4) * 0.25;
      atmo.material.opacity = on ? 0.09 : 0.05;

      // label follows the marker, hidden when it swings behind the globe
      if (marker.visible) {
        v.copy(marker.position).applyMatrix4(globe.matrixWorld);
        const front = v.clone().normalize().dot(camera.position.clone().normalize()) > 0.16;
        const s = this.size();
        v.project(camera);
        tag.style.left = ((v.x * 0.5 + 0.5) * s.w) + 'px';
        tag.style.top = ((-v.y * 0.5 + 0.5) * s.h) + 'px';
        tag.style.opacity = front ? '1' : '0';
      } else tag.style.opacity = '0';

      renderer.render(scene, camera);
    };
    globe.updateMatrixWorld();
    loop();

    const resize = () => fit();
    resize();
    this._ro = new ResizeObserver(resize);
    this._ro.observe(this);
    if (this.parentElement) this._ro.observe(this.parentElement);
  }
}
if (!customElements.get('goji-globe')) customElements.define('goji-globe', GojiGlobe);
