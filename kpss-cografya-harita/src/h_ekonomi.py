# -*- coding: utf-8 -*-
"""Ekonomik coğrafya haritaları."""
from __future__ import annotations

import geopandas as gpd
import numpy as np
from matplotlib import pyplot as plt
from matplotlib.lines import Line2D

import cizim as C
import harita_temel as H
import veri_ekonomi as E


# ------------------------------------------- KÜÇÜK ÇOKLU (ürün) SAYFASI
def urun_sayfasi(fig, urunler, mod, sutun, satir, altnot=""):
    """Her ürün için ayrı mini Türkiye haritası; ★ = klasik 1. sıradaki il."""
    import textwrap
    eksenler, hucreler = C.izgara(fig, sutun, satir, ust=0.836, alt=0.082)
    for ax, hc, u in zip(eksenler, hucreler, urunler):
        ad, isaret, lider, iller, notu = u
        hx, hy, hw, hh, hucre_ust, gh = hc
        C.mini_turkiye(ax, vurgu=iller, lider=lider)
        ort = hx + hw / 2
        if mod == "dolu":
            fig.text(ort, hy + hh + 0.012, ad, fontsize=7.4, ha="center",
                     va="bottom", fontweight="bold")
            fig.text(ort, hy - 0.016, f"★ {lider}", fontsize=5.9, ha="center",
                     va="top", color="#2b2b2b")
            if notu:
                for j, t in enumerate(textwrap.wrap(notu, int(hw * 175))[:2]):
                    fig.text(ort, hy - 0.036 - j * 0.017, t, fontsize=4.6,
                             ha="center", va="top", color="#6b6b6b")
        else:
            fig.add_artist(plt.Line2D([ort - hw * 0.40, ort + hw * 0.40],
                                      [hy + hh + 0.016] * 2, color="#c0c0c0", lw=0.6))
            fig.text(ort - hw * 0.30, hy - 0.022, "★", fontsize=5.9, ha="center",
                     va="center", color="#9a9a9a")
            fig.add_artist(plt.Line2D([ort - hw * 0.24, ort + hw * 0.34],
                                      [hy - 0.026] * 2, color="#c0c0c0", lw=0.5))
    for ax in eksenler[len(urunler):]:
        ax.set_visible(False)
    fig.text(0.5, 0.042,
             altnot or ("Koyu iller ürünün başlıca üretim alanlarıdır; ★ KPSS'de klasik olarak "
                        "1. sırada sorulan ildir. Üretim sıralamaları yıllara göre değişebilir."),
             fontsize=5.8, ha="center", color=H.GRI["soluk"], style="italic")


def harita_tarim_tahil(fig, mod):
    urun_sayfasi(fig, E.TARIM_TAHIL, mod, 3, 2)


def harita_tarim_endustri(fig, mod):
    urun_sayfasi(fig, E.TARIM_ENDUSTRI, mod, 4, 2)


def harita_tarim_meyve(fig, mod):
    urun_sayfasi(fig, E.TARIM_MEYVE, mod, 4, 3)


def harita_hayvancilik(fig, mod):
    urun_sayfasi(fig, E.HAYVANCILIK, mod, 4, 2,
                 altnot="Koyu iller başlıca üretim/yetiştirme alanlarıdır; ★ klasik 1. sıradaki il. "
                        "Hayvancılık türü doğrudan BİTKİ ÖRTÜSÜ ile ilişkilidir: bozkır→küçükbaş, "
                        "maki→kıl keçisi, çayır→büyükbaş.")


# --------------------------------------------------- NOKTA SEMBOL SAYFALARI
def _sembol_sayfasi(ax, kax, mod, gruplar, baslik, sutun=3, notlar=True,
                    genislik=1.0, boyut=52, not_yazi=4.9):
    C.numarali_kategoriler(ax, gruplar, boyut=boyut, etiket=(mod == "dolu"))
    C.numarali_kategori_anahtari(kax, gruplar, mod, sutun=sutun, baslik=baslik,
                                 genislik=genislik, not_yazi=not_yazi)


def harita_madenler(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    _sembol_sayfasi(ax, kax, mod, E.MADENLER,
                    "MADENLER — numaranın karşısına maden adını yaz", sutun=4,
                    genislik=1.0, not_yazi=4.7)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


def harita_enerji(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    C.numarali_kategoriler(ax, E.SANTRALLER, boyut=52, etiket=(mod == "dolu"))
    C.numarali_kategori_anahtari(kax, E.SANTRALLER, mod, sutun=2,
                                 baslik="ENERJİ KAYNAKLARI ve SANTRALLER",
                                 genislik=0.44)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.68, 0.96, 0.32, "SINAV NOTU", [
            "· Linyit kalorisi düşüktür → çıkarıldığı yerde yakılır",
            "  (Afşin-Elbistan, Soma, Yatağan, Tunçbilek).",
            "· Jeotermal Ege'de yoğundur: Menderes Grabeni",
            "  (Denizli–Aydın–Manisa) — fay hatlarıyla ilişkilidir.",
            "· RES: Ege ve Marmara kıyıları, Çanakkale Boğazı, Belen.",
            "· GES: İç Anadolu'nun güneyi ve Güneydoğu (Karapınar).",
            "· Akkuyu (Mersin) ilk nükleer santralimizdir.",
        ], yazi=5.0)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


def harita_sanayi(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    _sembol_sayfasi(ax, kax, mod, E.SANAYI,
                    "SANAYİ KURULUŞLARI", sutun=3, genislik=0.70, not_yazi=4.7)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.72, 0.96, 0.28, "SINAV NOTU", [
            "· Sanayinin en gelişmiş bölgesi MARMARA'dır",
            "  (İstanbul–Kocaeli–Bursa üçgeni); en az gelişmiş",
            "  bölge Doğu Anadolu'dur.",
            "· Karabük (1937) ilk entegre demir-çelik tesisidir.",
            "· İlk şeker fabrikaları 1926'da Alpullu ve Uşak.",
            "· Çimento hammaddesi her yerde bulunduğu ve ağır",
            "  olduğu için hemen her ilde fabrika vardır.",
        ], yazi=5.0)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


def harita_ulasim(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.2, ulke_lw=1.2)
    for ad, tip, hat in E.BORU_HATLARI:
        xs, ys = zip(*[H.xy(lo, la) for lo, la in hat])
        ax.plot(xs, ys, color="#2e2e2e", lw=1.15,
                ls="-" if tip == "petrol" else (0, (5, 2)), zorder=5.6)
    gruplar = [
        ("Liman", "s", [(a, lo, la) for a, lo, la, n in E.LIMANLAR]),
        ("Havalimanı (büyük)", "^", [(a, lo, la) for a, lo, la, t in E.HAVALIMANLARI if t == "buyuk"]),
        ("Havalimanı", "o", [(a, lo, la) for a, lo, la, t in E.HAVALIMANLARI if t == "orta"]),
        ("Köprü / tünel", "D", [(a, lo, la) for a, lo, la in E.KOPRU_TUNEL]),
    ]
    C.numarali_kategoriler(ax, gruplar, boyut=48, etiket=(mod == "dolu"))
    C.numarali_kategori_anahtari(kax, gruplar, mod, sutun=2, baslik="ULAŞIM",
                                 genislik=0.36)
    el = [Line2D([0], [0], color="#2e2e2e", lw=1.15,
                 ls="-" if t == "petrol" else (0, (5, 2)),
                 label=a if mod == "dolu" else "." * 34)
          for a, t, _ in E.BORU_HATLARI]
    kax.legend(handles=el, loc="upper right", fontsize=5.3, frameon=True,
               edgecolor="#cfcfcf", title="BORU HATLARI",
               title_fontproperties={"weight": "bold", "size": 5.8},
               bbox_to_anchor=(1.0, 1.02))
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


def harita_turizm(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    gruplar = [
        ("UNESCO Dünya Mirası", "*", [(a, lo, la) for a, lo, la, y in E.UNESCO]),
        ("Millî Park", "^", [(a, lo, la) for a, lo, la in E.MILLI_PARK]),
        ("Kayak merkezi", "D", [(a, lo, la) for a, lo, la in E.KAYAK]),
        ("Kaplıca / termal", "o", [(a, lo, la) for a, lo, la in E.KAPLICA]),
    ]
    C.numarali_kategoriler(ax, gruplar, boyut=48, etiket=(mod == "dolu"),
                           etiket_yazi=3.9)
    C.numarali_kategori_anahtari(kax, gruplar, mod, sutun=1, baslik="TURİZM",
                                 genislik=0.20)
    if mod == "dolu":
        yarim = len(E.UNESCO) // 2 + 1
        C.bilgi_kutusu(kax, 0.22, 0.96, 0.30, "UNESCO DÜNYA MİRASI (2023 sonu: 21 varlık)",
                       [f"{y} · {a}" for a, lo, la, y in E.UNESCO[:yarim]], yazi=4.6)
        C.bilgi_kutusu(kax, 0.53, 0.96, 0.30, " ",
                       [f"{y} · {a}" for a, lo, la, y in E.UNESCO[yarim:]], yazi=4.6)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "UNESCO listesi 2023 sonu itibarıyladır")


def harita_balikcilik(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.25, ulke_lw=1.2)
    for i, (ad, lo, la, notu) in enumerate(E.BALIKCILIK, 1):
        x, y = H.xy(lo, la)
        ax.scatter([x], [y], s=190, marker="o", facecolor="white",
                   edgecolor="#111111", linewidth=1.1, zorder=8)
        ax.text(x, y, str(i), fontsize=7.4, ha="center", va="center",
                fontweight="bold", zorder=8.1)
    C.liste_paneli(kax, [str(i) for i in range(1, 5)],
                   [f"{a} — {n}" for a, lo, la, n in E.BALIKCILIK], mod, sutun=1,
                   baslik="DENİZLERİMİZDE BALIKÇILIK", yazi=5.9, genislik=0.46)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.50, 0.96, 0.50, "SU ÜRÜNLERİ — SINAV NOTLARI", [
            "· Avlanan balığın büyük bölümü KARADENİZ'den elde edilir; en çok avlanan tür HAMSİ'dir.",
            "· Karadeniz'in verimli olmasının nedenleri: kıta sahanlığının geniş olması, tuzluluğun",
            "  düşük olması ve akarsuların getirdiği besin maddeleri (plankton bolluğu).",
            "· Akdeniz tuzluluğu yüksek, kıta sahanlığı dar olduğu için en az verimli denizimizdir.",
            "· Kültür balıkçılığı: Muğla ve İzmir (çipura-levrek), Elazığ ve Samsun (alabalık).",
        ], yazi=5.2)
    H.denizleri_yaz(ax, fontsize=6.4); H.olcek_kuzey(ax)
