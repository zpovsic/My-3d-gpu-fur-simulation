// Procedural Cute Fluffy Monster Sound Engine (Web Audio API)
// Synthesizes organic formant vocalizations, playful mini-growls, cozy petting purrs,
// drag squeaks, and landing plops with per-trigger pitch/formant variation.

export function createMonsterAudio({ enabled = true } = {}) {
  let ctx = null;
  let masterGain = null;
  let noiseBuffer = null;
  let isEnabled = Boolean(enabled);

  // Continuous petting purr state
  let purrGain = null;
  let purrOsc = null;
  let purrSubOsc = null;
  let purrLfo = null;
  let purrNoiseGain = null;
  let purrTarget = 0;
  let lastBoopIndex = -1;

  function ensureContext() {
    if (!isEnabled) return null;
    if (!ctx) {
      const AudioCtx = window.AudioContext || window.webkitAudioContext;
      if (!AudioCtx) return null;
      ctx = new AudioCtx();

      const compressor = ctx.createDynamicsCompressor();
      compressor.threshold.value = -14;
      compressor.knee.value = 18;
      compressor.ratio.value = 6;
      compressor.attack.value = 0.004;
      compressor.release.value = 0.14;

      masterGain = ctx.createGain();
      masterGain.gain.value = 0.42;
      masterGain.connect(compressor);
      compressor.connect(ctx.destination);

      // Precompute 1.5s pink-ish breath/fur noise buffer
      const length = Math.floor(ctx.sampleRate * 1.5);
      noiseBuffer = ctx.createBuffer(1, length, ctx.sampleRate);
      const data = noiseBuffer.getChannelData(0);
      let b0 = 0;
      let b1 = 0;
      let b2 = 0;
      for (let i = 0; i < length; i++) {
        const white = Math.random() * 2 - 1;
        b0 = 0.997 * b0 + white * 0.029;
        b1 = 0.985 * b1 + white * 0.032;
        b2 = 0.95 * b2 + white * 0.048;
        data[i] = Math.max(-1, Math.min(1, (b0 + b1 + b2 + white * 0.06) * 1.8));
      }

      initPurrVoice();
    }

    if (ctx.state === "suspended") {
      ctx.resume().catch(() => {});
    }
    return ctx;
  }

  function initPurrVoice() {
    if (!ctx || !masterGain || purrGain) return;

    purrGain = ctx.createGain();
    purrGain.gain.value = 0.0001;

    // Warm low-pass vocal tract filter for cozy monster purr
    const purrFilter = ctx.createBiquadFilter();
    purrFilter.type = "lowpass";
    purrFilter.frequency.value = 340;
    purrFilter.Q.value = 2.4;

    // Fundamental purr + sub-octave chest hum
    purrOsc = ctx.createOscillator();
    purrOsc.type = "sawtooth";
    purrOsc.frequency.value = 78;

    purrSubOsc = ctx.createOscillator();
    purrSubOsc.type = "triangle";
    purrSubOsc.frequency.value = 39;

    const purrMix = ctx.createGain();
    purrMix.gain.value = 0.5;

    // Amplitude modulation LFO (~24 Hz feline/monster purr flutter)
    purrLfo = ctx.createOscillator();
    purrLfo.type = "sine";
    purrLfo.frequency.value = 23.5;

    const purrLfoDepth = ctx.createGain();
    purrLfoDepth.gain.value = 0.34;

    purrLfo.connect(purrLfoDepth);
    purrLfoDepth.connect(purrMix.gain);

    purrOsc.connect(purrMix);
    purrSubOsc.connect(purrMix);
    purrMix.connect(purrFilter);
    purrFilter.connect(purrGain);

    // Subtle soft fur-brushing rustle layered into petting
    if (noiseBuffer) {
      const rustle = ctx.createBufferSource();
      rustle.buffer = noiseBuffer;
      rustle.loop = true;

      const rustleFilter = ctx.createBiquadFilter();
      rustleFilter.type = "bandpass";
      rustleFilter.frequency.value = 1450;
      rustleFilter.Q.value = 1.6;

      purrNoiseGain = ctx.createGain();
      purrNoiseGain.gain.value = 0.0001;

      rustle.connect(rustleFilter);
      rustleFilter.connect(purrNoiseGain);
      purrNoiseGain.connect(masterGain);
      rustle.start();
    }

    purrGain.connect(masterGain);
    purrOsc.start();
    purrSubOsc.start();
    purrLfo.start();
  }

  // Helper: create a dual-formant vocal tract filter chain for creature voices
  function createFormantVoice(ac, now, { f1Start, f1End, f2Start, f2End, dur, q = 4.5 }) {
    const input = ac.createGain();
    const out = ac.createGain();

    const bp1 = ac.createBiquadFilter();
    bp1.type = "bandpass";
    bp1.Q.value = q;
    bp1.frequency.setValueAtTime(f1Start, now);
    bp1.frequency.exponentialRampToValueAtTime(Math.max(80, f1End), now + dur);

    const bp2 = ac.createBiquadFilter();
    bp2.type = "bandpass";
    bp2.Q.value = q * 1.15;
    bp2.frequency.setValueAtTime(f2Start, now);
    bp2.frequency.exponentialRampToValueAtTime(Math.max(120, f2End), now + dur);

    const g1 = ac.createGain();
    g1.gain.value = 1.15;
    const g2 = ac.createGain();
    g2.gain.value = 0.72;

    input.connect(bp1);
    input.connect(bp2);
    bp1.connect(g1);
    bp2.connect(g2);
    g1.connect(out);
    g2.connect(out);

    return { input, out };
  }

  function addBreathNoise(ac, now, dest, dur, centerFreq, level) {
    if (!noiseBuffer) return;
    const src = ac.createBufferSource();
    src.buffer = noiseBuffer;

    const filter = ac.createBiquadFilter();
    filter.type = "bandpass";
    filter.frequency.value = centerFreq;
    filter.Q.value = 2.0;

    const env = ac.createGain();
    env.gain.setValueAtTime(0.0001, now);
    env.gain.linearRampToValueAtTime(level, now + dur * 0.22);
    env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

    src.connect(filter);
    filter.connect(env);
    env.connect(dest);

    src.start(now);
    src.stop(now + dur + 0.03);
  }

  // 1. Playful Monster Boop Vocalizations (5 distinct randomized monster voices)
  function playBoop() {
    const ac = ensureContext();
    if (!ac || !masterGain) return;

    const now = ac.currentTime;
    let variant = Math.floor(Math.random() * 5);
    if (variant === lastBoopIndex) {
      variant = (variant + 1) % 5;
    }
    lastBoopIndex = variant;

    const jitter = 0.92 + Math.random() * 0.16;

    if (variant === 0) {
      // "Mrrp-bwoop!" — bouncy curious furry monster chirp
      const dur = 0.28;
      const tract = createFormantVoice(ac, now, {
        f1Start: 420 * jitter,
        f1End: 920 * jitter,
        f2Start: 1250 * jitter,
        f2End: 2100 * jitter,
        dur,
        q: 4.0,
      });

      const osc = ac.createOscillator();
      osc.type = "sawtooth";
      osc.frequency.setValueAtTime(165 * jitter, now);
      osc.frequency.exponentialRampToValueAtTime(340 * jitter, now + 0.12);
      osc.frequency.exponentialRampToValueAtTime(260 * jitter, now + dur);

      const vib = ac.createOscillator();
      const vibGain = ac.createGain();
      vib.frequency.value = 30;
      vibGain.gain.value = 18;
      vib.connect(vibGain);
      vibGain.connect(osc.frequency);

      const env = ac.createGain();
      env.gain.setValueAtTime(0.0001, now);
      env.gain.linearRampToValueAtTime(0.95, now + 0.025);
      env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

      osc.connect(tract.input);
      tract.out.connect(env);
      env.connect(masterGain);

      addBreathNoise(ac, now, masterGain, 0.16, 1600, 0.14);

      osc.start(now);
      vib.start(now);
      osc.stop(now + dur + 0.03);
      vib.stop(now + dur + 0.03);
    } else if (variant === 1) {
      // "Grrr-mep!" — gravelly mini-monster growl into a tiny squeak
      const dur = 0.36;
      const tract = createFormantVoice(ac, now, {
        f1Start: 310 * jitter,
        f1End: 680 * jitter,
        f2Start: 880 * jitter,
        f2End: 1750 * jitter,
        dur,
        q: 3.6,
      });

      const osc = ac.createOscillator();
      osc.type = "sawtooth";
      osc.frequency.setValueAtTime(112 * jitter, now);
      osc.frequency.linearRampToValueAtTime(98 * jitter, now + 0.19);
      osc.frequency.exponentialRampToValueAtTime(295 * jitter, now + 0.29);
      osc.frequency.exponentialRampToValueAtTime(215 * jitter, now + dur);

      // Growl vocal-cord rasp (FM)
      const rasp = ac.createOscillator();
      const raspGain = ac.createGain();
      rasp.type = "triangle";
      rasp.frequency.value = 34;
      raspGain.gain.setValueAtTime(34, now);
      raspGain.gain.exponentialRampToValueAtTime(4, now + dur);
      rasp.connect(raspGain);
      raspGain.connect(osc.frequency);

      const env = ac.createGain();
      env.gain.setValueAtTime(0.0001, now);
      env.gain.linearRampToValueAtTime(1.05, now + 0.035);
      env.gain.setValueAtTime(0.85, now + 0.22);
      env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

      osc.connect(tract.input);
      tract.out.connect(env);
      env.connect(masterGain);

      addBreathNoise(ac, now, masterGain, 0.26, 950, 0.2);

      osc.start(now);
      rasp.start(now);
      osc.stop(now + dur + 0.03);
      rasp.stop(now + dur + 0.03);
    } else if (variant === 2) {
      // "Rawr-pip!" — tiny dramatic fierce-but-adorable monster roar
      const dur = 0.34;
      const tract = createFormantVoice(ac, now, {
        f1Start: 690 * jitter,
        f1End: 290 * jitter,
        f2Start: 1580 * jitter,
        f2End: 780 * jitter,
        dur,
        q: 3.2,
      });

      const osc = ac.createOscillator();
      osc.type = "sawtooth";
      osc.frequency.setValueAtTime(210 * jitter, now);
      osc.frequency.exponentialRampToValueAtTime(118 * jitter, now + dur);

      const sub = ac.createOscillator();
      sub.type = "triangle";
      sub.frequency.setValueAtTime(105 * jitter, now);
      sub.frequency.exponentialRampToValueAtTime(64 * jitter, now + dur);

      const growlMod = ac.createOscillator();
      const growlDepth = ac.createGain();
      growlMod.frequency.value = 42;
      growlDepth.gain.value = 26;
      growlMod.connect(growlDepth);
      growlDepth.connect(osc.frequency);

      const env = ac.createGain();
      env.gain.setValueAtTime(0.0001, now);
      env.gain.linearRampToValueAtTime(1.1, now + 0.03);
      env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

      osc.connect(tract.input);
      sub.connect(tract.input);
      tract.out.connect(env);
      env.connect(masterGain);

      addBreathNoise(ac, now, masterGain, dur, 1150, 0.24);

      osc.start(now);
      sub.start(now);
      growlMod.start(now);
      osc.stop(now + dur + 0.03);
      sub.stop(now + dur + 0.03);
      growlMod.stop(now + dur + 0.03);
    } else if (variant === 3) {
      // "Brrwah!" — wobbly bubbly gremlin chirp
      const dur = 0.3;
      const tract = createFormantVoice(ac, now, {
        f1Start: 360 * jitter,
        f1End: 840 * jitter,
        f2Start: 1050 * jitter,
        f2End: 2250 * jitter,
        dur,
        q: 5.0,
      });

      const osc = ac.createOscillator();
      osc.type = "square";
      osc.frequency.setValueAtTime(150 * jitter, now);
      osc.frequency.exponentialRampToValueAtTime(260 * jitter, now + 0.14);
      osc.frequency.exponentialRampToValueAtTime(195 * jitter, now + dur);

      const lfo = ac.createOscillator();
      const lfoGain = ac.createGain();
      lfo.frequency.value = 24;
      lfoGain.gain.value = 32;
      lfo.connect(lfoGain);
      lfoGain.connect(osc.frequency);

      const env = ac.createGain();
      env.gain.setValueAtTime(0.0001, now);
      env.gain.linearRampToValueAtTime(0.75, now + 0.02);
      env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

      osc.connect(tract.input);
      tract.out.connect(env);
      env.connect(masterGain);

      osc.start(now);
      lfo.start(now);
      osc.stop(now + dur + 0.03);
      lfo.stop(now + dur + 0.03);
    } else {
      // "Puff-churr!" — double-chirp happy fluffy monster trill
      for (let i = 0; i < 2; i++) {
        const t0 = now + i * 0.11;
        const dur = 0.14;
        const pitch = (i === 0 ? 225 : 310) * jitter;

        const tract = createFormantVoice(ac, t0, {
          f1Start: 520 * jitter,
          f1End: 1080 * jitter,
          f2Start: 1400 * jitter,
          f2End: 2450 * jitter,
          dur,
          q: 4.5,
        });

        const osc = ac.createOscillator();
        osc.type = "sawtooth";
        osc.frequency.setValueAtTime(pitch * 0.85, t0);
        osc.frequency.exponentialRampToValueAtTime(pitch * 1.38, t0 + dur * 0.7);
        osc.frequency.exponentialRampToValueAtTime(pitch * 1.15, t0 + dur);

        const env = ac.createGain();
        env.gain.setValueAtTime(0.0001, t0);
        env.gain.linearRampToValueAtTime(0.85, t0 + 0.018);
        env.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

        osc.connect(tract.input);
        tract.out.connect(env);
        env.connect(masterGain);

        osc.start(t0);
        osc.stop(t0 + dur + 0.02);
      }
      addBreathNoise(ac, now, masterGain, 0.22, 2000, 0.12);
    }
  }

  // 2. Pickup / Start-Drag Surprised Monster Squeak ("Wheee-eep!")
  function playPickup() {
    const ac = ensureContext();
    if (!ac || !masterGain) return;

    const now = ac.currentTime;
    const dur = 0.2;
    const jitter = 0.94 + Math.random() * 0.12;

    const tract = createFormantVoice(ac, now, {
      f1Start: 500 * jitter,
      f1End: 1150 * jitter,
      f2Start: 1400 * jitter,
      f2End: 2550 * jitter,
      dur,
      q: 4.2,
    });

    const osc = ac.createOscillator();
    osc.type = "sawtooth";
    osc.frequency.setValueAtTime(195 * jitter, now);
    osc.frequency.exponentialRampToValueAtTime(420 * jitter, now + dur * 0.8);
    osc.frequency.exponentialRampToValueAtTime(360 * jitter, now + dur);

    const env = ac.createGain();
    env.gain.setValueAtTime(0.0001, now);
    env.gain.linearRampToValueAtTime(0.68, now + 0.02);
    env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

    osc.connect(tract.input);
    tract.out.connect(env);
    env.connect(masterGain);

    osc.start(now);
    osc.stop(now + dur + 0.03);
  }

  // 3. Soft Squishy Set-Down Plop ("Bloop-mrr")
  function playDrop() {
    const ac = ensureContext();
    if (!ac || !masterGain) return;

    const now = ac.currentTime;
    const dur = 0.18;
    const osc = ac.createOscillator();
    osc.type = "sine";
    osc.frequency.setValueAtTime(240, now);
    osc.frequency.exponentialRampToValueAtTime(105, now + 0.09);
    osc.frequency.exponentialRampToValueAtTime(165, now + dur);

    const env = ac.createGain();
    env.gain.setValueAtTime(0.0001, now);
    env.gain.linearRampToValueAtTime(0.55, now + 0.014);
    env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

    osc.connect(env);
    env.connect(masterGain);

    osc.start(now);
    osc.stop(now + dur + 0.02);
  }

  // 4. Continuous Cozy Monster Purr while Petting & Combing Fur
  function updatePetting(petSpeed, isOver) {
    if (!isEnabled) {
      purrTarget = 0;
      if (ctx && purrGain) {
        purrGain.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.06);
      }
      if (ctx && purrNoiseGain) {
        purrNoiseGain.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.06);
      }
      return;
    }

    const intensity = isOver ? Math.min(1.0, Math.max(0.0, (petSpeed - 0.18) / 2.2)) : 0.0;
    if (intensity > 0.05) {
      ensureContext();
    }
    if (!ctx || !purrGain) return;

    purrTarget = intensity;
    const now = ctx.currentTime;
    const gainVal = purrTarget > 0.02 ? 0.06 + purrTarget * 0.24 : 0.0001;
    const rustleVal = purrTarget > 0.02 ? purrTarget * 0.08 : 0.0001;

    purrGain.gain.setTargetAtTime(gainVal, now, purrTarget > 0.02 ? 0.08 : 0.16);
    if (purrNoiseGain) {
      purrNoiseGain.gain.setTargetAtTime(rustleVal, now, 0.07);
    }
    if (purrOsc && purrSubOsc) {
      const pitch = 74 + purrTarget * 22;
      purrOsc.frequency.setTargetAtTime(pitch, now, 0.1);
      purrSubOsc.frequency.setTargetAtTime(pitch * 0.5, now, 0.1);
    }
  }

  // 5. Happy Trill on Preset Change
  function playHappyTrill() {
    const ac = ensureContext();
    if (!ac || !masterGain) return;

    const now = ac.currentTime;
    const notes = [230, 290, 365];
    notes.forEach((freq, idx) => {
      const t0 = now + idx * 0.07;
      const dur = 0.12;
      const osc = ac.createOscillator();
      osc.type = "triangle";
      osc.frequency.setValueAtTime(freq, t0);
      osc.frequency.exponentialRampToValueAtTime(freq * 1.18, t0 + dur);

      const env = ac.createGain();
      env.gain.setValueAtTime(0.0001, t0);
      env.gain.linearRampToValueAtTime(0.42, t0 + 0.015);
      env.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);

      osc.connect(env);
      env.connect(masterGain);
      osc.start(t0);
      osc.stop(t0 + dur + 0.02);
    });
  }

  // 6. Soft Cozy Sleepy Monster Snore ("hrrr-pfff...")
  function playSnore() {
    const ac = ensureContext();
    if (!ac || !masterGain) return;

    const now = ac.currentTime;
    const dur = 0.78;
    const jitter = 0.92 + Math.random() * 0.14;

    const filter = ac.createBiquadFilter();
    filter.type = "lowpass";
    filter.frequency.setValueAtTime(260 * jitter, now);
    filter.frequency.exponentialRampToValueAtTime(160 * jitter, now + dur);
    filter.Q.value = 3.0;

    const osc = ac.createOscillator();
    osc.type = "sawtooth";
    osc.frequency.setValueAtTime(64 * jitter, now);
    osc.frequency.exponentialRampToValueAtTime(52 * jitter, now + dur);

    // Gentle snore flutter
    const flutter = ac.createOscillator();
    const flutterGain = ac.createGain();
    flutter.frequency.value = 17;
    flutterGain.gain.value = 10;
    flutter.connect(flutterGain);
    flutterGain.connect(osc.frequency);

    const env = ac.createGain();
    env.gain.setValueAtTime(0.0001, now);
    env.gain.linearRampToValueAtTime(0.2, now + 0.24);
    env.gain.exponentialRampToValueAtTime(0.0001, now + dur);

    osc.connect(filter);
    filter.connect(env);
    env.connect(masterGain);

    addBreathNoise(ac, now + 0.1, masterGain, 0.55, 620, 0.07);

    osc.start(now);
    flutter.start(now);
    osc.stop(now + dur + 0.04);
    flutter.stop(now + dur + 0.04);
  }

  function setEnabled(flag) {
    isEnabled = Boolean(flag);
    if (!isEnabled && ctx && purrGain) {
      purrGain.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.05);
      if (purrNoiseGain) {
        purrNoiseGain.gain.setTargetAtTime(0.0001, ctx.currentTime, 0.05);
      }
    }
  }

  return {
    playBoop,
    playPickup,
    playDrop,
    playHappyTrill,
    playSnore,
    updatePetting,
    setEnabled,
    isEnabled: () => isEnabled,
  };
}
