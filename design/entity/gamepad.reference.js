
  // ---------- Xbox mode: Gamepad API (Elite 2 works as a standard Xbox pad) ----------
  padInit() {
    const has = Array.from((navigator.getGamepads && navigator.getGamepads()) || []).some(Boolean);
    this.state = Object.assign({}, this.state || {}, { padOn: has, lt: 0, rt: 0 });   // constructor: no setState yet
    this.padPrev = []; this.padRepeat = 0; this.padHeld = false; this.padLT = 0; this.padRT = 0; this.padSurge = 0; this.lastRumble = 0;
    this.setRoot = (el) => { this.root = el; };
    this.onPadConn = () => this.padStart();
    window.addEventListener('gamepadconnected', this.onPadConn);
    window.addEventListener('gamepaddisconnected', this.onPadConn);
    this.padLoop = (now) => {
      const gp = Array.from((navigator.getGamepads && navigator.getGamepads()) || []).find(Boolean);
      if (!gp) { this.padRaf = 0; this.padStart(); return; }
      this.padRaf = requestAnimationFrame(this.padLoop);
      if (document.hidden) return;
      const b = (i) => !!(gp.buttons[i] && gp.buttons[i].pressed);
      const pressed = (i) => b(i) && !this.padPrev[i];
      this.padLT = gp.buttons[6] ? gp.buttons[6].value : 0;
      this.padRT = gp.buttons[7] ? gp.buttons[7].value : 0;
      if (Math.abs(this.padLT - this.state.lt) > 0.03 || Math.abs(this.padRT - this.state.rt) > 0.03) this.setState({ lt: this.padLT, rt: this.padRT });
      const rx = gp.axes[2] || 0, ry = gp.axes[3] || 0, mag = Math.hypot(rx, ry) > 0.18;   // right stick = magnet
      if (mag) this.mouse = [rx * 1.6, -ry * 1.6, 1]; else if (this.padMag) this.mouse = [0, 0, 0];
      this.padMag = mag;
      this.padRumble(gp, now);
      const lx = gp.axes[0] || 0, ly = gp.axes[1] || 0;
      let dx = (b(15) ? 1 : 0) - (b(14) ? 1 : 0), dy = (b(13) ? 1 : 0) - (b(12) ? 1 : 0);
      if (!dx && !dy && Math.hypot(lx, ly) > 0.5) { if (Math.abs(lx) > Math.abs(ly)) dx = Math.sign(lx); else dy = Math.sign(ly); }
      if (dx || dy) { if (now > this.padRepeat) { this.padMove(dx, dy); this.padRepeat = now + (this.padHeld ? 120 : 280); this.padHeld = true; } }
      else { this.padHeld = false; this.padRepeat = 0; }
      if (pressed(0)) { if (this.padA) this.padA(); else if (this.padFocus) this.padFocus.click(); }
      if (pressed(1)) this.padBack();
      if (pressed(2) && this.padX) this.padX();
      if (pressed(3) && this.padY) this.padY();
      if (pressed(4)) this.padTab(-1);
      if (pressed(5)) this.padTab(1);
      if (pressed(9) && this.onMute) this.onMute();
      if (this.padLT > 0.95 && this.padRT > 0.95) {                                          // LT+RT held: discharge
        if (this.padSurge >= 0) { this.padSurge += 1 / 60; if (this.padSurge > 0.6) { this.padSurge = -1.5; this.padKick(gp); if (this.padDischarge) this.padDischarge(); } }
      } else if (this.padSurge > 0) this.padSurge = 0;
      if (this.padSurge < 0) this.padSurge = Math.min(0, this.padSurge + 1 / 60);
      this.padPrev = gp.buttons.map((x) => x.pressed);
    };
    if (has) this.padRaf = requestAnimationFrame(this.padLoop);
  }
  padStart() {
    const has = Array.from((navigator.getGamepads && navigator.getGamepads()) || []).some(Boolean);
    if (has !== this.state.padOn) this.setState({ padOn: has });
    if (has && !this.padRaf) this.padRaf = requestAnimationFrame(this.padLoop);
    if (!has) { this.padLT = this.padRT = 0; if (this.padMag && this.releasePointer) this.releasePointer(); this.padMag = false; }
  }
  padFocusables() {
    if (!this.root) return [];
    return Array.from(this.root.querySelectorAll('a[href], button, input')).filter((el) => el.offsetParent !== null || el.tagName === 'BUTTON');
  }
  padSetFocus(el) {
    if (this.padFocus) this.padFocus.classList.remove('pad-focus');
    this.padFocus = el;
    el.classList.add('pad-focus');
    el.focus({ preventScroll: true });
    if (this.sfx) this.sfx('tick');
  }
  padMove(dx, dy) {
    const list = this.padFocusables(); if (!list.length) return;
    if (!this.padFocus || !list.includes(this.padFocus)) { this.padSetFocus(list.find((e) => e.classList.contains('entity')) || list[0]); return; }
    const r0 = this.padFocus.getBoundingClientRect(), cx = r0.left + r0.width / 2, cy = r0.top + r0.height / 2;
    let best = null, bs = Infinity;
    for (const el of list) {
      if (el === this.padFocus) continue;
      const r = el.getBoundingClientRect(), x = r.left + r.width / 2 - cx, y = r.top + r.height / 2 - cy;
      const along = x * dx + y * dy; if (along <= 4) continue;
      const score = along + Math.abs(dx ? y : x) * 2.2;
      if (score < bs) { bs = score; best = el; }
    }
    if (best) this.padSetFocus(best);
  }
  padTabs() {
    if (!this.root) return [];
    const seen = new Set();
    return Array.from(this.root.querySelectorAll('nav a[href]')).filter((a) => { const h = a.getAttribute('href'); if (seen.has(h)) return false; seen.add(h); return true; });
  }
  padTab(dir) {
    const tabs = this.padTabs(); if (!tabs.length) return;
    const cur = this.root.querySelector('nav a[aria-current="page"]');
    const i = Math.max(0, tabs.findIndex((a) => cur && a.getAttribute('href') === cur.getAttribute('href')));
    tabs[(i + dir + tabs.length) % tabs.length].click();
  }
  padBack() {
    if (this.padB && this.padB()) return;
    const home = this.root && this.root.querySelector('nav a[href="Main.dc.html"]');
    if (home && !home.hasAttribute('aria-current')) home.click();
  }
  padRumble(gp, now) {
    const act = gp.vibrationActuator; if (!act || !act.playEffect) return;
    const lt = this.padLT, rt = this.padRT;
    if ((lt < 0.05 && rt < 0.05) || now - this.lastRumble < 90) return;
    this.lastRumble = now;
    const fx = act.effects || [];
    const p = fx.includes && fx.includes('trigger-rumble')
      ? act.playEffect('trigger-rumble', { duration: 110, leftTrigger: lt * 0.35, rightTrigger: rt * 0.5, strongMagnitude: 0, weakMagnitude: 0 })
      : act.playEffect('dual-rumble', { duration: 110, strongMagnitude: lt * 0.12, weakMagnitude: rt * 0.25 });
    if (p && p.catch) p.catch(() => {});
  }
  padKick(gp) {
    const act = gp.vibrationActuator; if (!act || !act.playEffect) return;
    const p = act.playEffect('dual-rumble', { duration: 380, strongMagnitude: 0.8, weakMagnitude: 0.5 });
    if (p && p.catch) p.catch(() => {});
  }
  padVals() {
    return { setRoot: this.setRoot, padOn: !!this.state.padOn,
             ltPct: Math.round((this.state.lt || 0) * 100) + '%', rtPct: Math.round((this.state.rt || 0) * 100) + '%' };
  }
  padDispose() {
    cancelAnimationFrame(this.padRaf);
    window.removeEventListener('gamepadconnected', this.onPadConn);
    window.removeEventListener('gamepaddisconnected', this.onPadConn);
  }
