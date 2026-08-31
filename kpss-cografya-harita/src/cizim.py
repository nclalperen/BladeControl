# -*- coding: utf-8 -*-
"""Yeniden kullanılabilir çizim yardımcıları (siyah-beyaz baskıya göre)."""
from __future__ import annotations

import numpy as np
import geopandas as gpd
import matplotlib as mpl
from matplotlib import pyplot as plt
from matplotlib.lines import Line2D
from matplotlib.patches import Rectangle

import harita_temel as H

ISARETLER = ["o", "s", "^", "D", "v", "P", "*", "X", "h", "p", "8", "<", ">", "d", "H"]

PANEL_MM = 274.0   # anahtar panelinin gerçek genişliği (A4 yatay, kenar boşlukları düşülmüş)


def bos_satir(kax, x, y, genislik, dy=0.018, renk="#c4c4c4", lw=0.5):
    """Dilsiz sayfada elle doldurulacak yazı çizgisi (noktadan daha okunaklı)."""
    kax.plot([x, x + genislik], [y - dy, y - dy], color=renk, lw=lw,
             solid_capstyle="butt", clip_on=False, zorder=2)


# ------------------------------------------------------------ NOKTALAR
def numarali_noktalar(ax, kax, kayitlar, mod, sutun=4, baslik="",
                      isaret="o", boyut=44, yazi=6.0, anahtar_yazi=6.1,
                      numara_yazi=4.6, renk="#1f1f1f"):
    """kayitlar: [(etiket, lon, lat)] — haritaya numaralı işaret, panele liste.

    'dolu' modda liste dolu, 'bos' modda boş satır gelir; numaralar iki
    sürümde de aynıdır, böylece dolu sayfa dilsiz sayfanın cevap anahtarıdır.
    """
    for i, (ad, lon, lat) in enumerate(kayitlar, 1):
        x, y = H.xy(lon, lat)
        ax.scatter([x], [y], s=boyut, marker=isaret, facecolor="white",
                   edgecolor=renk, linewidth=0.85, zorder=7)
        ax.text(x, y, str(i), fontsize=numara_yazi, ha="center", va="center",
                zorder=7.5, color=renk, fontweight="bold")
    liste_paneli(kax, [f"{i}" for i in range(1, len(kayitlar) + 1)],
                 [k[0] for k in kayitlar], mod, sutun, baslik, anahtar_yazi)


def liste_paneli(kax, numaralar, adlar, mod, sutun=4, baslik="", yazi=6.1,
                 ek=None, genislik=1.0, x0=0.0):
    """Alt panelde numaralı anahtar listesi."""
    n = len(adlar)
    satir = int(np.ceil(n / sutun))
    y0 = 0.92 if not baslik else 0.80
    if baslik:
        kax.text(x0, 0.95, baslik, fontsize=7.0, fontweight="bold",
                 color=H.GRI["metin"], va="center")
    dy = y0 / max(satir, 1)
    sw = genislik / sutun
    for i, (num, ad) in enumerate(zip(numaralar, adlar)):
        c, r = i // satir, i % satir
        x = x0 + c * sw
        y = y0 - r * dy - dy * 0.35
        kax.text(x, y, f"{num}", fontsize=yazi, fontweight="bold",
                 color=H.GRI["metin"], va="center")
        if mod == "dolu":
            kax.text(x + sw * 0.075, y, ad, fontsize=yazi, color=H.GRI["metin"],
                     va="center", clip_on=True)
        else:
            bos_satir(kax, x + sw * 0.075, y, sw * 0.88)
    if ek:
        kax.text(x0, 0.02, ek, fontsize=5.9, color=H.GRI["soluk"], va="bottom")


def sembol_katmani(ax, gruplar, boyut=30, cizgi=0.8):
    """gruplar: [(ad, isaret, [(etiket, lon, lat)])] — her grup ayrı sembol."""
    for ad, isaret, noktalar in gruplar:
        xs, ys = [], []
        for _, lon, lat in noktalar:
            x, y = H.xy(lon, lat); xs.append(x); ys.append(y)
        ax.scatter(xs, ys, s=boyut, marker=isaret, facecolor="white",
                   edgecolor="#1f1f1f", linewidth=cizgi, zorder=7)


def sembol_anahtari(kax, gruplar, mod, sutun=3, baslik="", yazi=6.2, notlar=True,
                    genislik=1.0, x0=0.0):
    """Sembol açıklamaları; 'bos' modda ad gizlenir."""
    n = len(gruplar)
    satir = int(np.ceil(n / sutun))
    y0 = 0.80 if baslik else 0.92
    if baslik:
        kax.text(x0, 0.95, baslik, fontsize=7.0, fontweight="bold", va="center")
    dy = y0 / max(satir, 1)
    sw = genislik / sutun
    for i, g in enumerate(gruplar):
        ad, isaret = g[0], g[1]
        not_ = g[3] if len(g) > 3 and notlar else ""
        c, r = i // satir, i % satir
        x, y = x0 + c * sw, y0 - r * dy - dy * 0.35
        kax.scatter([x + 0.008], [y], s=26, marker=isaret, facecolor="white",
                    edgecolor="#1f1f1f", linewidth=0.8, clip_on=False)
        if mod == "dolu":
            kax.text(x + 0.026, y, ad, fontsize=yazi, fontweight="bold", va="center")
            if not_:
                kax.text(x + 0.026, y - dy * 0.34, not_, fontsize=5.2,
                         color=H.GRI["soluk"], va="center")
        else:
            bos_satir(kax, x + 0.026, y, sw * 0.86)


# ------------------------------------------------------------ ALANLAR
def zon_ciz(ax, zonlar, mod, dolgu=None, taramalar=None, etiket_konum=None,
            cizgi_lw=0.9, etiket_yazi=6.6, numarali=True):
    """zonlar: [(ad, geometri)] — taramalı alan gösterimi.

    'dolu': gri ton + tarama + ada numarası;  'bos': yalnız sınır + numara.
    """
    taramalar = taramalar or ["", "///", "...", "\\\\\\", "xxx", "+++", "ooo", "||", "--"]
    dolgu = dolgu or ["#ffffff", "#ededed", "#dcdcdc", "#cacaca", "#b8b8b8",
                      "#a6a6a6", "#949494", "#828282"]
    merkezler = []
    for i, (ad, g) in enumerate(zonlar):
        gs = gpd.GeoSeries([g], crs=H.LCC)
        if mod == "dolu":
            gs.plot(ax=ax, facecolor=dolgu[i % len(dolgu)], edgecolor="#3d3d3d",
                    linewidth=cizgi_lw, hatch=taramalar[i % len(taramalar)], zorder=4)
        else:
            gs.plot(ax=ax, facecolor="white", edgecolor="#3d3d3d",
                    linewidth=cizgi_lw, zorder=4)
        p = g.representative_point()
        merkezler.append((ad, p.x, p.y))
    if numarali:
        for i, (ad, px, py) in enumerate(merkezler, 1):
            ax.text(px, py, str(i), fontsize=7.4, fontweight="bold", ha="center",
                    va="center", zorder=7,
                    bbox=dict(boxstyle="circle,pad=0.22", facecolor="white",
                              edgecolor="#2b2b2b", linewidth=0.8))
    return merkezler


def zon_anahtari(kax, zonlar, mod, sutun=3, baslik="", taramalar=None,
                 dolgu=None, yazi=6.3, aciklamalar=None, genislik=1.0, x0=0.0):
    taramalar = taramalar or ["", "///", "...", "\\\\\\", "xxx", "+++", "ooo", "||", "--"]
    dolgu = dolgu or ["#ffffff", "#ededed", "#dcdcdc", "#cacaca", "#b8b8b8",
                      "#a6a6a6", "#949494", "#828282"]
    n = len(zonlar); satir = int(np.ceil(n / sutun))
    y0 = 0.80 if baslik else 0.92
    if baslik:
        kax.text(x0, 0.95, baslik, fontsize=7.0, fontweight="bold", va="center")
    dy = y0 / max(satir, 1)
    sw = genislik / sutun
    for i, (ad, _) in enumerate(zonlar):
        c, r = i // satir, i % satir
        x, y = x0 + c * sw, y0 - r * dy - dy * 0.3
        kax.add_patch(Rectangle((x, y - 0.055), 0.020, 0.11,
                                facecolor=dolgu[i % len(dolgu)], edgecolor="#3d3d3d",
                                hatch=taramalar[i % len(taramalar)], lw=0.7, clip_on=False))
        kax.text(x + 0.026, y, f"{i+1}", fontsize=yazi, fontweight="bold", va="center")
        if mod == "dolu":
            kax.text(x + 0.042, y, ad, fontsize=yazi, va="center")
            if aciklamalar and i < len(aciklamalar) and aciklamalar[i]:
                import textwrap
                for j, t in enumerate(textwrap.wrap(aciklamalar[i],
                                                    max(20, int(sw * 105)))[:2]):
                    kax.text(x + 0.042, y - dy * (0.34 + j * 0.26), t, fontsize=5.0,
                             color=H.GRI["soluk"], va="center")
        else:
            bos_satir(kax, x + 0.042, y, sw * 0.82)


# --------------------------------------------------------- CHOROPLETH
def choropleth(ax, degerler: dict, kesikler, dolgular=None, taramalar=None,
               cizgi="#8f8f8f", lw=0.3, mod="dolu"):
    """İl bazlı sınıflı gösterim. degerler: {il: sayı}"""
    il = H.iller()
    dolgular = dolgular or ["#ffffff", "#e4e4e4", "#c6c6c6", "#a4a4a4", "#7d7d7d", "#575757"]
    taramalar = taramalar or ["", "", "", "", "", ""]
    if mod == "bos":
        il.plot(ax=ax, facecolor="white", edgecolor=cizgi, linewidth=lw, zorder=4)
        return
    for i in range(len(kesikler) - 1):
        alt, ust = kesikler[i], kesikler[i + 1]
        sec = [k for k, v in degerler.items() if alt <= v < ust and k in il.index]
        if not sec:
            continue
        il.loc[sec].plot(ax=ax, facecolor=dolgular[i], edgecolor=cizgi,
                         linewidth=lw, hatch=taramalar[i], zorder=4)


def choropleth_anahtari(kax, kesikler, etiketler, dolgular=None, baslik="",
                        x=0.0, y=0.62, yazi=6.1, taramalar=None):
    dolgular = dolgular or ["#ffffff", "#e4e4e4", "#c6c6c6", "#a4a4a4", "#7d7d7d", "#575757"]
    taramalar = taramalar or [""] * len(etiketler)
    if baslik:
        kax.text(x, y + 0.26, baslik, fontsize=7.0, fontweight="bold", va="center")
    for i, et in enumerate(etiketler):
        kax.add_patch(Rectangle((x + i * 0.108, y - 0.06), 0.022, 0.12,
                                facecolor=dolgular[i], edgecolor="#3d3d3d",
                                hatch=taramalar[i], lw=0.7, clip_on=False))
        kax.text(x + i * 0.108 + 0.028, y, et, fontsize=yazi, va="center")


# ------------------------------------------------------- METİN KUTUSU
def bilgi_kutusu(kax, x, y, w, baslik, satirlar, yazi=5.9, baslik_yazi=6.6):
    kax.text(x, y, baslik, fontsize=baslik_yazi, fontweight="bold", va="top")
    for i, s in enumerate(satirlar):
        kax.text(x, y - 0.13 - i * 0.125, s, fontsize=yazi, va="top",
                 color=H.GRI["metin"], wrap=True)


def harita_notu(ax, metin, y=0.018, yazi=5.6):
    ax.text(0.5, y, metin, transform=ax.transAxes, fontsize=yazi, ha="center",
            va="bottom", color=H.GRI["soluk"], style="italic", zorder=9,
            bbox=dict(boxstyle="round,pad=0.3", facecolor="white",
                      edgecolor="#d5d5d5", lw=0.5, alpha=0.9))


# ------------------------------------------------- KÜÇÜK ÇOKLU IZGARA
def izgara(fig, sutun, satir, ust=0.845, alt=0.075, sol=0.030, sag=0.970,
           baslik_pay=0.22, not_pay=0.20):
    """Küçük çoklu mini harita eksenleri.

    Hücrenin üstünde başlık, altında not için yer bırakılır; harita ekseni
    Türkiye'nin gerçek en/boy oranına göre boyutlandırılır, böylece başlık ve
    notlar haritaya sabit uzaklıkta durur.
    """
    gw = (sag - sol) / sutun
    gh = (ust - alt) / satir
    hw = gw * 0.94                                   # harita genişliği (figür oranı)
    oran = (H.pencere()[1] - H.pencere()[0]) / (H.pencere()[3] - H.pencere()[2])
    hh = (hw * H.SAYFA_W / oran) / H.SAYFA_H         # harita yüksekliği (figür oranı)
    eksenler, hucreler = [], []
    for r in range(satir):
        for c in range(sutun):
            hx = sol + c * gw + (gw - hw) / 2
            hucre_ust = ust - r * gh
            hy = hucre_ust - gh * baslik_pay - hh
            ax = fig.add_axes([hx, hy, hw, hh])
            ax.set_xticks([]); ax.set_yticks([])
            for sp in ax.spines.values():
                sp.set_visible(False)
            x0, x1, y0, y1 = H.pencere()
            ax.set_xlim(x0, x1); ax.set_ylim(y0, y1)
            ax.set_aspect("equal", adjustable="box")
            eksenler.append(ax)
            hucreler.append((hx, hy, hw, hh, hucre_ust, gh))
    return eksenler, hucreler


def mini_turkiye(ax, vurgu=None, lider=None, lw=0.55):
    """Mini haritaya Türkiye + vurgulanan iller."""
    il = H.iller()
    il.plot(ax=ax, facecolor="white", edgecolor="#d4d4d4", linewidth=0.16, zorder=2)
    if vurgu:
        sec = [v for v in vurgu if v in il.index]
        il.loc[sec].plot(ax=ax, facecolor="#8d8d8d", edgecolor="#464646",
                         linewidth=0.25, zorder=3)
    gpd.GeoSeries([H.turkiye()], crs=H.LCC).boundary.plot(
        ax=ax, color="#1a1a1a", linewidth=lw, zorder=5)
    if lider and lider in il.index:
        p = il.loc[lider].geometry.representative_point()
        ax.scatter([p.x], [p.y], s=46, marker="*", facecolor="white",
                   edgecolor="#111111", linewidth=0.7, zorder=6)


# --------------------------------------------- ETİKET ÇAKIŞMA ÖNLEYİCİ
def etiketle(ax, ogeler, yazi=4.2, renk="#242424", zorder=7.8, pay=1.0):
    """ogeler: [(metin, x, y)] — çakışmayan olanları yerleştirir, sayısını döner.

    Basit greedy yerleştirme: her etiket için sırayla birkaç kaydırma denenir,
    daha önce yerleştirilenlerle kesişmeyen ilk konum kullanılır.
    """
    fig = ax.figure
    dpi = fig.dpi
    yerlesik = []
    denemeler = [(0, 6), (0, -8), (7, 2), (-7, 2), (0, 12), (0, -14), (12, -6), (-12, -6)]
    kondu = 0
    for metin, x, y in ogeler:
        px, py = ax.transData.transform((x, y))
        gw = len(metin) * yazi * 0.52 * dpi / 72.0 * pay
        gh = yazi * 1.35 * dpi / 72.0
        for dx, dy in denemeler:
            cx = px + dx * dpi / 72.0
            cy = py + dy * dpi / 72.0
            kutu = (cx - gw / 2, cy - gh / 2, cx + gw / 2, cy + gh / 2)
            if all(kutu[0] > b[2] or kutu[2] < b[0] or kutu[1] > b[3] or kutu[3] < b[1]
                   for b in yerlesik):
                yerlesik.append(kutu)
                ax.annotate(metin, xy=(x, y), xytext=(dx, dy),
                            textcoords="offset points", fontsize=yazi, ha="center",
                            va="center", color=renk, zorder=zorder)
                kondu += 1
                break
    return kondu


# ------------------------------------------- NUMARALI KATEGORİ SEMBOLÜ
def numarali_kategoriler(ax, gruplar, boyut=54, yazi=4.6, etiket_yazi=4.0,
                         etiket=True):
    """Her KATEGORİ bir numara alır; o kategorinin tüm noktaları aynı numarayla
    çizilir. Siyah-beyaz baskıda 15 farklı sembol şeklinden çok daha okunaklıdır."""
    tum_etiket = []
    for i, g in enumerate(gruplar, 1):
        noktalar = g[2]
        for et, lon, lat in noktalar:
            x, y = H.xy(lon, lat)
            ax.scatter([x], [y], s=boyut, marker="o", facecolor="white",
                       edgecolor="#141414", linewidth=0.8, zorder=7)
            ax.text(x, y, str(i), fontsize=yazi, ha="center", va="center",
                    fontweight="bold", zorder=7.5)
            tum_etiket.append((et.split(" (")[0], x, y))
    if etiket:
        etiketle(ax, tum_etiket, yazi=etiket_yazi)


def numarali_kategori_anahtari(kax, gruplar, mod, sutun=3, baslik="", yazi=6.0,
                               genislik=1.0, x0=0.0, not_yazi=4.9, sarma=None):
    """Numaralı kategori açıklaması; notlar sütun genişliğine göre sarılır."""
    import textwrap
    n = len(gruplar); satir = int(np.ceil(n / sutun))
    sw = genislik / sutun
    sarma = sarma or max(18, int(sw * 118))
    y0 = 0.80 if baslik else 0.93
    if baslik:
        kax.text(x0, 0.95, baslik, fontsize=7.0, fontweight="bold", va="center")
    dy = y0 / max(satir, 1)
    for i, g in enumerate(gruplar):
        ad = g[0]
        not_ = g[3] if len(g) > 3 else ""
        c, r = i // satir, i % satir
        x, y = x0 + c * sw, y0 - r * dy - dy * 0.30
        kax.scatter([x + 0.007], [y], s=52, marker="o", facecolor="white",
                    edgecolor="#141414", linewidth=0.8, clip_on=False, zorder=5)
        kax.text(x + 0.007, y, str(i + 1), fontsize=4.6, ha="center", va="center",
                 fontweight="bold", zorder=6)
        if mod == "dolu":
            kax.text(x + 0.022, y, ad, fontsize=yazi, fontweight="bold", va="center")
            if not_:
                sat = textwrap.wrap(not_, sarma)[:3]
                for j, t in enumerate(sat):
                    kax.text(x + 0.022, y - dy * (0.28 + j * 0.22), t,
                             fontsize=not_yazi, color=H.GRI["soluk"], va="center")
        else:
            bos_satir(kax, x + 0.022, y, sw * 0.88)
