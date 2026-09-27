
  entInit(cells) {
    this.cells = cells || 1;
    this.mouse = [0, 0, 0];
    this.ent = { w: [1, 0, 0, 0, 0], target: 1, charge: 0, spike: 0, spikeV: 0, absorb: 0, snapUntil: 0,
                 gulp: 0, gulpV: 0, mx: 0, my: 0, mz: 0, rot: 0, shock: 0, t: 0 };
    this.timers = [];
    this.setCanvas = (el) => this.mountGL(el);
  }
  vivid() { return this.props.intensidad === 'Viva'; }
  palette() {
    return this.vivid() ? ['#00FF66', '#00E5FF', '#D000FF', '#00FFAA', '#FFB700']
                        : ['#B4E3C8', '#B2E4EC', '#DDB3EE', '#B3E6D6', '#EDD5A6'];
  }
  marks() {
    return this.vivid() ? ['#00C853', '#00B8CC', '#A800CC', '#00C98A', '#D99800']
                        : ['#5E9E7C', '#5A9AA8', '#9A6BB0', '#5C9C88', '#A98A4E'];
  }
  dark() { return this.props.fondo === 'Oscuro'; }
  setEntity(n) { this.ent.target = n; }
  gulp(force) { this.ent.gulpV = force || 7; this.ent.snapUntil = performance.now() + 420; }
  shock() { this.ent.shock = 0.001; }
  mountGL(el) {
    if (!el || el === this.canvas) return;
    this.canvas = el;
    const gl = el.getContext('webgl2', { antialias: false, alpha: false });
    if (!gl) return;
    const mk = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s);
      if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) console.warn(gl.getShaderInfoLog(s)); return s; };
    const prog = gl.createProgram();
    gl.attachShader(prog, mk(gl.VERTEX_SHADER, '#version 300 es\nin vec2 a;void main(){gl_Position=vec4(a,0.,1.);}'));
    gl.attachShader(prog, mk(gl.FRAGMENT_SHADER, SHADER_SRC));
    gl.bindAttribLocation(prog, 0, 'a');
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { console.warn(gl.getProgramInfoLog(prog)); return; }
    gl.useProgram(prog);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, 1, 1]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    const U = (n) => gl.getUniformLocation(prog, n);
    const u = { res: U('uRes'), time: U('uTime'), cells: U('uCells'), w: U('uW'), fx: U('uFx'), mouse: U('uMouse'),
                rot: U('uRot'), shock: U('uShock'), bg: U('uBg'), dark: U('uDark'), pal: U('uPal'), amt: U('uGlowAmt') };
    const hex = (h) => [parseInt(h.slice(1, 3), 16) / 255, parseInt(h.slice(3, 5), 16) / 255, parseInt(h.slice(5, 7), 16) / 255];
    const reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.visible = true;
    let last = performance.now(), lastDraw = 0, running = false;
    const loop = (now) => {
      if (!this.visible || document.hidden) { running = false; return; }                 // 0 fps hidden/off-screen
      this.raf = requestAnimationFrame(loop);
      const e = this.ent, dt = Math.min((now - last) / 1000, 0.05); last = now;
      const tg = this.entTargets();
      const ease = (c, x, k) => c + (x - c) * (1 - Math.exp(-dt * k));
      for (let i = 0; i < 5; i++) e.w[i] = ease(e.w[i], i === e.target - 1 ? 1 : 0, 3.2);
      e.charge = ease(e.charge, tg.charge, 8);
      const snapping = now < e.snapUntil;
      e.absorb = ease(e.absorb, snapping ? 1 : tg.absorb, snapping ? 22 : (tg.absorb > e.absorb ? 6 : 1.6));
      e.spikeV += (110 * (tg.spike - e.spike) - 12 * e.spikeV) * dt; e.spike += e.spikeV * dt;
      e.gulpV += (-150 * e.gulp - 9 * e.gulpV) * dt; e.gulp += e.gulpV * dt;
      e.mx = ease(e.mx, this.mouse[0], 9); e.my = ease(e.my, this.mouse[1], 9); e.mz = ease(e.mz, this.mouse[2], 5);
      e.rot += dt * tg.rot;
      if (e.shock > 0) { e.shock += dt * 1.1; if (e.shock >= 1) e.shock = 0; }
      e.t += dt * (reduce ? 0.25 : tg.speed);
      const settling = Math.abs(e.spikeV) + Math.abs(e.gulpV) + Math.abs(e.w[e.target - 1] - 1) > 0.01 || e.shock > 0;
      if (tg.calm && !settling && now - lastDraw < 32) return;                              // 30 fps at rest
      lastDraw = now;
      const q = 0.85 * Math.min(window.devicePixelRatio || 1, 2);
      const w = Math.round(el.clientWidth * q), h = Math.round(el.clientHeight * q);
      if (!w || !h) return;
      if (el.width !== w || el.height !== h) { el.width = w; el.height = h; }
      const dk = this.dark();
      gl.viewport(0, 0, w, h);
      gl.uniform2f(u.res, w, h);
      gl.uniform1f(u.time, e.t);
      gl.uniform1f(u.cells, this.cells);
      gl.uniform1fv(u.w, e.w);
      gl.uniform4f(u.fx, e.charge, Math.max(e.spike, 0), e.absorb, Math.max(e.gulp, -0.3));
      gl.uniform3f(u.mouse, e.mx, e.my, e.mz);
      gl.uniform1f(u.rot, e.rot);
      gl.uniform1f(u.shock, e.shock);
      gl.uniform3fv(u.bg, dk ? [6 / 255, 6 / 255, 7 / 255] : [238 / 255, 239 / 255, 236 / 255]);
      gl.uniform1f(u.dark, dk ? 1 : 0);
      gl.uniform3fv(u.pal, this.palette().flatMap(hex));
      gl.uniform1f(u.amt, this.vivid() ? 1.15 : 0.75);
      gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
    };
    const wake = () => { if (!running && this.visible && !document.hidden) { running = true; last = performance.now(); this.raf = requestAnimationFrame(loop); } };
    if (window.IntersectionObserver) {
      this.io = new IntersectionObserver((es) => { this.visible = es[0].isIntersecting; wake(); });
      this.io.observe(el);
    }
    this.onVis = () => wake();
    document.addEventListener('visibilitychange', this.onVis);
    wake();
  }
  trackPointer(ev) {                                   // magnetic field: the cursor anywhere in the window pulls the surface
    if (!this.canvas) return;
    const r = this.canvas.getBoundingClientRect();
    const half = Math.min(r.width, r.height) / 2;
    const x = (ev.clientX - (r.left + r.width / 2)) / half, y = -(ev.clientY - (r.top + r.height / 2)) / half;
    this.mouse = [x, y, 0.45 + 0.55 * Math.exp(-Math.hypot(x, y) / 1.2)];
  }
  releasePointer() { this.mouse = [0, 0, 0]; }

  // ---------- sound: synthesized, quiet ----------
  soundOn() { return (this.props.sonido ?? true) && !(this.state && this.state.muted); }
  audio() {
    if (this.ac) return this.ac;
    const AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    const ac = new AC();
    const comp = ac.createDynamicsCompressor();
    this.master = ac.createGain(); this.master.gain.value = 0.4;
    this.master.connect(comp); comp.connect(ac.destination);
    const buf = ac.createBuffer(1, ac.sampleRate, ac.sampleRate), d = buf.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = Math.random() * 2 - 1;
    this.noiseBuf = buf; this.ac = ac;
    return ac;
  }
  env(g, t, peak, attack, dur) {
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(peak, t + attack);
    g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
  }
  tone(type, f0, f1, t, dur, peak, attack, lp) {
    const ac = this.ac, o = ac.createOscillator(), g = ac.createGain();
    o.type = type; o.frequency.setValueAtTime(f0, t);
    if (f1 && f1 !== f0) o.frequency.exponentialRampToValueAtTime(f1, t + dur);
    this.env(g, t, peak, attack || 0.01, dur);
    let node = o;
    if (lp) { const f = ac.createBiquadFilter(); f.type = 'lowpass'; f.frequency.value = lp; o.connect(f); node = f; }
    node.connect(g); g.connect(this.master);
    o.start(t); o.stop(t + dur + 0.05);
    return o;
  }
  hiss(t, dur, type, f0, f1, q, peak, attack) {
    const ac = this.ac, s = ac.createBufferSource(), f = ac.createBiquadFilter(), g = ac.createGain();
    s.buffer = this.noiseBuf; s.loop = true;
    f.type = type; f.Q.value = q; f.frequency.setValueAtTime(f0, t); f.frequency.exponentialRampToValueAtTime(f1, t + dur);
    this.env(g, t, peak, attack || 0.01, dur);
    s.connect(f); f.connect(g); g.connect(this.master);
    s.start(t); s.stop(t + dur + 0.05);
  }
  sfx(name) {
    if (!this.soundOn()) return;
    const ac = this.audio(); if (!ac) return;
    if (ac.state === 'suspended') ac.resume();
    const t = ac.currentTime + 0.01;
    if (name === 'pulse') {
      this.tone('sine', 56, 44, t, 1.5, 0.08, 0.2);
      this.hiss(t, 0.9, 'lowpass', 170, 90, 0.7, 0.01, 0.2);
    } else if (name === 'absorb') {
      const o = this.tone('sine', 90, 340, t, 0.95, 0.05, 0.08);
      const lfo = ac.createOscillator(), lg = ac.createGain();
      lfo.frequency.value = 18; lg.gain.value = 12; lfo.connect(lg); lg.connect(o.frequency); lfo.start(t); lfo.stop(t + 1);
      this.hiss(t, 0.95, 'bandpass', 300, 2400, 9, 0.035, 0.1);
    } else if (name === 'gulp') {
      this.hiss(t, 0.3, 'bandpass', 2400, 220, 6, 0.08, 0.005);
      this.tone('sine', 180, 50, t + 0.05, 0.35, 0.14, 0.01);
    } else if (name === 'tick') {
      this.tone('sine', 1320, 1320, t, 0.05, 0.012, 0.002);
    } else if (name === 'boost') {
      this.hiss(t, 0.03, 'highpass', 3200, 3000, 0.7, 0.09, 0.001);
      [1, 2.76, 5.4, 8.93].forEach((m, i) => this.tone('sine', 392 * m, 392 * m, t, 1.3 / (1 + i * 0.6), 0.04 / (1 + i), 0.002));
      this.tone('sawtooth', 55, 52, t, 2.6, 0.04, 0.08, 200);
    } else if (name === 'release') {
      this.hiss(t, 1.3, 'lowpass', 1600, 150, 1, 0.06, 0.05);
      this.tone('sine', 330, 110, t, 1.0, 0.03, 0.05);
    } else if (name === 'persist') {
      this.tone('sine', 72, 44, t, 0.45, 0.15, 0.005);
      this.hiss(t, 0.025, 'bandpass', 1200, 1100, 3, 0.07, 0.001);
    }
  }
  entDispose() {
    cancelAnimationFrame(this.raf);
    if (this.io) this.io.disconnect();
    if (this.onVis) document.removeEventListener('visibilitychange', this.onVis);
    (this.timers || []).forEach(clearTimeout);
    clearInterval(this.pulseTimer);
    if (this.ac) this.ac.close();
  }
  later(fn, ms) { this.timers.push(setTimeout(fn, ms)); }
