# -*- coding: utf-8 -*-
"""AWS Terrain Tiles (terrarium) z9 karolarından Türkiye yükselti dizisi üretir.

Çalıştır:  python3 src/hazirla_dem.py
Çıktı:     veri/dem_tr.npz  (elev int16, extent W/E/S/N, z)
Karolar Web Mercator dizilimindedir; extent buna göre yazılır ve okuma
tarafında (harita_temel.dem_lcc) enlem doğrusal olmayacak şekilde çözülür.
"""
import math
import os
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import numpy as np
from PIL import Image

VERI = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "veri")
Z, X0, X1, Y0, Y1 = 9, 291, 321, 188, 202     # Türkiye'yi kaplayan karo aralığı
KARO = os.path.join(VERI, "tiles9")
URL = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png"


def indir(is_):
    x, y = is_
    yol = os.path.join(KARO, f"{x}_{y}.png")
    if os.path.exists(yol) and os.path.getsize(yol) > 0:
        return True
    for _ in range(3):
        try:
            urllib.request.urlretrieve(URL.format(z=Z, x=x, y=y), yol)
            return True
        except Exception:
            pass
    return False


def main():
    os.makedirs(KARO, exist_ok=True)
    isler = [(x, y) for x in range(X0, X1 + 1) for y in range(Y0, Y1 + 1)]
    with ThreadPoolExecutor(16) as ex:
        sonuc = list(ex.map(indir, isler))
    print(f"indirilemeyen karo: {sonuc.count(False)}")

    w, h = (X1 - X0 + 1) * 256, (Y1 - Y0 + 1) * 256
    tuval = np.zeros((h, w, 3), dtype=np.uint8)
    for x in range(X0, X1 + 1):
        for y in range(Y0, Y1 + 1):
            yol = os.path.join(KARO, f"{x}_{y}.png")
            if os.path.exists(yol):
                tuval[(y - Y0) * 256:(y - Y0 + 1) * 256,
                      (x - X0) * 256:(x - X0 + 1) * 256] = np.array(
                          Image.open(yol).convert("RGB"))
    r, g, b = (tuval[..., i].astype(np.float32) for i in range(3))
    elev = (r * 256.0 + g + b / 256.0) - 32768.0        # terrarium çözümü

    def lon(x):
        return x / 2 ** Z * 360.0 - 180.0

    def lat(y):
        return math.degrees(math.atan(math.sinh(math.pi - 2 * math.pi * y / 2 ** Z)))

    ext = (lon(X0), lon(X1 + 1), lat(Y1 + 1), lat(Y0))
    np.savez_compressed(os.path.join(VERI, "dem_tr.npz"),
                        elev=elev.astype(np.int16), extent=np.array(ext), z=Z)
    print(f"dem_tr.npz yazıldı · {elev.shape} · en yüksek {elev.max():.0f} m "
          f"(Ağrı Dağı 5137 m)")


if __name__ == "__main__":
    main()
