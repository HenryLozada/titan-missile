using System.Runtime.InteropServices;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace Titan.App.Services;

public enum Sfx { Pulse, Absorb, Gulp, Tick, Boost, Release, Persist }

/// <summary>
/// Every sound is synthesized once at start-up (oscillators, filtered noise, exponential envelopes — the same recipe as
/// the design prototype) and played through XAudio2. No audio files ship with the app.
/// </summary>
public sealed class SoundEngine : IDisposable
{
    private const int Rate = 48000;
    private readonly IXAudio2? _xa;
    private readonly IXAudio2MasteringVoice? _master;
    private readonly Dictionary<Sfx, (nint Ptr, int Bytes, double Seconds)> _bank = [];
    private readonly List<(IXAudio2SourceVoice Voice, DateTime End)> _playing = [];
    private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);

    public bool Enabled { get; set; } = true;

    public SoundEngine()
    {
        try
        {
            _xa = XAudio2.XAudio2Create();
            _master = _xa.CreateMasteringVoice();
            foreach (Sfx s in Enum.GetValues<Sfx>()) _bank[s] = Upload(Render(s));
        }
        catch
        {
            _xa = null;   // no audio device: stay silent
        }
    }

    public void Play(Sfx s)
    {
        if (!Enabled || _xa is null || !_bank.TryGetValue(s, out var clip)) return;
        Reap();
        var voice = _xa.CreateSourceVoice(_format);
        voice.SubmitSourceBuffer(new AudioBuffer(clip.Ptr, (uint)clip.Bytes, BufferFlags.EndOfStream));
        voice.Start();
        _playing.Add((voice, DateTime.UtcNow.AddSeconds(clip.Seconds + .2)));
    }

    private void Reap()
    {
        var now = DateTime.UtcNow;
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            if (_playing[i].End > now) continue;
            _playing[i].Voice.DestroyVoice();
            _playing[i].Voice.Dispose();
            _playing.RemoveAt(i);
        }
    }

    private static (nint, int, double) Upload(float[] samples)
    {
        int bytes = samples.Length * sizeof(float);
        nint p = Marshal.AllocHGlobal(bytes);
        Marshal.Copy(samples, 0, p, samples.Length);
        return (p, bytes, samples.Length / (double)Rate);
    }

    // ------------------------------------------------------------------ synthesis
    private static float[] Render(Sfx s)
    {
        var mix = new Synth(s switch { Sfx.Boost => 2.8, Sfx.Pulse => 1.6, Sfx.Release => 1.4, _ => 1.1 });
        switch (s)
        {
            case Sfx.Pulse:      // Reposo: deep, fluid heartbeat
                mix.Tone(Wave.Sine, 56, 44, 0, 1.5, .08, .2);
                mix.Hiss(Filter.Low, 0, .9, 170, 90, .7, .01, .2);
                break;
            case Sfx.Absorb:     // Análisis: magnetic hum rising
                mix.Tone(Wave.Sine, 90, 340, 0, .95, .05, .08, vibratoHz: 18, vibratoDepth: 12);
                mix.Hiss(Filter.Band, 0, .95, 300, 2400, 9, .035, .1);
                break;
            case Sfx.Gulp:       // Limpiar memoria: violent suck + thump
                mix.Hiss(Filter.Band, 0, .3, 2400, 220, 6, .08, .005);
                mix.Tone(Wave.Sine, 180, 50, .05, .35, .14, .01);
                break;
            case Sfx.Tick:
                mix.Tone(Wave.Sine, 1320, 1320, 0, .05, .012, .002);
                break;
            case Sfx.Boost:      // Max boost: metallic click, bell partials, low hum
                mix.Hiss(Filter.High, 0, .03, 3200, 3000, .7, .09, .001);
                double[] partials = [1, 2.76, 5.4, 8.93];
                for (int i = 0; i < partials.Length; i++)
                    mix.Tone(Wave.Sine, 392 * partials[i], 392 * partials[i], 0, 1.3 / (1 + i * .6), .04 / (1 + i), .002);
                mix.Tone(Wave.Saw, 55, 52, 0, 2.6, .04, .08, lowpass: 200);
                break;
            case Sfx.Release:    // Liberación: fluid wave falling away
                mix.Hiss(Filter.Low, 0, 1.3, 1600, 150, 1, .06, .05);
                mix.Tone(Wave.Sine, 330, 110, 0, 1, .03, .05);
                break;
            case Sfx.Persist:    // Persistencia: dense clunk
                mix.Tone(Wave.Sine, 72, 44, 0, .45, .15, .005);
                mix.Hiss(Filter.Band, 0, .025, 1200, 1100, 3, .07, .001);
                break;
        }
        return mix.Finish(.4f);
    }

    private enum Wave { Sine, Saw }
    private enum Filter { Low, Band, High }

    private sealed class Synth(double seconds)
    {
        private readonly float[] _buf = new float[(int)(seconds * Rate)];
        private readonly Random _rng = new(7);

        /// <summary>WebAudio-style envelope: exponential rise from ~0 to peak, exponential fall to ~0 at dur.</summary>
        private static double Env(double x, double peak, double attack, double dur)
        {
            const double floor = .0001;
            if (x < 0 || x > dur) return 0;
            if (x < attack) return floor * Math.Pow(peak / floor, x / attack);
            return peak * Math.Pow(floor / peak, (x - attack) / Math.Max(1e-6, dur - attack));
        }

        public void Tone(Wave wave, double f0, double f1, double start, double dur, double peak, double attack,
                         double lowpass = 0, double vibratoHz = 0, double vibratoDepth = 0)
        {
            var lp = lowpass > 0 ? new Biquad() : null;
            lp?.Set(Filter.Low, lowpass, .707);
            double phase = 0;
            int a = (int)(start * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && a + i < _buf.Length; i++)
            {
                double x = i / (double)Rate;
                double f = f0 * Math.Pow(f1 / f0, x / dur) + vibratoDepth * Math.Sin(2 * Math.PI * vibratoHz * x);
                phase += f / Rate;
                phase -= Math.Floor(phase);
                double v = wave == Wave.Sine ? Math.Sin(2 * Math.PI * phase) : 2 * phase - 1;
                if (lp is not null) v = lp.Process(v);
                _buf[a + i] += (float)(v * Env(x, peak, attack, dur));
            }
        }

        public void Hiss(Filter type, double start, double dur, double f0, double f1, double q, double peak, double attack)
        {
            var bq = new Biquad();
            int a = (int)(start * Rate), n = (int)(dur * Rate);
            for (int i = 0; i < n && a + i < _buf.Length; i++)
            {
                double x = i / (double)Rate;
                if ((i & 15) == 0) bq.Set(type, f0 * Math.Pow(f1 / f0, x / dur), q);
                double v = bq.Process(_rng.NextDouble() * 2 - 1);
                _buf[a + i] += (float)(v * Env(x, peak, attack, dur));
            }
        }

        public float[] Finish(float gain)
        {
            for (int i = 0; i < _buf.Length; i++) _buf[i] = MathF.Tanh(_buf[i] * gain * 2.5f) * .9f;
            return _buf;
        }
    }

    /// <summary>RBJ cookbook biquad.</summary>
    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;

        public void Set(Filter type, double freq, double q)
        {
            double w = 2 * Math.PI * Math.Clamp(freq, 10, Rate * .45) / Rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
            double b0, b1, b2, a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
            switch (type)
            {
                case Filter.Low: b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = b0; break;
                case Filter.High: b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = b0; break;
                default: b0 = alpha; b1 = 0; b2 = -alpha; break;
            }
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public double Process(double x)
        {
            double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }
    }

    public void Dispose()
    {
        foreach (var (voice, _) in _playing) { voice.DestroyVoice(); voice.Dispose(); }
        _master?.Dispose();
        _xa?.Dispose();
        foreach (var clip in _bank.Values) Marshal.FreeHGlobal(clip.Ptr);
    }
}
