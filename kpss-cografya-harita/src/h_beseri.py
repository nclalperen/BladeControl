# -*- coding: utf-8 -*-
"""Beşeri coğrafya: nüfus ve göç."""
from __future__ import annotations

import geopandas as gpd
import numpy as np

import cizim as C
import harita_temel as H
import veri_idari as V


def yogunluk() -> dict:
    """Gerçek nüfus / gerçek il alanı = aritmetik nüfus yoğunluğu (kişi/km²)."""
    il = H.iller()
    return {ad: V.NUFUS[ad] / il.loc[ad, "alan_km2"] for ad in il.index}


# ------------------------------------------------------------------ 18
def harita_nufus(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, ulke_lw=1.25)
    y = yogunluk()
    kad = [0, 30, 60, 100, 200, 500, 100000]
    etik = ["<30", "30–60", "60–100", "100–200", "200–500", ">500"]
    tonlar = ["#ffffff", "#e8e8e8", "#cfcfcf", "#b0b0b0", "#8a8a8a", "#565656"]
    C.choropleth(ax, y, kad, tonlar, mod=mod, lw=0.28)
    gpd.GeoSeries([H.turkiye()], crs=H.LCC).boundary.plot(ax=ax, color="#141414",
                                                          linewidth=1.25, zorder=6)
    if mod == "bos":
        il = H.iller()
        for ad in il.index:
            p = il.loc[ad].geometry.representative_point()
            ax.scatter([p.x], [p.y], s=3, marker="o", facecolor="#b0b0b0",
                       edgecolor="none", zorder=6)

    C.choropleth_anahtari(kax, kad, etik, tonlar,
                          baslik="NÜFUS YOĞUNLUĞU (kişi/km²) — TÜİK ADNKS 2025" if mod == "dolu"
                          else "YOĞUNLUK KADEMELERİNİ YAZ", y=0.70)
    sirali = sorted(V.NUFUS.items(), key=lambda kv: -kv[1])
    yog = sorted(y.items(), key=lambda kv: -kv[1])
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.0, 0.44, 0.30, "EN KALABALIK 6 İL",
                       [f"{i}. {a} — {n:,}".replace(",", ".") for i, (a, n) in enumerate(sirali[:6], 1)],
                       yazi=5.3)
        C.bilgi_kutusu(kax, 0.31, 0.44, 0.30, "EN AZ NÜFUSLU 6 İL",
                       [f"{i}. {a} — {n:,}".replace(",", ".") for i, (a, n) in enumerate(sirali[-6:][::-1], 1)],
                       yazi=5.3)
        C.bilgi_kutusu(kax, 0.62, 0.44, 0.38, "SIK ve SEYREK NÜFUSLU ALANLARIN NEDENLERİ", [
            f"En yoğun: {yog[0][0]} ({yog[0][1]:.0f}) · En seyrek: {yog[-1][0]} ({yog[-1][1]:.0f}) kişi/km²",
            "SIK: Marmara (sanayi), Ege ve Akdeniz kıyıları (tarım-turizm), Çukurova,",
            "     Karadeniz kıyı şeridi (dar alanda tarım), GAP illeri (yüksek doğum oranı).",
            "SEYREK: Doğu Anadolu'nun yüksek-engebeli kesimi, Menteşe ve Taşeli yöreleri,",
            "     Tuz Gölü çevresi (kuraklık ve tuzlu toprak), iç Karadeniz'in dağlık kesimi.",
        ], yazi=5.1)
    else:
        C.bilgi_kutusu(kax, 0.0, 0.44, 0.48, "EN KALABALIK 6 İLİ YAZ", ["." * 60] * 3, yazi=5.3)
        C.bilgi_kutusu(kax, 0.50, 0.44, 0.50, "SIK ve SEYREK NÜFUSLU ALANLARI NEDENLERİYLE YAZ",
                       ["." * 64] * 3, yazi=5.3)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Yoğunluk = TÜİK ADNKS 2025 il nüfusu ÷ il alanı (alanlar sınır "
                      "verisinden hesaplanmıştır)")


# ------------------------------------------------------------------ 19
GOC_ALAN = [("İstanbul", 28.95, 41.02), ("Ankara", 32.86, 39.93), ("İzmir", 27.14, 38.42),
            ("Bursa", 29.06, 40.20), ("Kocaeli", 29.92, 40.77), ("Antalya", 30.71, 36.89),
            ("Tekirdağ", 27.51, 41.00), ("Adana", 35.32, 37.00)]
GOC_VEREN = [(41.27, 39.90), (43.38, 38.49), (39.22, 38.68), (36.55, 40.32),
             (40.55, 41.02), (37.02, 39.75), (34.98, 40.65), (42.70, 40.60),
             (44.05, 39.92), (41.94, 37.93), (33.78, 41.39), (38.36, 38.38)]


def harita_goc(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.28, ulke_lw=1.25)
    il = H.iller()
    if mod == "dolu":
        il.loc[[a for a, _, _ in GOC_ALAN]].plot(ax=ax, facecolor="#8f8f8f",
                                                 edgecolor="#3a3a3a", linewidth=0.4, zorder=4)
    for i, (ad, lo, la) in enumerate(GOC_ALAN, 1):
        x, y = H.xy(lo, la)
        ax.scatter([x], [y], s=95, marker="o", facecolor="white", edgecolor="#111111",
                   linewidth=1.0, zorder=7.5)
        ax.text(x, y, str(i), fontsize=5.4, ha="center", va="center",
                fontweight="bold", zorder=7.6)
    # şematik göç okları: veren yörelerden en yakın çekim merkezine
    for lo, la in GOC_VEREN:
        x, y = H.xy(lo, la)
        hedef = min(GOC_ALAN, key=lambda g: (H.xy(g[1], g[2])[0] - x) ** 2
                                            + (H.xy(g[1], g[2])[1] - y) ** 2)
        hx, hy = H.xy(hedef[1], hedef[2])
        ax.annotate("", xy=(hx + (x - hx) * 0.14, hy + (y - hy) * 0.14), xytext=(x, y),
                    arrowprops=dict(arrowstyle="-|>", lw=0.75, color="#4a4a4a",
                                    mutation_scale=7,
                                    connectionstyle="arc3,rad=0.13"), zorder=6.8)
    C.liste_paneli(kax, [str(i) for i in range(1, len(GOC_ALAN) + 1)],
                   [a for a, _, _ in GOC_ALAN], mod, sutun=2,
                   baslik="EN ÇOK GÖÇ ALAN İLLER", yazi=5.9, genislik=0.34)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.36, 0.96, 0.32, "GÖÇÜN NEDENLERİ", [
            "İTİCİ (göç veren yer): tarımda makineleşme, miras yoluyla",
            "toprakların bölünmesi, erozyon, iş olanağının azlığı,",
            "olumsuz iklim ve yer şekilleri, terör, kan davası.",
            "ÇEKİCİ (göç alan yer): sanayi ve hizmet sektöründe iş,",
            "eğitim ve sağlık olanakları, turizm, daha yüksek gelir.",
        ], yazi=5.2)
        C.bilgi_kutusu(kax, 0.69, 0.96, 0.31, "GÖÇÜN SONUÇLARI", [
            "· Kentlerde çarpık kentleşme ve gecekondulaşma",
            "· Alt yapı hizmetlerinin yetersiz kalması",
            "· Kırsalda yatırımların âtıl kalması, köylerin boşalması",
            "· Nüfusun dengesiz dağılması; kadın-erkek ve yaş",
            "  yapısının bozulması",
            "· Sanayi tesislerinin kent içinde kalması",
        ], yazi=5.2)
    else:
        C.bilgi_kutusu(kax, 0.36, 0.96, 0.32, "GÖÇÜN NEDENLERİ (itici / çekici)", ["." * 56] * 5)
        C.bilgi_kutusu(kax, 0.69, 0.96, 0.31, "GÖÇÜN SONUÇLARI", ["." * 54] * 5)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Oklar şematiktir; ülke içi göçün genel yönünü gösterir "
                      "(kırsaldan sanayileşmiş kentlere)")
