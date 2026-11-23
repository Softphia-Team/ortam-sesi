"""Creates very quiet (-40 dBFS RMS) test sounds for the real-device tests. Standard library only."""
import array
import math
import os
import random
import sys
import wave

FS = 48000


def write(path, mono):
    data = array.array("h")
    for s in mono:
        v = int(max(-1.0, min(1.0, s)) * 32767)
        data.append(v)
        data.append(v)
    with wave.open(path, "wb") as w:
        w.setnchannels(2)
        w.setsampwidth(2)
        w.setframerate(FS)
        w.writeframes(data.tobytes())


def fade(samples, ms=20):
    n = int(FS * ms / 1000)
    for i in range(min(n, len(samples))):
        g = i / n
        samples[i] *= g
        samples[-1 - i] *= g
    return samples


def noise(seconds, rms=0.01, seed=1):
    rnd = random.Random(seed)
    a = rms * math.sqrt(3)
    return fade([rnd.uniform(-a, a) for _ in range(int(FS * seconds))])


def tone(seconds, freq=880, rms=0.01):
    a = rms * math.sqrt(2)
    return fade([a * math.sin(2 * math.pi * freq * i / FS) for i in range(int(FS * seconds))])


if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "assets")
    os.makedirs(out, exist_ok=True)
    write(os.path.join(out, "noise10s.wav"), noise(10, seed=1))
    write(os.path.join(out, "noise8s.wav"), noise(8, seed=2))
    write(os.path.join(out, "blip05.wav"), tone(0.5))
    print("test sounds written:", out)
