# -*- coding: utf-8 -*-
"""Konum, idari bölünüş ve coğrafi bölge haritaları."""
from __future__ import annotations

import geopandas as gpd
import numpy as np
from matplotlib.lines import Line2D
from matplotlib.patches import Rectangle
from shapely.ops import unary_union

import cizim as C
import harita_temel as H
import veri_idari as V


# ------------------------------------------------------------------ 01
def harita_matematiksel_konum(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    x0, x1, y0, y1 = H.pencere()

    # paralel ve meridyen ağı — gerçek projeksiyonda eğri çizilir
    import matplotlib.transforms as mtr
    tx = mtr.blended_transform_factory(ax.transAxes, ax.transData)   # x=eksen, y=veri
    ty = mtr.blended_transform_factory(ax.transData, ax.transAxes)
    for lat in range(35, 44):
        lo = np.linspace(24, 47, 120)
        xs, ys = H.xy(lo, np.full_like(lo, float(lat)))
        ax.plot(xs, ys, color="#9aa3aa", lw=0.45,
                ls=(0, (4, 3)) if lat % 2 else "-", zorder=3.2)
        _, ye = H.xy(35.5, float(lat))
        if y0 + 12000 < ye < y1 - 12000:
            ax.text(0.006, ye, f"{lat}°K", transform=tx, fontsize=5.2,
                    color="#5f6greyc" if False else "#5f676d", va="center",
                    ha="left", zorder=9.3,
                    bbox=dict(boxstyle="square,pad=0.15", facecolor="white",
                              edgecolor="none", alpha=0.8))
    for lon in range(26, 46, 2):
        la = np.linspace(34.5, 43.5, 60)
        xs, ys = H.xy(np.full_like(la, float(lon)), la)
        ax.plot(xs, ys, color="#9aa3aa", lw=0.45, ls=(0, (4, 3)), zorder=3.2)
        xe, _ = H.xy(float(lon), 39.0)
        if x0 + 12000 < xe < x1 - 12000:
            ax.text(xe, 0.006, f"{lon}°D", transform=ty, fontsize=5.2,
                    color="#5f676d", ha="center", va="bottom", zorder=9.3,
                    bbox=dict(boxstyle="square,pad=0.15", facecolor="white",
                              edgecolor="none", alpha=0.8))

    # 36-42 K ve 26-45 D kuşağını vurgula
    for lat in (36, 42):
        lo = np.linspace(24, 47, 120)
        xs, ys = H.xy(lo, np.full_like(lo, float(lat)))
        ax.plot(xs, ys, color="#1a1a1a", lw=1.0, zorder=3.6)
    for lon in (26, 45):
        la = np.linspace(34.5, 43.5, 60)
        xs, ys = H.xy(np.full_like(la, float(lon)), la)
        ax.plot(xs, ys, color="#1a1a1a", lw=1.0, zorder=3.6)

    kayitlar = []
    for yon, yer, lo, la, koord, dx, dy in V.UC_NOKTALAR:
        x, y = H.xy(lo, la)
        ax.scatter([x], [y], s=76, marker="*", facecolor="white",
                   edgecolor="#111111", linewidth=1.0, zorder=8)
        if mod == "dolu":
            ax.annotate(f"{yon}\n{yer}\n{koord}", xy=(x, y), xytext=(x + dx, y + dy),
                        fontsize=5.3, ha="center", va="center", zorder=8.5,
                        arrowprops=dict(arrowstyle="-", lw=0.5, color="#555"),
                        bbox=dict(boxstyle="round,pad=0.3", facecolor="white",
                                  edgecolor="#b0b0b0", lw=0.5))
        kayitlar.append((f"{yon}: {yer} — {koord}", lo, la))

    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    satirlar = [
        "Türkiye 36°–42° kuzey paralelleri ile 26°–45° doğu meridyenleri arasındadır.",
        "Kuzey Yarım Küre'de, Ekvator ile Kuzey Kutbu arasında; ORTA KUŞAK'ta yer alır.",
        "Doğu–batı yönünde 19°'lik meridyen farkı vardır → 19 × 4 = 76 dakika yerel saat farkı.",
        "Kuzey–güney 6°'lik enlem farkı → yaklaşık 666 km; doğu–batı yaklaşık 1565 km.",
        "Başlangıç meridyeninin doğusunda olduğu için saat dilimi GMT+3'tür.",
    ]
    if mod == "dolu":
        C.liste_paneli(kax, ["★"] * 4, [k[0] for k in kayitlar], "dolu", sutun=1,
                       baslik="UÇ NOKTALAR", yazi=6.0, genislik=0.48)
        C.bilgi_kutusu(kax, 0.52, 0.98, 0.46, "MATEMATİKSEL KONUMUN SONUÇLARI", satirlar)
    else:
        C.liste_paneli(kax, ["★"] * 4, [""] * 4, "bos", sutun=1,
                       baslik="UÇ NOKTALARI YAZ (yön · yer · koordinat)", yazi=6.0,
                       genislik=0.48)
        C.bilgi_kutusu(kax, 0.52, 0.98, 0.46, "MATEMATİKSEL KONUMUN SONUÇLARINI YAZ",
                       ["." * 92] * 5)
    C.harita_notu(ax, "Uç nokta koordinatları il sınırı verisiyle doğrulanmıştır · "
                      "Kırklareli'nin kuzey ve Hakkâri'nin doğu sınırı bu değerlere 1 km'den "
                      "yakındır; sınavda beklenen cevaplar İnceburun ve Dilucu'dur")


# ------------------------------------------------------------------ 02
def harita_komsular(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.3, komsu_etiket=(mod == "dolu"))
    kayitlar = [(f"{a} ({ilad})", lo, la) for a, lo, la, u, ilad in V.SINIR_KAPILARI]
    C.numarali_noktalar(ax, kax, kayitlar, mod, sutun=4,
                        baslik="SINIR KAPILARI — numaranın karşısına kapı adını ve ilini yaz",
                        isaret="s", boyut=46, numara_yazi=4.6)
    H.denizleri_yaz(ax, fontsize=7.6); H.olcek_kuzey(ax)
    if mod == "dolu":
        sat = [f"{u:24} ~{km:>4} km  · {n}" for u, km, n in V.KOMSU_SINIR]
        ax.text(0.012, 0.30, "KARA SINIRI KOMŞULARI\n" + "\n".join(sat),
                transform=ax.transAxes, fontsize=5.0, va="top", family="DejaVu Sans",
                zorder=9, bbox=dict(boxstyle="round,pad=0.4", facecolor="white",
                                    edgecolor="#c0c0c0", lw=0.6, alpha=0.95))
    C.harita_notu(ax, "Sınır uzunlukları yaklaşıktır · Ermenistan sınır kapıları kapalıdır")


# ------------------------------------------------------------------ 03
def harita_iller(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=True, il_lw=0.45, ulke_lw=1.25)
    il = H.iller()
    for ad, plaka in V.IL_PLAKA.items():
        p = il.loc[ad].geometry.representative_point()
        if mod == "dolu":
            ax.text(p.x, p.y, str(plaka), fontsize=4.6, ha="center", va="center",
                    fontweight="bold", color="#111111", zorder=7)
        else:
            ax.scatter([p.x], [p.y], s=4, marker="o", facecolor="#9b9b9b",
                       edgecolor="none", zorder=6)
    if mod == "dolu":
        adlar = [f"{k:02d} {v}" for k, v in sorted(V.PLAKA.items())]
        C.liste_paneli(kax, [""] * 81, adlar, "dolu", sutun=9,
                       baslik="PLAKA KODLARI", yazi=4.9)
    else:
        C.liste_paneli(kax, [f"{k:02d}" for k in sorted(V.PLAKA)], [""] * 81, "bos",
                       sutun=9, baslik="PLAKA KODUNUN KARŞISINA İL ADINI YAZ", yazi=4.9)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


# ------------------------------------------------------------------ 04
def _bolge_geom():
    il = H.iller()
    out = []
    for b in V.BOLGELER:
        g = unary_union([il.loc[i].geometry for i in V._B[b]]).buffer(300).buffer(-300)
        out.append((b + " Bölgesi", g.buffer(0)))
    return out


def harita_bolgeler(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    H.iller().boundary.plot(ax=ax, color="#dcdcdc", linewidth=0.22, zorder=3.5)
    zonlar = _bolge_geom()
    taramalar = ["", "///", "...", "\\\\\\", "xxx", "|||", "+++"]
    C.zon_ciz(ax, zonlar, mod, taramalar=taramalar, numarali=True)
    C.zon_anahtari(kax, zonlar, mod, sutun=3, taramalar=taramalar,
                   baslik="COĞRAFİ BÖLGELER (1941 Birinci Türk Coğrafya Kongresi)",
                   genislik=0.50)
    if mod == "dolu":
        sira = sorted(V.BOLGE_ALAN_PAY.items(), key=lambda kv: -kv[1])
        C.bilgi_kutusu(kax, 0.52, 0.96, 0.20, "YÜZÖLÇÜMÜ SIRASI (resmî)",
                       [f"{i}. {b}  ~%{p}" for i, (b, p) in enumerate(sira, 1)], yazi=5.0)
        sat = [f"· {a} → {b}" for a, b in V.IKI_BOLGELI_ILLER[:6]]
        C.bilgi_kutusu(kax, 0.74, 0.96, 0.26,
                       "BİRDEN FAZLA BÖLGEDE TOPRAĞI OLAN İLLER", sat, yazi=5.0)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "DİKKAT: Gerçek bölge sınırları İL SINIRLARINI KESER. Bu harita, her ilin ağırlıklı "
                      "olarak bulunduğu bölgeyi gösteren il bazlı şematik gösterimdir; bu yüzden taralı "
                      "alanların oranları resmî yüzölçümü paylarıyla birebir aynı değildir.")


# ------------------------------------------------------------------ 05
def harita_bolumler(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    H.iller().boundary.plot(ax=ax, color="#e0e0e0", linewidth=0.2, zorder=3.5)
    il = H.iller()
    zonlar = []
    for ad, bolge, iller in V.BOLUMLER:
        g = unary_union([il.loc[i].geometry for i in iller]).buffer(300).buffer(-300)
        zonlar.append((ad, g.buffer(0)))
    taramalar = ["", "///", "...", "\\\\\\", "xxx", "|||", "+++", "ooo", "---", "//", "\\\\"]
    dolgu = ["#ffffff", "#efefef", "#e0e0e0", "#d0d0d0", "#c1c1c1", "#b2b2b2"]
    C.zon_ciz(ax, zonlar, mod, taramalar=taramalar, dolgu=dolgu, cizgi_lw=0.7)
    # bölge sınırlarını kalın çiz
    for _, g in _bolge_geom():
        gpd.GeoSeries([g], crs=H.LCC).boundary.plot(ax=ax, color="#161616",
                                                    linewidth=1.15, zorder=6.2)
    acik = [b for _, b, _ in V.BOLUMLER]
    C.zon_anahtari(kax, zonlar, mod, sutun=4, taramalar=taramalar, dolgu=dolgu,
                   baslik="21 BÖLÜM — kalın çizgi bölge sınırını, ince çizgi bölüm sınırını gösterir",
                   aciklamalar=acik, yazi=5.5)
    H.olcek_kuzey(ax)
    C.harita_notu(ax, "Bölüm sınırları da il sınırlarıyla birebir örtüşmez — il bazlı şematik gösterim")
