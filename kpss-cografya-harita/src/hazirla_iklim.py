# -*- coding: utf-8 -*-
"""WorldClim v2.1 aylık rasterlerinden Türkiye iklim dizilerini üretir.

Çalıştır (veri/ klasöründe wc2.1_2.5m_prec.zip ve wc2.1_2.5m_tavg.zip varken):
    python3 src/hazirla_iklim.py
Çıktı: veri/iklim_tr.npz  (yagis, sicaklik_yil, sicaklik_ocak, sicaklik_temmuz, extent)
"""
import io
import os
import zipfile

import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
VERI = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "veri")
W, E, S, N = 24.5, 46.5, 34.8, 43.2          # Türkiye penceresi
NODATA = -3000                                # WorldClim nodata = -32768


def aylik(zip_yolu):
    with zipfile.ZipFile(zip_yolu) as z:
        adlar = sorted(n for n in z.namelist() if n.endswith(".tif"))
        yigin, ext, dilim = None, None, None
        for i, ad in enumerate(adlar):
            with z.open(ad) as f:
                im = Image.open(io.BytesIO(f.read())); im.load()
            a = np.array(im, dtype=np.float32)
            if ext is None:
                h, w = a.shape
                c0, c1 = int((W + 180) / 360 * w), int((E + 180) / 360 * w)
                r0, r1 = int((90 - N) / 180 * h), int((90 - S) / 180 * h)
                ext = (c0 / w * 360 - 180, c1 / w * 360 - 180,
                       90 - r1 / h * 180, 90 - r0 / h * 180)
                dilim = (r0, r1, c0, c1)
                yigin = np.empty((12, r1 - r0, c1 - c0), dtype=np.float32)
            r0, r1, c0, c1 = dilim
            s = a[r0:r1, c0:c1].astype(np.float32)
            s[s < NODATA] = np.nan
            yigin[i] = s
    return yigin, ext


def main():
    prec, ext = aylik(os.path.join(VERI, "wc2.1_2.5m_prec.zip"))
    tavg, _ = aylik(os.path.join(VERI, "wc2.1_2.5m_tavg.zip"))
    yagis = np.nansum(np.where(np.isnan(prec), 0, prec), axis=0)
    yagis[np.all(np.isnan(prec), axis=0)] = np.nan
    np.savez_compressed(
        os.path.join(VERI, "iklim_tr.npz"),
        yagis=yagis.astype(np.float32),
        sicaklik_yil=np.nanmean(tavg, axis=0).astype(np.float32),
        sicaklik_ocak=tavg[0].astype(np.float32),
        sicaklik_temmuz=tavg[6].astype(np.float32),
        extent=np.array(ext))
    print(f"iklim_tr.npz yazıldı · {yagis.shape} · yağış "
          f"{np.nanmin(yagis):.0f}–{np.nanmax(yagis):.0f} mm")


if __name__ == "__main__":
    main()
