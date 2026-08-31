# -*- coding: utf-8 -*-
"""İklim, yağış, sıcaklık, bitki örtüsü ve toprak haritaları."""
from __future__ import annotations

import os

import geopandas as gpd
import numpy as np
from matplotlib.patches import Rectangle

import cizim as C
import harita_temel as H
import zonlar as Z

_IK: dict = {}


def iklim_lcc(alan: str, nx: int = 760):
    """WorldClim katmanını LCC ızgarasına taşır (gerçek veri, 1970-2000 normali)."""
    key = f"{alan}_{nx}"
    if key in _IK:
        return _IK[key]
    import pyproj
    d = np.load(os.path.join(H.VERI, "iklim_tr.npz"))
    A = d[alan].astype(np.float32)
    W, E, S, N = [float(v) for v in d["extent"]]
    h, w = A.shape
    x0, x1, y0, y1 = H.pencere()
    ny = int(nx * (y1 - y0) / (x1 - x0))
    gx, gy = np.meshgrid(np.linspace(x0, x1, nx), np.linspace(y1, y0, ny))
    tf = pyproj.Transformer.from_crs(H.LCC, "EPSG:4326", always_xy=True)
    lon, lat = tf.transform(gx, gy)
    col = (lon - W) / (E - W) * (w - 1)
    row = (N - lat) / (N - S) * (h - 1)
    ok = (col >= 0) & (col <= w - 1) & (row >= 0) & (row <= h - 1)
    out = A[np.clip(np.round(row), 0, h - 1).astype(np.int32),
            np.clip(np.round(col), 0, w - 1).astype(np.int32)]
    out = np.where(ok, out, np.nan)
    _IK[key] = (out, (x0, x1, y0, y1))
    return _IK[key]


def _tr_kirp(ax):
    from matplotlib.path import Path
    from matplotlib.patches import PathPatch
    tr = H.turkiye()
    yollar = [Path(np.array(g.exterior.coords))
              for g in (tr.geoms if tr.geom_type == "MultiPolygon" else [tr])]
    yama = PathPatch(Path.make_compound_path(*yollar), transform=ax.transData,
                     facecolor="none", lw=0)
    ax.add_patch(yama)
    return yama


# ------------------------------------------------------------------ 13
def harita_iklim(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    H.iller().boundary.plot(ax=ax, color="#e2e2e2", linewidth=0.2, zorder=3.4)
    zonlar = Z.iklim_kusaklari()
    taramalar = ["xxx", "\\\\\\", "...", "ooo", "|||", "+++", ""]
    dolgu = ["#d8d8d8", "#ececec", "#ffffff", "#f4f4f4", "#c4c4c4", "#dedede", "#ffffff"]
    C.zon_ciz(ax, zonlar, mod, taramalar=taramalar, dolgu=dolgu, cizgi_lw=0.85)
    acik = ["Her mevsim yağışlı, yazlar serin", "Karadeniz–Akdeniz arası geçiş",
            "Yaz kurak-sıcak, kış ılık-yağışlı", "Yaz kurak-sıcak, kış ılık-yağışlı",
            "Kışlar çok sert ve uzun, kar örtüsü kalıcı",
            "Yaz çok sıcak-kurak, kış ılık (en yüksek sıcaklıklar)",
            "Yaz kurak, kış soğuk; yağış ilkbaharda"]
    C.zon_anahtari(kax, zonlar, mod, sutun=3, taramalar=taramalar, dolgu=dolgu,
                   baslik="İKLİM TİPLERİ", aciklamalar=acik, genislik=0.70, yazi=5.9)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.72, 0.96, 0.28, "UÇ DEĞERLER", [
            "En çok yağış: RİZE (~2.300 mm)", "En az yağış: IĞDIR (~250 mm)",
            "En yüksek sıcaklıklar: Güneydoğu Anadolu",
            "En düşük sıcaklıklar: Doğu Anadolu (Ağrı, Kars)",
            "Yıllık sıcaklık farkı en az: kıyılar (Rize)",
        ], yazi=5.2)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Kuşak sınırları GERÇEK KIYI ÇİZGİSİNDEN tampon alınarak üretilmiştir; "
                      "il sınırlarına bağlı değildir. Geçişler kademelidir, keskin değildir.")


# ------------------------------------------------------------------ 14
def harita_yagis(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.3)
    kad = [0, 300, 400, 500, 700, 1000, 1500, 4000]
    etik = ["<300", "300–400", "400–500", "500–700", "700–1000", "1000–1500", ">1500"]
    tonlar = ["#ffffff", "#ececec", "#d7d7d7", "#bfbfbf", "#a2a2a2", "#7e7e7e", "#565656"]
    if mod == "dolu":
        A, ext = iklim_lcc("yagis")
        A = np.where(H.turkiye_maskesi(A.shape, ext), A, np.nan)
        X = np.linspace(ext[0], ext[1], A.shape[1])
        Y = np.linspace(ext[3], ext[2], A.shape[0])
        ax.contourf(X, Y, A, levels=kad, colors=tonlar, zorder=3.8,
                    hatches=["....", "", "", "", "", "", ""])
        ax.contour(X, Y, A, levels=[400, 1000], colors="#333333",
                   linewidths=0.45, zorder=4.2)
    H.iller().boundary.plot(ax=ax, color="#b9b9b9", linewidth=0.22, zorder=4.4)
    gpd.GeoSeries([H.turkiye()], crs=H.LCC).boundary.plot(ax=ax, color="#141414",
                                                          linewidth=1.25, zorder=6)
    C.choropleth_anahtari(kax, kad, etik, tonlar,
                          baslik="YILLIK TOPLAM YAĞIŞ (mm)" if mod == "dolu"
                          else "YAĞIŞ KADEMELERİNİ YAZ", y=0.78,
                          taramalar=["....", "", "", "", "", "", ""])
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.0, 0.52, 0.68, "", [
            "· En yağışlı yer DOĞU KARADENİZ kıyısıdır (Rize–Hopa): yükselti + nemli hava kütlesi.",
            "· En kurak yerler: Iğdır Ovası, Tuz Gölü çevresi ve Konya Kapalı Havzası.",
            "· Kıyılarda yağış boldur; dağların denize BAKAN yamaçları daha çok yağış alır.",
        ], yazi=5.4)
    else:
        C.bilgi_kutusu(kax, 0.0, 0.52, 0.68, "AÇIKLA", ["." * 100] * 3, yazi=5.4)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Veri: WorldClim v2.1 (1970–2000 normalleri, ~4,6 km ızgara). Izgara "
                      "ortalaması olduğu için Rize ve Antalya'daki yerel uç değerler "
                      "istasyon ölçümlerinden daha yumuşak görünür.")


# ------------------------------------------------------------------ 15
def harita_sicaklik(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.3)
    kad = [-30, -4, 0, 2, 4, 6, 9, 40]
    etik = ["<-4", "-4–0", "0–2", "2–4", "4–6", "6–9", ">9"]
    tonlar = ["#ffffff", "#ededed", "#dadada", "#c2c2c2", "#a6a6a6", "#848484", "#5c5c5c"]
    if mod == "dolu":
        A, ext = iklim_lcc("sicaklik_ocak")
        A = np.where(H.turkiye_maskesi(A.shape, ext), A, np.nan)
        X = np.linspace(ext[0], ext[1], A.shape[1])
        Y = np.linspace(ext[3], ext[2], A.shape[0])
        ax.contourf(X, Y, A, levels=kad, colors=tonlar, zorder=3.8)
        ax.contour(X, Y, A, levels=[0], colors="#111111", linewidths=0.9, zorder=4.3)
    H.iller().boundary.plot(ax=ax, color="#b9b9b9", linewidth=0.22, zorder=4.4)
    gpd.GeoSeries([H.turkiye()], crs=H.LCC).boundary.plot(ax=ax, color="#141414",
                                                          linewidth=1.25, zorder=6)
    C.choropleth_anahtari(kax, kad, etik, tonlar,
                          baslik="OCAK AYI ORTALAMA SICAKLIĞI (°C) — kalın çizgi 0 °C izotermi",
                          y=0.78)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.0, 0.52, 0.78, "", [
            "· 0 °C izotermi Türkiye'yi ikiye böler: kuzeydoğuda kışlar donlu, kıyılarda ılıktır.",
            "· Sıcaklığı belirleyen üç etken: ENLEM, YÜKSELTİ ve DENİZ ETKİSİ (karasallık).",
            "· Aynı enlemde bile Doğu Anadolu, yükseltisi nedeniyle çok daha soğuktur.",
            "· Kıyılarda yıllık sıcaklık farkı azdır; iç kesimlerde çok yüksektir.",
        ], yazi=5.4)
    else:
        C.bilgi_kutusu(kax, 0.0, 0.52, 0.78, "AÇIKLA", ["." * 104] * 4, yazi=5.4)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Veri: WorldClim v2.1 (1970–2000 Ocak ortalaması)")


# ------------------------------------------------------------------ 16
def harita_bitki(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    H.iller().boundary.plot(ax=ax, color="#e6e6e6", linewidth=0.18, zorder=3.4)
    zonlar = Z.bitki_kusaklari()
    taramalar = ["...", "///", "xxx", "\\\\\\", ""]
    dolgu = ["#ffffff", "#e6e6e6", "#cfcfcf", "#f0f0f0", "#fafafa"]
    C.zon_ciz(ax, zonlar, mod, taramalar=taramalar, dolgu=dolgu, cizgi_lw=0.85)
    if mod == "dolu":
        Z.yukseklik_maskesi(ax, esik=2000, renk="#8c8c8c", alpha=0.5, zorder=4.6)
        ax.text(0.5, 0.972, "Koyu gri gölge: 2000 m ÜZERİ — DAĞ (ALPİN) ÇAYIRLARI",
                transform=ax.transAxes, fontsize=5.6, ha="center", va="top", zorder=9,
                bbox=dict(boxstyle="round,pad=0.3", facecolor="white",
                          edgecolor="#c5c5c5", lw=0.5, alpha=0.94))
    acik = ["Kısa boylu, kurağa dayanıklı çalı (0–800 m)",
            "Makinin üstünde, 800–1200 m",
            "0–1000 m yayvan (kayın-gürgen), üstü iğne yapraklı (ladin-köknar)",
            "Kuru orman ve psödomaki (yalancı maki)",
            "Yazın sararan ot topluluğu — kurak iç bölgeler"]
    C.zon_anahtari(kax, zonlar, mod, sutun=3, taramalar=taramalar, dolgu=dolgu,
                   baslik="BİTKİ ÖRTÜSÜ", aciklamalar=acik, genislik=0.72, yazi=5.9)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.74, 0.96, 0.26, "SINAV NOTU", [
            "Maki: Akdeniz ikliminin doğal bitkisi",
            "Garig: tahrip edilmiş maki",
            "Psödomaki: Karadeniz'de maki benzeri",
            "Longoz ormanı: İğneada, Acarlar",
            "2000 m üstü: dağ çayırı (yaylacılık)",
        ], yazi=5.2)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Kuşaklar gerçek kıyı çizgisinden; dağ çayırı sınırı gerçek yükselti "
                      "verisinden (2000 m) üretilmiştir")


# ------------------------------------------------------------------ 17
def harita_toprak(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    H.iller().boundary.plot(ax=ax, color="#e6e6e6", linewidth=0.18, zorder=3.4)
    zonlar = Z.toprak_kusaklari()
    taramalar = ["...", "///", "xxx", ""]
    dolgu = ["#e8e8e8", "#d2d2d2", "#b6b6b6", "#ffffff"]
    C.zon_ciz(ax, zonlar, mod, taramalar=taramalar, dolgu=dolgu, cizgi_lw=0.85)
    acik = ["Kalker üzerinde, demir oksitten kırmızı",
            "Nemli ve ormanlık alanların toprağı",
            "Çayır altında, humusça en zengin toprak",
            "Yarı kurak bozkır alanlarının toprağı"]
    C.zon_anahtari(kax, zonlar, mod, sutun=2, taramalar=taramalar, dolgu=dolgu,
                   baslik="ZONAL (İKLİME BAĞLI) TOPRAKLAR", aciklamalar=acik,
                   genislik=0.52, yazi=5.9)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.54, 0.96, 0.46, "AZONAL ve İNTRAZONAL TOPRAKLAR", [
            "AZONAL (taşınmış, katmanlaşmamış):",
            "  · Alüvyal → ova ve deltalar (Çukurova, Bafra, Çarşamba, Menderes) — en verimli",
            "  · Kolüvyal → dağ etekleri · Litosol → dik yamaçlar · Regosol → volkanik kum (Nevşehir)",
            "İNTRAZONAL (yerel koşullara bağlı):",
            "  · Halomorfik (tuzlu-alkali) → Tuz Gölü çevresi, Konya Kapalı Havzası, Iğdır",
            "  · Hidromorfik → taban suyu yüksek bataklıklar   · Kalsimorfik → vertisol, rendzina",
        ], yazi=5.1)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Zonal kuşaklar iklim kuşaklarıyla birlikte üretilmiştir; "
                      "azonal topraklar ova/vadi tabanlarında noktasal dağılır.")
