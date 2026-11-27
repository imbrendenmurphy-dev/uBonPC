// uBonPC floppy drive sounds, made up as they play (no sample files): the head stepping (a click per track, a buzz
// when seeking), the head loading (a clunk when the motor starts) and unloading, and the spindle motor's hum, which
// spins up on a command and runs down about 2.5 seconds after the drive goes quiet. The FDC reports steps and
// commands with their T-state; the audio mixes Sample(t) in for every output sample.
// With fast disk access the controller steps every 300 T, far quicker than a drive can; steps are queued and played
// at least 3 ms apart (a real drive's fastest rate), so a seek still sounds like a seek.
using System;
using System.Collections.Generic;

public sealed class DiskSound
{
    public bool On;
    public double Volume = 1.0;
    readonly long hz;
    readonly double tsec;                  // seconds per T-state
    const double MOTOR_RUN = 2.5, MIN_STEP = 0.003;

    long motorUntil = long.MinValue;
    double motor;                          // 0 stopped .. 1 at speed
    bool headLoaded;
    readonly Queue<long> steps = new Queue<long>();
    long lastStep = long.MinValue;

    struct Shot { public int Kind; public double Age, Amp; }
    readonly List<Shot> shots = new List<Shot>();
    const int K_STEP = 0, K_LOAD = 1, K_UNLOAD = 2;

    uint seed = 12345; double lp1, lp2, motorPhase, rotPhase;

    public DiskSound(long cpuHz) { hz = cpuHz; tsec = 1.0 / cpuHz; }

    public void Reset() { motorUntil = long.MinValue; motor = 0; headLoaded = false; steps.Clear(); shots.Clear(); lastStep = long.MinValue; }

    /// <summary>A controller command at T t: the motor starts (or keeps running) and the head loads.</summary>
    public void Command(long t)
    {
        if (!On) return;
        bool wasOff = t >= motorUntil;
        motorUntil = t + (long)(MOTOR_RUN * hz);
        if (wasOff || !headLoaded) { headLoaded = true; shots.Add(new Shot { Kind = K_LOAD, Age = -0.02, Amp = 0.55 }); }
    }
    /// <summary>The drive is still in use at T t (a command finished): keep the motor running.</summary>
    public void Busy(long t) { if (On && t + (long)(MOTOR_RUN * hz) > motorUntil) motorUntil = t + (long)(MOTOR_RUN * hz); }

    /// <summary>One head step at T t.</summary>
    public void Step(long t)
    {
        if (!On) return;
        if (steps.Count > 120) return;
        long at = Math.Max(t, lastStep == long.MinValue ? t : lastStep + (long)(MIN_STEP * hz));
        steps.Enqueue(at); lastStep = at;
        Busy(t);
    }

    double Noise() { seed = seed * 1664525 + 1013904223; return ((seed >> 8) & 0xFFFF) / 32768.0 - 1.0; }

    /// <summary>The drive's sound for the output sample at T t (about -1..1 at full volume).</summary>
    public double Sample(long t, double dt)
    {
        if (!On) return 0;
        while (steps.Count > 0 && steps.Peek() <= t) { steps.Dequeue(); shots.Add(new Shot { Kind = K_STEP, Age = 0, Amp = 0.8 + 0.1 * Noise() }); }
        bool running = t < motorUntil;
        if (!running && headLoaded && motor > 0.95) { headLoaded = false; shots.Add(new Shot { Kind = K_UNLOAD, Age = 0, Amp = 0.3 }); }
        double target = running ? 1 : 0;
        motor += (target - motor) * dt / (running ? 0.35 : 0.7);
        if (motor < 0.001 && !running) motor = 0;
        if (motor == 0 && shots.Count == 0) return 0;

        double s = 0;
        if (motor > 0)
        {
            // the spindle: a low hum that rises with the speed, belt / bearing whirr, and a 5 Hz (300 rpm) wobble
            motorPhase += dt * (40 + 60 * motor); if (motorPhase > 1) motorPhase -= 1;
            rotPhase += dt * 5 * motor; if (rotPhase > 1) rotPhase -= 1;
            double n = Noise();
            lp1 += (n - lp1) * 0.08; lp2 += (lp1 - lp2) * 0.08;
            double hum = Math.Sin(2 * Math.PI * motorPhase) * 0.5 + Math.Sin(4 * Math.PI * motorPhase) * 0.2;
            s += motor * (hum * 0.05 + lp2 * 0.3) * (1 + 0.3 * Math.Sin(2 * Math.PI * rotPhase));
        }
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var sh = shots[i];
            double a = sh.Age, v = 0;
            if (a >= 0)
            {
                switch (sh.Kind)
                {
                    case K_STEP:      // a sharp metallic tick and the carriage's thump
                        v = Math.Exp(-a / 0.0018) * (0.55 * Math.Sin(2 * Math.PI * 2300 * a) + 0.45 * Noise())
                          + 0.6 * Math.Exp(-a / 0.007) * Math.Sin(2 * Math.PI * 160 * a);
                        if (a > 0.04) { shots.RemoveAt(i); continue; }
                        break;
                    case K_LOAD:      // the head pad hitting the disk: a dull clunk
                        v = Math.Exp(-a / 0.014) * Math.Sin(2 * Math.PI * 95 * a) + 0.5 * Math.Exp(-a / 0.003) * Noise();
                        if (a > 0.09) { shots.RemoveAt(i); continue; }
                        break;
                    default:          // the head lifting off
                        v = Math.Exp(-a / 0.008) * Math.Sin(2 * Math.PI * 130 * a) + 0.3 * Math.Exp(-a / 0.002) * Noise();
                        if (a > 0.06) { shots.RemoveAt(i); continue; }
                        break;
                }
                s += v * sh.Amp;
            }
            sh.Age = a + dt; shots[i] = sh;
        }
        return s * Volume;
    }
}
