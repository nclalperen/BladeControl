# -*- coding: utf-8 -*-
"""Portföyü üretir: baskıya hazır tek PDF + sayfa PNG'leri."""
from __future__ import annotations

import os
import sys

import matplotlib
matplotlib.use("Agg")
from matplotlib import pyplot as plt
from matplotlib.backends.backend_pdf import PdfPages
from matplotlib.patches import Rectangle

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import cizim as C
import h_beseri as B
import h_ekonomi as EK
import h_fiziki as FZ
import h_iklim as IK
import h_konum as KN
import harita_temel as H
import veri_idari as V

CIKTI = H.CIKTI
PNG = os.path.join(CIKTI, "png")

# (bölüm, başlık, altbaşlık, fonksiyon, düzen)
HARITALAR = [
 ("Bölüm 1 · Konum ve İdari Yapı", "Matematiksel Konum ve Uç Noktalar",
  "Paralel–meridyen ağı gerçek projeksiyonda çizilmiştir", KN.harita_matematiksel_konum, "tek"),
 ("Bölüm 1 · Konum ve İdari Yapı", "Komşular, Denizler ve Sınır Kapıları",
  "Özel konumun sonuçları", KN.harita_komsular, "tek"),
 ("Bölüm 1 · Konum ve İdari Yapı", "İdari Bölünüş: 81 İl ve Plaka Kodları",
  "", KN.harita_iller, "tek"),
 ("Bölüm 1 · Konum ve İdari Yapı", "Coğrafi Bölgeler",
  "1941 Birinci Türk Coğrafya Kongresi sınıflandırması", KN.harita_bolgeler, "tek"),
 ("Bölüm 1 · Konum ve İdari Yapı", "Coğrafi Bölümler (21 Bölüm)",
  "", KN.harita_bolumler, "tek"),

 ("Bölüm 2 · Yer Şekilleri", "Dağlar ve Zirveler",
  "Gerçek yükselti verisinden üretilmiş kabartma", FZ.harita_yerelsekilleri, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Ovalar ve Platolar",
  "Delta, kıyı ve iç ovalar", FZ.harita_ova_plato, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Akarsular ve Havzalar",
  "Akarsu çizgileri Natural Earth 10m verisidir", FZ.harita_akarsular, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Göller ve Oluşumları",
  "Tektonik · karstik · volkanik set · heyelan set · alüvyal set · buzul",
  FZ.harita_goller, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Barajlar ve Baraj Gölleri",
  "", FZ.harita_barajlar, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Volkanik ve Karstik Alanlar",
  "", FZ.harita_volkanik_karstik, "tek"),
 ("Bölüm 2 · Yer Şekilleri", "Fay Hatları ve Deprem Kuşakları",
  "", FZ.harita_faylar, "tek"),

 ("Bölüm 3 · İklim, Bitki Örtüsü, Toprak", "İklim Tipleri",
  "Kuşaklar gerçek kıyı çizgisinden üretilmiştir", IK.harita_iklim, "tek"),
 ("Bölüm 3 · İklim, Bitki Örtüsü, Toprak", "Yıllık Yağış Dağılışı",
  "Gerçek iklim verisi: WorldClim v2.1 (1970–2000)", IK.harita_yagis, "tek"),
 ("Bölüm 3 · İklim, Bitki Örtüsü, Toprak", "Ocak Ayı Sıcaklık Dağılışı",
  "Gerçek iklim verisi: WorldClim v2.1 (1970–2000)", IK.harita_sicaklik, "tek"),
 ("Bölüm 3 · İklim, Bitki Örtüsü, Toprak", "Bitki Örtüsü",
  "Dağ çayırı sınırı gerçek yükselti verisinden", IK.harita_bitki, "tek"),
 ("Bölüm 3 · İklim, Bitki Örtüsü, Toprak", "Toprak Tipleri",
  "Zonal · azonal · intrazonal", IK.harita_toprak, "tek"),

 ("Bölüm 4 · Beşeri Coğrafya", "Nüfus Yoğunluğu",
  "TÜİK ADNKS 2025 nüfusu ÷ gerçek il alanı", B.harita_nufus, "tek"),
 ("Bölüm 4 · Beşeri Coğrafya", "Göç ve Kentleşme",
  "", B.harita_goc, "tek"),

 ("Bölüm 5 · Ekonomik Coğrafya", "Tarım I — Tahıllar ve Baklagiller",
  "Her ürün için ayrı harita", EK.harita_tarim_tahil, "izgara"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Tarım II — Endüstri Bitkileri",
  "Her ürün için ayrı harita", EK.harita_tarim_endustri, "izgara"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Tarım III — Meyveler ve Özel Ürünler",
  "Her ürün için ayrı harita", EK.harita_tarim_meyve, "izgara"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Hayvancılık",
  "Hayvancılık türü bitki örtüsüne bağlıdır", EK.harita_hayvancilik, "izgara"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Su Ürünleri ve Balıkçılık",
  "", EK.harita_balikcilik, "tek"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Madenler",
  "", EK.harita_madenler, "tek"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Enerji Kaynakları ve Santraller",
  "", EK.harita_enerji, "tek"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Sanayi Kuruluşları",
  "", EK.harita_sanayi, "tek"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Ulaşım: Limanlar, Havalimanları, Boru Hatları",
  "", EK.harita_ulasim, "tek"),
 ("Bölüm 5 · Ekonomik Coğrafya", "Turizm ve UNESCO Dünya Mirası",
  "", EK.harita_turizm, "tek"),
]


# ------------------------------------------------------------ ÖN SAYFALAR
def kapak():
    fig = plt.figure(figsize=(H.SAYFA_W * H.MM, H.SAYFA_H * H.MM))
    fig.patch.set_facecolor("white")
    fig.add_artist(plt.Line2D([0.08, 0.92], [0.845, 0.845], color="#141414", lw=2.2))
    fig.text(0.08, 0.885, " ".join("KPSS LİSANS · GENEL KÜLTÜR"), fontsize=9,
             color="#5c5c5c", fontweight="bold")
    fig.text(0.08, 0.72, "TÜRKİYE COĞRAFYASI", fontsize=40, fontweight="bold",
             color="#111111", va="center")
    fig.text(0.08, 0.625, "Harita Portföyü", fontsize=26, color="#3a3a3a", va="center")
    fig.text(0.08, 0.545,
             "29 konu · her konu için etiketli cevap anahtarı + dilsiz çalışma haritası",
             fontsize=11, color="#555555", va="center")

    ax = fig.add_axes([0.08, 0.12, 0.50, 0.36])
    ax.set_xticks([]); ax.set_yticks([])
    for s in ax.spines.values():
        s.set_visible(False)
    x0, x1, y0, y1 = H.pencere((0.50 * 297) / (0.36 * 210))
    ax.set_xlim(x0, x1); ax.set_ylim(y0, y1); ax.set_aspect("equal", adjustable="datalim")
    import geopandas as gpd
    import numpy as np
    kirp = FZ._turkiye_kirp(ax)
    FZ.dagsuyu_kabartma(ax)
    for im in ax.images:
        im.set_clip_path(kirp)
    gpd.GeoSeries([H.turkiye()], crs=H.LCC).boundary.plot(ax=ax, color="#111111", lw=1.0)

    fig.text(0.63, 0.44, "İÇİNDEKİ VERİ KAYNAKLARI", fontsize=9, fontweight="bold",
             color="#111111")
    for i, s in enumerate([
        "İl sınırları — 81 il, resmî sınır verisi",
        "Yükselti — AWS Terrain Tiles (SRTM/ASTER türevi)",
        "Kıyı · göl · akarsu — Natural Earth 10m",
        "İklim — WorldClim v2.1 (1970–2000 normalleri)",
        "Nüfus — TÜİK ADNKS 2025",
    ]):
        fig.text(0.63, 0.40 - i * 0.030, "· " + s, fontsize=7.6, color="#4a4a4a")
    fig.text(0.63, 0.20,
             "Tüm nokta konumları il sınırı verisiyle,\n"
             "zirve koordinatları yükselti verisiyle\ndoğrulanmıştır.",
             fontsize=7.6, color="#4a4a4a", va="top")
    fig.add_artist(plt.Line2D([0.08, 0.92], [0.075, 0.075], color="#c8c8c8", lw=0.8))
    fig.text(0.08, 0.050, "Siyah-beyaz yazıcı için optimize edilmiştir · A4 yatay",
             fontsize=7.4, color="#6f6f6f")
    return fig


def icindekiler():
    fig = plt.figure(figsize=(H.SAYFA_W * H.MM, H.SAYFA_H * H.MM))
    fig.patch.set_facecolor("white")
    fig.text(0.05, 0.935, "İÇİNDEKİLER", fontsize=17, fontweight="bold", va="center")
    fig.add_artist(plt.Line2D([0.05, 0.95], [0.905, 0.905], color="#141414", lw=1.1))

    sayfa_no = 3
    sol, ust = 0.05, 0.865
    sutun_g, satir_y = 0.475, 0.0245
    i = 0
    onceki = None
    for bolum, baslik, alt, fn, duzen in HARITALAR:
        sutun = 0 if i < 17 else 1
        yy = ust - (i if i < 17 else i - 17) * satir_y
        x = sol + sutun * sutun_g
        if bolum != onceki:
            fig.text(x, yy, bolum.upper(), fontsize=6.3, fontweight="bold",
                     color="#6a6a6a", va="center")
            onceki = bolum
            i += 1
            sutun = 0 if i < 17 else 1
            yy = ust - (i if i < 17 else i - 17) * satir_y
            x = sol + sutun * sutun_g
        fig.text(x + 0.012, yy, baslik, fontsize=7.5, va="center")
        fig.text(x + 0.44, yy, f"{sayfa_no}–{sayfa_no+1}", fontsize=7.0, va="center",
                 ha="right", color="#555555")
        sayfa_no += 2
        i += 1

    fig.add_artist(plt.Line2D([0.05, 0.95], [0.075, 0.075], color="#c8c8c8", lw=0.8))
    fig.text(0.05, 0.050,
             "Her konu iki sayfadır: solda ETİKETLİ cevap anahtarı, sağda DİLSİZ çalışma haritası. "
             "Numaralar iki sayfada aynıdır.",
             fontsize=7.0, color="#5f5f5f")
    return fig


def nasil_kullanilir():
    fig = plt.figure(figsize=(H.SAYFA_W * H.MM, H.SAYFA_H * H.MM))
    fig.patch.set_facecolor("white")
    fig.text(0.05, 0.935, "NASIL ÇALIŞILIR + KPSS COĞRAFYA KONU DAĞILIMI",
             fontsize=15, fontweight="bold", va="center")
    fig.add_artist(plt.Line2D([0.05, 0.95], [0.905, 0.905], color="#141414", lw=1.1))

    fig.text(0.05, 0.855, "SINAVDAKİ YERİ", fontsize=9, fontweight="bold")
    for i, s in enumerate([
        "KPSS Lisans Genel Kültür testi 60 sorudur:",
        "   · Tarih .................................. 27 soru",
        "   · TÜRKİYE COĞRAFYASI ....... 18 soru",
        "   · Temel Yurttaşlık Bilgisi ......... 9 soru",
        "   · Güncel Bilgiler ....................... 6 soru",
        "",
        "18 coğrafya sorusunun büyük bölümü doğrudan",
        "harita bilgisiyle ya da haritadan çıkarım yaparak",
        "çözülür. Bu portföy o 18 soruyu hedefler.",
    ]):
        fig.text(0.05, 0.815 - i * 0.028, s, fontsize=8.0, color="#333333")

    fig.text(0.38, 0.855, "ÜÇ ADIMDA ÇALIŞMA YÖNTEMİ", fontsize=9, fontweight="bold")
    for i, s in enumerate([
        "1  ETİKETLİ sayfayı 3–4 dakika incele. Sadece adları",
        "    değil, KONUMU ve NEDENİ oku (neden orada?).",
        "",
        "2  Sayfayı çevir. DİLSİZ haritada numaraların",
        "    karşısını hafızandan doldur. Bakma.",
        "",
        "3  Etiketli sayfayla karşılaştır. Yanlışları işaretle;",
        "    ertesi gün SADECE yanlışları tekrar et.",
        "",
        "Dilsiz sayfaları birkaç kez çoğaltıp aynı haritayı",
        "gün aşırı tekrar etmek en verimli yöntemdir.",
    ]):
        fig.text(0.38, 0.815 - i * 0.028, s, fontsize=8.0, color="#333333")

    fig.text(0.05, 0.505, "HAFTAYA SINAV VARSA — 7 GÜNLÜK SIRALAMA",
             fontsize=9, fontweight="bold")
    plan = [
        ("1. gün", "Konum, komşular, iller, bölgeler, bölümler (s. 3–12)",
         "Bölge sınırlarını ve hangi ilin hangi bölgede olduğunu oturt."),
        ("2. gün", "Yer şekilleri: dağlar, ovalar, platolar (s. 13–18)",
         "Dağ sıralarının YÖNÜ ile iklim arasındaki bağı kur."),
        ("3. gün", "Akarsular, göller, barajlar, faylar (s. 19–26)",
         "Göllerin OLUŞUM tipi ve akarsuların döküldüğü deniz kritik."),
        ("4. gün", "İklim, yağış, sıcaklık, bitki, toprak (s. 27–36)",
         "En çok/az yağış ve sıcaklık uçlarını ezberle."),
        ("5. gün", "Nüfus, göç + Tarım I-II (s. 37–46)",
         "Sık/seyrek nüfusun NEDENLERİ sorulur."),
        ("6. gün", "Tarım III, hayvancılık, su ürünleri, madenler (s. 47–54)",
         "Ürün–iklim ve maden–yer eşleşmelerini kartla çalış."),
        ("7. gün", "Enerji, sanayi, ulaşım, turizm + tüm dilsiz sayfalar (s. 55–62)",
         "Son gün yalnızca dilsiz haritaları baştan sona doldur."),
    ]
    for i, (g, k, n) in enumerate(plan):
        y = 0.455 - i * 0.052
        fig.text(0.05, y, g, fontsize=8.2, fontweight="bold", color="#111111")
        fig.text(0.115, y, k, fontsize=8.0, color="#222222")
        fig.text(0.115, y - 0.021, n, fontsize=6.8, color="#6a6a6a")

    fig.add_artist(plt.Line2D([0.05, 0.95], [0.062, 0.062], color="#c8c8c8", lw=0.8))
    fig.text(0.05, 0.038,
             "Not: Sayfa numaraları bu portföyün kendi numaralarıdır.",
             fontsize=6.8, color="#6f6f6f")
    return fig


# --------------------------------------------------------------- ÜRETİM
def uret(png_de_yaz=True):
    os.makedirs(CIKTI, exist_ok=True)
    os.makedirs(PNG, exist_ok=True)
    pdf_yol = os.path.join(CIKTI, "KPSS-Cografya-Harita-Portfoyu.pdf")
    n = 0
    with PdfPages(pdf_yol) as pdf:
        for ad, f in [("00-kapak", kapak), ("01-icindekiler", icindekiler),
                      ("02-nasil-calisilir", nasil_kullanilir)]:
            fig = f()
            pdf.savefig(fig)
            if png_de_yaz:
                fig.savefig(os.path.join(PNG, f"{ad}.png"), dpi=110)
            plt.close(fig); n += 1

        sno = 3
        for idx, (bolum, baslik, alt, fn, duzen) in enumerate(HARITALAR, 1):
            for mod in ("dolu", "bos"):
                fig, ax, kax = H.sayfa(baslik, bolum, alt, mod, sno, duzen=duzen)
                if duzen == "izgara":
                    fn(fig, mod)
                else:
                    fn(fig, ax, kax, mod)
                pdf.savefig(fig)
                if png_de_yaz:
                    fig.savefig(os.path.join(PNG, f"{sno:02d}-{idx:02d}-{mod}.png"), dpi=105)
                plt.close(fig)
                sno += 1; n += 1
            print(f"  [{idx:2d}/{len(HARITALAR)}] {baslik}")
        d = pdf.infodict()
        d["Title"] = "KPSS Lisans — Türkiye Coğrafyası Harita Portföyü"
        d["Subject"] = "Etiketli + dilsiz harita seti"
    print(f"\n{n} sayfa · {pdf_yol}")
    return pdf_yol


if __name__ == "__main__":
    uret(png_de_yaz="--png" in sys.argv)
