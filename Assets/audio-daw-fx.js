/**
 * AudioDawFx — per-track insert effects + master limiter + automation helpers for the DAW.
 * Every effect is a fixed sub-graph `input → {dry bypassGain ∥ effect→wetGain} → output`
 * so enable/disable and mix are always param-only crossfades (no graph rebuilds).
 * ctx-agnostic: the same builders run in the live AudioContext and OfflineAudioContext export.
 * Reuses SwarmUI utilities: createDiv(), createSpan(); knobs come from AudioDawMixer.createKnob.
 */
const AudioDawFx = (() => {
    'use strict';

    const PARAM_SMOOTH = 0.015; // setTargetAtTime constant, matches updatePlaybackGains

    // ===== small helpers =====

    function dbToGain(db) { return Math.pow(10, db / 20); }

    /** Log-taper mapping for frequency-ish knobs: norm 0..1 <-> value in [min, max]. */
    function logToNorm(v, min, max) { return Math.log(v / min) / Math.log(max / min); }
    function normToLog(n, min, max) { return min * Math.pow(max / min, n); }

    function setParam(param, value, when) {
        try { param.setTargetAtTime(value, when, PARAM_SMOOTH); } catch (_) { param.value = value; }
    }

    /** Procedural reverb impulse response: decorrelated stereo noise with a power-law decay tail. */
    function makeImpulseResponse(ctx, { seconds, decay }) {
        const len = Math.max(64, Math.round(seconds * ctx.sampleRate));
        const ir = ctx.createBuffer(2, len, ctx.sampleRate);
        for (let ch = 0; ch < 2; ch++) {
            const data = ir.getChannelData(ch);
            for (let i = 0; i < len; i++) {
                data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / len, decay);
            }
        }
        return ir;
    }

    /** Soft-saturation transfer curve: tanh(k·x)/tanh(k), k grows with drive. */
    function makeSaturationCurve(drive) {
        const k = 1 + drive * 9;
        const curve = new Float32Array(1024);
        const norm = Math.tanh(k);
        for (let n = 0; n < 1024; n++) {
            const x = (2 * n) / 1023 - 1;
            curve[n] = Math.tanh(k * x) / norm;
        }
        return curve;
    }

    /** Beats per division label, for tempo-synced delay. */
    const DELAY_DIVISIONS = { '1/2': 2, '1/4': 1, '1/4.': 1.5, '1/8': 0.5, '1/8.': 0.75, '1/16': 0.25 };

    function syncedDelayTime(division, bpm) {
        const beats = DELAY_DIVISIONS[division] ?? 1;
        return Math.min(4, (60 / (bpm || 120)) * beats);
    }

    // ===== effect definitions =====
    // Each def: label, defaults(), build(ctx, params, opts) -> inst {input, output, ...nodes},
    // apply(inst, params, when, opts) -> live param update. `mixStyle: 'crossfade'` means
    // bypassGain carries (1-mix) dry and wetGain carries mix; otherwise bypass is a pure on/off dry path.
    // knobs: [{key, label, min, max, taper?, fmt}] for the generic panel renderer.

    const FX_DEFS = {
        eq: {
            label: 'EQ',
            mixStyle: 'insert',
            defaults: () => ({ hpFreq: 20, lowGain: 0, lowFreq: 200, midGain: 0, midFreq: 1000, midQ: 1.0, highGain: 0, highFreq: 4000, lpFreq: 20000 }),
            knobs: [
                { key: 'hpFreq', label: 'HP', min: 20, max: 1000, taper: 'log', fmt: v => v >= 1000 ? (v / 1000).toFixed(1) + 'k' : Math.round(v) + '' },
                { key: 'lowGain', label: 'Low', min: -12, max: 12, fmt: v => v.toFixed(1) + 'dB' },
                { key: 'lowFreq', label: 'LoFrq', min: 40, max: 600, taper: 'log', fmt: v => Math.round(v) + '' },
                { key: 'midGain', label: 'Mid', min: -12, max: 12, fmt: v => v.toFixed(1) + 'dB' },
                { key: 'midFreq', label: 'MidFrq', min: 200, max: 8000, taper: 'log', fmt: v => v >= 1000 ? (v / 1000).toFixed(1) + 'k' : Math.round(v) + '' },
                { key: 'midQ', label: 'Q', min: 0.3, max: 4, fmt: v => v.toFixed(2) },
                { key: 'highGain', label: 'High', min: -12, max: 12, fmt: v => v.toFixed(1) + 'dB' },
                { key: 'highFreq', label: 'HiFrq', min: 1000, max: 16000, taper: 'log', fmt: v => (v / 1000).toFixed(1) + 'k' },
                { key: 'lpFreq', label: 'LP', min: 1000, max: 20000, taper: 'log', fmt: v => (v / 1000).toFixed(1) + 'k' }
            ],
            build(ctx, params) {
                const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.Q.value = 0.707;
                const low = ctx.createBiquadFilter(); low.type = 'lowshelf';
                const mid = ctx.createBiquadFilter(); mid.type = 'peaking';
                const high = ctx.createBiquadFilter(); high.type = 'highshelf';
                const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.Q.value = 0.707;
                hp.connect(low); low.connect(mid); mid.connect(high); high.connect(lp);
                const inst = { input: hp, output: lp, hp, low, mid, high, lp };
                this.apply(inst, params, ctx.currentTime);
                return inst;
            },
            apply(inst, p, when) {
                setParam(inst.hp.frequency, p.hpFreq, when);
                setParam(inst.low.gain, p.lowGain, when);
                setParam(inst.low.frequency, p.lowFreq, when);
                setParam(inst.mid.gain, p.midGain, when);
                setParam(inst.mid.frequency, p.midFreq, when);
                setParam(inst.mid.Q, p.midQ, when);
                setParam(inst.high.gain, p.highGain, when);
                setParam(inst.high.frequency, p.highFreq, when);
                setParam(inst.lp.frequency, p.lpFreq, when);
            }
        },

        compressor: {
            label: 'Compressor',
            mixStyle: 'insert',
            defaults: () => ({ threshold: -24, knee: 30, ratio: 4, attack: 0.003, release: 0.25, makeup: 0 }),
            knobs: [
                { key: 'threshold', label: 'Thresh', min: -60, max: 0, fmt: v => v.toFixed(0) + 'dB' },
                { key: 'knee', label: 'Knee', min: 0, max: 40, fmt: v => v.toFixed(0) },
                { key: 'ratio', label: 'Ratio', min: 1, max: 20, fmt: v => v.toFixed(1) + ':1' },
                { key: 'attack', label: 'Attack', min: 0.001, max: 1, taper: 'log', fmt: v => (v * 1000).toFixed(0) + 'ms' },
                { key: 'release', label: 'Rel', min: 0.01, max: 1, taper: 'log', fmt: v => (v * 1000).toFixed(0) + 'ms' },
                { key: 'makeup', label: 'Makeup', min: 0, max: 24, fmt: v => '+' + v.toFixed(1) + 'dB' }
            ],
            build(ctx, params) {
                const comp = ctx.createDynamicsCompressor();
                const makeup = ctx.createGain();
                comp.connect(makeup);
                const inst = { input: comp, output: makeup, comp, makeup };
                this.apply(inst, params, ctx.currentTime);
                return inst;
            },
            apply(inst, p, when) {
                setParam(inst.comp.threshold, p.threshold, when);
                setParam(inst.comp.knee, p.knee, when);
                setParam(inst.comp.ratio, p.ratio, when);
                setParam(inst.comp.attack, p.attack, when);
                setParam(inst.comp.release, p.release, when);
                setParam(inst.makeup.gain, dbToGain(p.makeup), when);
            }
        },

        reverb: {
            label: 'Reverb',
            mixStyle: 'crossfade',
            defaults: () => ({ size: 2.0, decay: 3.0, predelay: 0.02, mix: 0.3 }),
            knobs: [
                { key: 'size', label: 'Size', min: 0.1, max: 8, taper: 'log', fmt: v => v.toFixed(1) + 's' },
                { key: 'decay', label: 'Decay', min: 0.5, max: 6, fmt: v => v.toFixed(1) },
                { key: 'predelay', label: 'PreDly', min: 0, max: 0.2, fmt: v => (v * 1000).toFixed(0) + 'ms' },
                { key: 'mix', label: 'Mix', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' }
            ],
            build(ctx, params) {
                const predelay = ctx.createDelay(0.5);
                const conv = ctx.createConvolver();
                conv.normalize = true;
                conv.buffer = makeImpulseResponse(ctx, { seconds: params.size, decay: params.decay });
                predelay.connect(conv);
                const inst = { input: predelay, output: conv, predelay, conv, _irKey: `${params.size}|${params.decay}`, _irTimer: null };
                setParam(predelay.delayTime, params.predelay, ctx.currentTime);
                return inst;
            },
            apply(inst, p, when, opts = {}) {
                setParam(inst.predelay.delayTime, p.predelay, when);
                const key = `${p.size}|${p.decay}`;
                if (key !== inst._irKey) {
                    inst._irKey = key;
                    // Debounce IR regen while a knob drags — it's a synchronous noise fill
                    if (inst._irTimer) clearTimeout(inst._irTimer);
                    const ctx = opts.ctx || inst.conv.context;
                    inst._irTimer = setTimeout(() => {
                        inst._irTimer = null;
                        try { inst.conv.buffer = makeImpulseResponse(ctx, { seconds: p.size, decay: p.decay }); } catch (_) {}
                    }, 150);
                }
            }
        },

        delay: {
            label: 'Delay',
            mixStyle: 'crossfade',
            defaults: () => ({ time: 0.35, sync: false, division: '1/4', feedback: 0.35, tone: 8000, mix: 0.25 }),
            knobs: [
                { key: 'time', label: 'Time', min: 0.02, max: 2, taper: 'log', fmt: v => (v * 1000).toFixed(0) + 'ms' },
                { key: 'feedback', label: 'Fdbk', min: 0, max: 0.9, fmt: v => Math.round(v * 100) + '%' },
                { key: 'tone', label: 'Tone', min: 500, max: 15000, taper: 'log', fmt: v => (v / 1000).toFixed(1) + 'k' },
                { key: 'mix', label: 'Mix', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' }
            ],
            // extra non-knob controls rendered by the panel: sync checkbox + division select
            build(ctx, params, opts = {}) {
                const delay = ctx.createDelay(4);
                const toneLP = ctx.createBiquadFilter(); toneLP.type = 'lowpass'; toneLP.Q.value = 0.707;
                const feedback = ctx.createGain();
                delay.connect(toneLP);
                toneLP.connect(feedback);
                feedback.connect(delay); // feedback loop
                const inst = { input: delay, output: toneLP, delay, toneLP, feedback };
                this.apply(inst, params, ctx.currentTime, opts);
                return inst;
            },
            apply(inst, p, when, opts = {}) {
                const t = p.sync ? syncedDelayTime(p.division, opts.bpm) : p.time;
                setParam(inst.delay.delayTime, Math.min(4, t), when);
                setParam(inst.feedback.gain, Math.min(0.9, p.feedback), when);
                setParam(inst.toneLP.frequency, p.tone, when);
            }
        },

        saturation: {
            label: 'Saturation',
            mixStyle: 'crossfade',
            defaults: () => ({ drive: 0.3, trim: 0, mix: 1 }),
            knobs: [
                { key: 'drive', label: 'Drive', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' },
                { key: 'trim', label: 'Trim', min: -12, max: 6, fmt: v => v.toFixed(1) + 'dB' },
                { key: 'mix', label: 'Mix', min: 0, max: 1, fmt: v => Math.round(v * 100) + '%' }
            ],
            build(ctx, params) {
                const shaper = ctx.createWaveShaper();
                shaper.oversample = '4x';
                shaper.curve = makeSaturationCurve(params.drive);
                const trim = ctx.createGain();
                shaper.connect(trim);
                const inst = { input: shaper, output: trim, shaper, trim, _drive: params.drive };
                setParam(trim.gain, dbToGain(params.trim), ctx.currentTime);
                return inst;
            },
            apply(inst, p, when) {
                if (p.drive !== inst._drive) {
                    inst._drive = p.drive;
                    inst.shaper.curve = makeSaturationCurve(p.drive);
                }
                setParam(inst.trim.gain, dbToGain(p.trim), when);
            }
        }
    };

    /** Starter presets per effect type: label -> partial params merged over defaults. */
    const FX_PRESETS = {
        eq: {
            'Vocal clarity': { hpFreq: 90, lowGain: -2, midGain: 2.5, midFreq: 3000, midQ: 1.2, highGain: 2, highFreq: 8000 },
            'Drum punch': { hpFreq: 40, lowGain: 3, lowFreq: 80, midGain: -2, midFreq: 400, highGain: 2.5, highFreq: 6000 },
            'Bass focus': { hpFreq: 30, lowGain: 4, lowFreq: 70, midGain: -1.5, midFreq: 800, lpFreq: 8000 },
            'Telephone': { hpFreq: 600, lpFreq: 3000, midGain: 4, midFreq: 1500 }
        },
        compressor: {
            'Vocal level': { threshold: -20, ratio: 3, attack: 0.01, release: 0.2, makeup: 4 },
            'Drum smash': { threshold: -30, ratio: 8, attack: 0.002, release: 0.1, makeup: 6 },
            'Glue (gentle)': { threshold: -16, ratio: 2, attack: 0.03, release: 0.3, makeup: 2 }
        },
        reverb: {
            'Small room': { size: 0.6, decay: 2.5, predelay: 0.005, mix: 0.18 },
            'Hall': { size: 3.5, decay: 3, predelay: 0.03, mix: 0.3 },
            'Cathedral': { size: 7, decay: 2, predelay: 0.05, mix: 0.4 }
        },
        delay: {
            'Slap': { time: 0.09, sync: false, feedback: 0.12, mix: 0.22 },
            'Quarter echo': { sync: true, division: '1/4', feedback: 0.4, mix: 0.25 },
            'Dotted 8th': { sync: true, division: '1/8.', feedback: 0.45, mix: 0.28 }
        },
        saturation: {
            'Warm': { drive: 0.25, trim: -1, mix: 0.8 },
            'Crunch': { drive: 0.6, trim: -3, mix: 1 },
            'Destroy': { drive: 1, trim: -6, mix: 1 }
        }
    };

    /** Fresh effect state POJO for track.fx. */
    function createEffectState(type) {
        const def = FX_DEFS[type];
        if (!def) return null;
        return { type, enabled: true, params: def.defaults() };
    }

    // ===== chain building =====

    /**
     * Compute the dry/wet gain pair for one effect given its enabled + mix state.
     * insert-style: enabled -> dry 0 / wet 1; crossfade-style: dry (1-mix) / wet mix.
     */
    function mixGains(fx) {
        const def = FX_DEFS[fx.type];
        if (!fx.enabled) return { dry: 1, wet: 0 };
        if (def.mixStyle === 'crossfade') {
            const mix = fx.params.mix ?? 0.5;
            return { dry: 1 - mix, wet: mix };
        }
        return { dry: 0, wet: 1 };
    }

    /**
     * Build a serial chain of effect sub-graphs from track.fx.
     * @returns {{input, output, effects: [{type, inst, bypassGain, wetGain}]} | null}
     */
    function buildFxChain(ctx, fxArray, opts = {}) {
        const list = (fxArray || []).filter(fx => FX_DEFS[fx.type]);
        if (!list.length) return null;
        const chain = { input: null, output: null, effects: [] };
        let prev = null;
        for (const fx of list) {
            const def = FX_DEFS[fx.type];
            const stageIn = ctx.createGain();
            const stageOut = ctx.createGain();
            const bypassGain = ctx.createGain();
            const wetGain = ctx.createGain();
            const inst = def.build(ctx, fx.params, { ...opts, ctx });
            stageIn.connect(bypassGain);
            bypassGain.connect(stageOut);
            stageIn.connect(inst.input);
            inst.output.connect(wetGain);
            wetGain.connect(stageOut);
            const g = mixGains(fx);
            bypassGain.gain.value = g.dry;
            wetGain.gain.value = g.wet;
            chain.effects.push({ type: fx.type, inst, bypassGain, wetGain, stageIn, stageOut });
            if (prev) prev.connect(stageIn);
            else chain.input = stageIn;
            prev = stageOut;
        }
        chain.output = prev;
        return chain;
    }

    /** Disconnect every node in a chain (breaks delay feedback loops so nodes can be GC'd). */
    function disposeFxChain(chain) {
        if (!chain) return;
        for (const e of chain.effects) {
            for (const node of Object.values(e.inst)) {
                if (node && typeof node.disconnect === 'function') {
                    try { node.disconnect(); } catch (_) {}
                }
            }
            for (const n of [e.bypassGain, e.wetGain, e.stageIn, e.stageOut]) {
                try { n.disconnect(); } catch (_) {}
            }
            if (e.inst._irTimer) clearTimeout(e.inst._irTimer);
        }
        chain.effects = [];
    }

    /** Live enable/disable + mix crossfade for effect at index. */
    function setEffectEnabled(chain, index, fx, when) {
        const e = chain?.effects[index];
        if (!e) return;
        const g = mixGains(fx);
        setParam(e.bypassGain.gain, g.dry, when);
        setParam(e.wetGain.gain, g.wet, when);
    }

    /** Live param update for effect at index (also refreshes mix gains for crossfade effects). */
    function applyEffectParams(chain, index, fx, when, opts = {}) {
        const e = chain?.effects[index];
        if (!e) return;
        FX_DEFS[fx.type].apply(e.inst, fx.params, when, opts);
        setEffectEnabled(chain, index, fx, when);
    }

    /** Retune all tempo-synced delays after a BPM change. */
    function syncDelayTimes(chain, fxArray, bpm, when) {
        if (!chain) return;
        chain.effects.forEach((e, i) => {
            const fx = fxArray[i];
            if (e.type === 'delay' && fx?.params.sync) {
                setParam(e.inst.delay.delayTime, syncedDelayTime(fx.params.division, bpm), when);
            }
        });
    }

    /** Master bus limiter: near-brickwall DynamicsCompressor (no lookahead; close enough live+offline). */
    function buildMasterLimiter(ctx) {
        const lim = ctx.createDynamicsCompressor();
        lim.threshold.value = -1;
        lim.knee.value = 0;
        lim.ratio.value = 20;
        lim.attack.value = 0.002;
        lim.release.value = 0.15;
        return lim;
    }

    // ===== automation helpers =====

    /** Piecewise-linear envelope value at time t. Empty -> identity (undefined). */
    function envelopeValueAt(points, t) {
        if (!points || !points.length) return undefined;
        if (t <= points[0].t) return points[0].v;
        if (t >= points[points.length - 1].t) return points[points.length - 1].v;
        for (let i = 1; i < points.length; i++) {
            if (t <= points[i].t) {
                const a = points[i - 1], b = points[i];
                const f = (t - a.t) / (b.t - a.t || 1e-9);
                return a.v + (b.v - a.v) * f;
            }
        }
        return points[points.length - 1].v;
    }

    /**
     * Schedule an envelope onto an AudioParam using the DAW's origin mapping
     * (ctx time = t0Ctx + (t - t0Timeline)).
     * @param {Object} o - { t0Ctx, t0Timeline, windowEnd, transform, fresh }
     */
    function scheduleEnvelope(param, points, o) {
        if (!points || !points.length) return;
        const xf = o.transform || (v => v);
        const P = o.t0Timeline;
        const W = o.windowEnd ?? Infinity;
        if (o.fresh) {
            try { param.cancelScheduledValues(o.t0Ctx); } catch (_) {}
        }
        param.setValueAtTime(xf(envelopeValueAt(points, P)), o.t0Ctx);
        for (const pt of points) {
            if (pt.t <= P || pt.t >= W) continue;
            param.linearRampToValueAtTime(xf(pt.v), o.t0Ctx + (pt.t - P));
        }
        const last = points[points.length - 1];
        if (W < Infinity && last.t > W) {
            param.linearRampToValueAtTime(xf(envelopeValueAt(points, W)), o.t0Ctx + (W - P));
        }
    }

    // ===== FX panel UI =====

    /** Small knob+label column using the mixer's rotary widget; handles log tapers. */
    function knobColumn(fx, spec, onParamChange) {
        const col = createDiv(null, 'daw-fx-knob-col');
        const isLog = spec.taper === 'log';
        const cur = fx.params[spec.key];
        const knob = AudioDawMixer.createKnob({
            value: isLog ? logToNorm(cur, spec.min, spec.max) : cur,
            min: isLog ? 0 : spec.min,
            max: isLog ? 1 : spec.max,
            defaultValue: isLog ? logToNorm(FX_DEFS[fx.type].defaults()[spec.key], spec.min, spec.max) : FX_DEFS[fx.type].defaults()[spec.key],
            title: spec.label,
            onChange: (v) => {
                const real = isLog ? normToLog(v, spec.min, spec.max) : v;
                fx.params[spec.key] = real;
                val.textContent = spec.fmt(real);
                onParamChange(fx);
            }
        });
        const lbl = createSpan(null, 'daw-fx-knob-label');
        lbl.textContent = spec.label;
        const val = createSpan(null, 'daw-fx-knob-val');
        val.textContent = spec.fmt(cur);
        col.appendChild(knob);
        col.appendChild(lbl);
        col.appendChild(val);
        return col;
    }

    /**
     * Render the FX tab for one track.
     * callbacks: { onParamChange(track, fx, index), onToggle(track, fx, index),
     *              onAdd(track, type), onRemove(track, index), onMove(track, index, dir),
     *              onMasterLimiter(enabled), masterLimiterEnabled }
     */
    // One-line pitches for the effect browser (shown when a chain is empty)
    const FX_DESCS = {
        eq: '5-band shape — cut mud, add air',
        compressor: 'Even out dynamics, add punch',
        reverb: 'Space and depth (procedural room)',
        delay: 'Echoes — free or tempo-synced',
        saturation: 'Analog-style warmth and drive'
    };

    function renderFxPanel(container, track, callbacks) {
        container.innerHTML = '';
        if (!track) {
            container.innerHTML = '<span class="daw-stems-clipinfo">Select a track to edit its effects</span>';
            return;
        }

        // Toolbar: track context + chain save/load + master limiter
        const bar = createDiv(null, 'daw-fx-toolbar');
        const barTitle = createSpan(null, 'daw-fx-toolbar-title');
        barTitle.textContent = `FX — ${track.name}`;
        bar.appendChild(barTitle);
        const saveChainBtn = document.createElement('button');
        saveChainBtn.className = 'basic-button btn-sm';
        saveChainBtn.textContent = 'Save Chain';
        saveChainBtn.title = 'Save this track\'s whole effect chain as a reusable preset';
        saveChainBtn.disabled = !track.fx.length;
        saveChainBtn.addEventListener('click', (e) => callbacks.onSaveChain && callbacks.onSaveChain(track, e));
        bar.appendChild(saveChainBtn);
        const loadChainBtn = document.createElement('button');
        loadChainBtn.className = 'basic-button btn-sm';
        loadChainBtn.textContent = 'Load Chain';
        loadChainBtn.title = 'Replace this track\'s effects with a saved chain';
        loadChainBtn.addEventListener('click', (e) => callbacks.onLoadChain && callbacks.onLoadChain(track, e));
        bar.appendChild(loadChainBtn);
        const limWrap = createDiv(null, 'daw-fx-lim');
        const limBox = document.createElement('input');
        limBox.type = 'checkbox';
        limBox.id = 'daw_fx_master_limiter';
        limBox.checked = !!callbacks.masterLimiterEnabled;
        const limLbl = document.createElement('label');
        limLbl.htmlFor = limBox.id;
        limLbl.className = 'daw-fx-knob-label';
        limLbl.textContent = 'Master limiter';
        limBox.addEventListener('change', () => callbacks.onMasterLimiter(limBox.checked));
        limWrap.appendChild(limBox);
        limWrap.appendChild(limLbl);
        bar.appendChild(limWrap);
        container.appendChild(bar);

        // Empty chain: browse all available effects as cards — click one to add it
        if (!track.fx.length) {
            const hint = createDiv(null, 'daw-stems-desc');
            hint.textContent = 'Click an effect to add it to this track\'s chain:';
            container.appendChild(hint);
            const browser = createDiv(null, 'daw-fx-browser');
            for (const [type, def] of Object.entries(FX_DEFS)) {
                const pick = document.createElement('button');
                pick.className = 'daw-fx-pick';
                pick.innerHTML = `<span class="daw-fx-pick-name">${def.label}</span>`
                    + `<span class="daw-fx-pick-desc">${FX_DESCS[type] || ''}</span>`;
                pick.addEventListener('click', () => callbacks.onAdd(track, type));
                browser.appendChild(pick);
            }
            container.appendChild(browser);
            return;
        }

        const row = createDiv(null, 'daw-fx-row');
        container.appendChild(row);

        track.fx.forEach((fx, index) => {
            const def = FX_DEFS[fx.type];
            if (!def) return;
            const card = createDiv(null, 'daw-fx-card' + (fx.enabled ? '' : ' fx-disabled'));

            const head = createDiv(null, 'daw-fx-card-head');
            const enable = document.createElement('input');
            enable.type = 'checkbox';
            enable.checked = fx.enabled;
            enable.title = 'Enable/bypass';
            enable.addEventListener('change', () => {
                fx.enabled = enable.checked;
                card.classList.toggle('fx-disabled', !fx.enabled);
                callbacks.onToggle(track, fx, index);
            });
            head.appendChild(enable);
            const title = createSpan(null, 'daw-fx-card-title');
            title.textContent = def.label;
            head.appendChild(title);
            const controls = createDiv(null, 'daw-fx-card-btns');
            const mk = (txt, tip, fn, disabled) => {
                const b = document.createElement('button');
                b.className = 'daw-fx-mini-btn';
                b.innerHTML = txt;
                b.title = tip;
                b.disabled = !!disabled;
                b.addEventListener('click', fn);
                controls.appendChild(b);
            };
            const presets = FX_PRESETS[fx.type];
            if (presets && callbacks.showMenu) {
                mk('P', 'Load a preset', (e) => callbacks.showMenu(e, Object.entries(presets).map(([name, vals]) => ({
                    label: name,
                    action: () => {
                        Object.assign(fx.params, def.defaults(), vals);
                        callbacks.onParamChange(track, fx, index);
                        if (callbacks.onPresetApplied) callbacks.onPresetApplied();
                    }
                }))));
            }
            mk('&#x25C0;', 'Move earlier in chain', () => callbacks.onMove(track, index, -1), index === 0);
            mk('&#x25B6;', 'Move later in chain', () => callbacks.onMove(track, index, 1), index === track.fx.length - 1);
            mk('&#x2715;', 'Remove effect', () => callbacks.onRemove(track, index));
            head.appendChild(controls);
            card.appendChild(head);

            const knobs = createDiv(null, 'daw-fx-knobs');
            for (const spec of def.knobs) {
                knobs.appendChild(knobColumn(fx, spec, (f) => callbacks.onParamChange(track, f, index)));
            }
            card.appendChild(knobs);

            // Delay extras: sync checkbox + division select
            if (fx.type === 'delay') {
                const extra = createDiv(null, 'daw-fx-extra');
                const syncBox = document.createElement('input');
                syncBox.type = 'checkbox';
                syncBox.id = `daw_fx_sync_${track.id}_${index}`;
                syncBox.checked = !!fx.params.sync;
                const syncLbl = document.createElement('label');
                syncLbl.htmlFor = syncBox.id;
                syncLbl.textContent = 'Sync';
                syncLbl.className = 'daw-fx-knob-label';
                const divSel = document.createElement('select');
                divSel.className = 'daw-fx-select';
                for (const d of Object.keys(DELAY_DIVISIONS)) {
                    const opt = document.createElement('option');
                    opt.value = d; opt.textContent = d;
                    divSel.appendChild(opt);
                }
                divSel.value = fx.params.division;
                divSel.disabled = !fx.params.sync;
                syncBox.addEventListener('change', () => {
                    fx.params.sync = syncBox.checked;
                    divSel.disabled = !fx.params.sync;
                    callbacks.onParamChange(track, fx, index);
                });
                divSel.addEventListener('change', () => {
                    fx.params.division = divSel.value;
                    callbacks.onParamChange(track, fx, index);
                });
                extra.appendChild(syncBox);
                extra.appendChild(syncLbl);
                extra.appendChild(divSel);
                card.appendChild(extra);
            }

            row.appendChild(card);
        });

        // Add-effect card at the end of the chain
        const addCard = createDiv(null, 'daw-fx-card daw-fx-add-card');
        const addBtn = document.createElement('button');
        addBtn.className = 'daw-add-track';
        addBtn.textContent = '+ Add Effect';
        addBtn.addEventListener('click', (e) => {
            const items = Object.entries(FX_DEFS).map(([type, def]) => ({
                label: def.label,
                action: () => callbacks.onAdd(track, type)
            }));
            if (callbacks.showMenu) callbacks.showMenu(e, items);
        });
        addCard.appendChild(addBtn);
        row.appendChild(addCard);
    }

    return {
        FX_DEFS, createEffectState, buildFxChain, disposeFxChain, setEffectEnabled,
        applyEffectParams, syncDelayTimes, buildMasterLimiter, makeImpulseResponse,
        makeSaturationCurve, envelopeValueAt, scheduleEnvelope, renderFxPanel
    };
})();
