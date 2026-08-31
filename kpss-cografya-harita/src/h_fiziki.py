# -*- coding: utf-8 -*-
"""Fiziki coğrafya haritaları."""
from __future__ import annotations

import geopandas as gpd
import numpy as np
from matplotlib.lines import Line2D
from matplotlib.patches import Rectangle

import cizim as C
import harita_temel as H
import veri_fiziki as F
import veri_idari as V


# ------------------------------------------------------------------ 06
def dagsuyu_kabartma(ax, alfa=1.0):
    """Gerçek DEM'den gri hipsometrik ton + gölgelendirme.

    Görüntü TEK KANALLI gri olarak gömülür (RGBA değil): kabartma zaten gri
    olduğu için dört kanal aynı veriyi dört kez yazıyor ve PDF'i gereksiz
    büyütüyordu. Deniz alanı ayrıca kırpma yoluyla (clip path) gizlendiği için
    alfa kanalına da gerek yoktur.
    """
    elev, ext, pm = H.dem_lcc(1600)
    hs = H.kabartma(elev, pm, abartma=5.0)

    # hipsometrik gri: 0 m açık, 3000+ m koyu  (S/B yazıcıda ayrışsın diye)
    z = np.clip(np.nan_to_num(elev, nan=0.0), 0, 3200)
    ton = 0.97 - 0.5 * (z / 3200.0) ** 0.72
    gri = np.clip(ton * (0.55 + 0.45 * hs), 0, 1)
    if alfa < 1.0:                      # alfa yerine beyaza doğru karıştır
        gri = 1.0 - alfa * (1.0 - gri)
    # Kara dışı sabit beyaz: kırpma zaten gizliyor, ama deniz tabanı verisinin
    # gürültüsü sıkıştırılamadığı için görüntüyü megabaytlarca şişiriyordu.
    gri = np.where(np.isfinite(elev) & (elev > -50), gri, 1.0)

    ax.imshow(gri, cmap="gray", vmin=0.0, vmax=1.0,
              extent=(ext[0], ext[1], ext[2], ext[3]), origin="upper",
              zorder=2.5, interpolation="bilinear")
    return elev, ext


def _turkiye_kirp(ax):
    """Kabartmayı Türkiye sınırına kırpar."""
    from matplotlib.path import Path
    from matplotlib.patches import PathPatch
    tr = H.turkiye()
    yollar = []
    for g in (tr.geoms if tr.geom_type == "MultiPolygon" else [tr]):
        v = np.array(g.exterior.coords)
        yollar.append(Path(v))
    birlesik = Path.make_compound_path(*yollar)
    yama = PathPatch(birlesik, transform=ax.transData, facecolor="none", lw=0)
    ax.add_patch(yama)
    return yama


def harita_yerelsekilleri(fig, ax, kax, mod):
    H.temel(ax, iller_cizgi=False, il_lw=0.2, ulke_lw=1.15)
    if mod == "dolu":
        kirp = _turkiye_kirp(ax)
        elev, ext = dagsuyu_kabartma(ax)
        for im in ax.images:
            im.set_clip_path(kirp)
    else:
        H.iller().boundary.plot(ax=ax, color="#e0e0e0", linewidth=0.2, zorder=3)

    # sıradağ eksenleri
    for ad, elo, ela, hat in F.SIRADAGLAR:
        xs, ys = zip(*[H.xy(lo, la) for lo, la in hat])
        ax.plot(xs, ys, color="#141414", lw=1.6, zorder=6.5,
                solid_capstyle="round", alpha=0.9)
        if mod == "dolu":
            ex, ey = H.xy(elo, ela)
            ax.text(ex, ey, ad, fontsize=5.9, ha="center", va="center",
                    fontweight="bold", color="#111111", zorder=7.2,
                    bbox=dict(boxstyle="round,pad=0.24", facecolor="white",
                              edgecolor="#b5b5b5", lw=0.4, alpha=0.88))

    kayitlar = [(f"{a} ({y} m)", lo, la) for a, lo, la, y, t, il in F.ZIRVELER]
    C.numarali_noktalar(ax, kax, kayitlar, mod, sutun=5,
                        baslik="ZİRVELER — numarayı haritada bul, adını ve yüksekliğini yaz",
                        boyut=52, numara_yazi=5.0)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    if mod == "dolu":
        yukselti_anahtari(ax)
    C.harita_notu(ax, "Kabartma: AWS Terrain Tiles gerçek yükselti verisi (SRTM/ASTER türevi) · "
                      "Zirve konumları bu veriden doğrulanmıştır", y=0.012)


def yukselti_anahtari(ax, x=0.845, y=0.045, w=0.135, h=0.030):
    """Hipsometrik gri ton anahtarı (m)."""
    import matplotlib.transforms as mt
    kad = [0, 500, 1000, 1500, 2000, 3200]
    ax.add_patch(Rectangle((x - 0.012, y - 0.018), w + 0.026, h + 0.072,
                           transform=ax.transAxes, facecolor="white",
                           edgecolor="#c8c8c8", lw=0.5, alpha=0.92, zorder=8.8))
    n = 60
    for i in range(n):
        z = 3200 * i / (n - 1)
        ton = 0.97 - 0.5 * (z / 3200.0) ** 0.72
        ax.add_patch(Rectangle((x + w * i / n, y), w / n * 1.05, h,
                               transform=ax.transAxes, facecolor=str(ton),
                               edgecolor="none", zorder=9))
    ax.add_patch(Rectangle((x, y), w, h, transform=ax.transAxes, facecolor="none",
                           edgecolor="#4a4a4a", lw=0.5, zorder=9.1))
    for z in kad:
        fx = x + w * (z / 3200.0)
        ax.text(fx, y - 0.012, str(z), transform=ax.transAxes, fontsize=4.4,
                ha="center", va="top", zorder=9.2)
    ax.text(x + w / 2, y + h + 0.012, "YÜKSELTİ (m)", transform=ax.transAxes,
            fontsize=4.8, ha="center", va="bottom", fontweight="bold", zorder=9.2)


# ------------------------------------------------------------------ 09
def harita_goller(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22)
    H.ne_goller().plot(ax=ax, facecolor="#cfd8de", edgecolor="#6f7c85",
                       linewidth=0.4, zorder=4)
    tipler = {}
    for ad, lo, la, tip, alan, notu in F.GOLLER:
        tipler.setdefault(tip.split(" (")[0], []).append((ad, lo, la))
    sirali = sorted(tipler.items(), key=lambda kv: -len(kv[1]))
    isaretler = ["o", "s", "^", "D", "v", "P", "*", "X"]
    kayitlar = []
    for i, (tip, gs) in enumerate(sirali):
        for ad, lo, la in gs:
            x, y = H.xy(lo, la)
            ax.scatter([x], [y], s=46, marker=isaretler[i % len(isaretler)],
                       facecolor="white", edgecolor="#1c1c1c", linewidth=0.85, zorder=7)
            kayitlar.append((ad, x, y, len(kayitlar) + 1))
    for ad, x, y, n in kayitlar:
        ax.text(x, y, str(n), fontsize=4.2, ha="center", va="center",
                fontweight="bold", zorder=7.6)
    C.liste_paneli(kax, [str(i) for i in range(1, len(kayitlar) + 1)],
                   [k[0] for k in kayitlar], mod, sutun=5,
                   baslik="GÖLLER — oluşumlarına göre (sembol = oluşum tipi)", yazi=5.5)
    # sembol açıklaması harita üstünde
    el = [Line2D([0], [0], marker=isaretler[i % len(isaretler)], color="none",
                 markerfacecolor="white", markeredgecolor="#1c1c1c", markersize=5,
                 label=tip if mod == "dolu" else "?" * 12)
          for i, (tip, _) in enumerate(sirali)]
    ax.legend(handles=el, loc="lower left", fontsize=5.4, frameon=True,
              framealpha=0.92, edgecolor="#c8c8c8", ncol=2,
              bbox_to_anchor=(0.005, 0.02)).set_zorder(9)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


# ------------------------------------------------------------------ 07
def harita_ova_plato(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    kirp = _turkiye_kirp(ax)
    elev, ext, pm = H.dem_lcc(1200)
    hs = H.kabartma(elev, pm, abartma=4.0)
    gri = 1.0 - 0.5 * (1.0 - (hs * 0.4 + 0.6))     # tek kanal, alfa yerine karışım
    gri = np.where(np.isfinite(elev) & (elev > -50), gri, 1.0)   # kara dışı düz beyaz
    im = ax.imshow(gri, cmap="gray", vmin=0.0, vmax=1.0,
                   extent=(ext[0], ext[1], ext[2], ext[3]), origin="upper",
                   zorder=2.5, interpolation="bilinear")
    im.set_clip_path(kirp)

    gruplar = [("Delta ovası", "^", [(a, lo, la) for a, lo, la, t, n in F.OVALAR if t == "delta"]),
               ("Kıyı ovası", "s", [(a, lo, la) for a, lo, la, t, n in F.OVALAR if t == "kiyi"]),
               ("İç ova", "o", [(a, lo, la) for a, lo, la, t, n in F.OVALAR if t == "ic"]),
               ("Plato", "D", [(a, lo, la) for a, lo, la, b in F.PLATOLAR])]
    C.sembol_katmani(ax, gruplar, boyut=44)
    n = 1
    kayitlar = []
    for ad, isaret, noktalar in gruplar:
        for et, lo, la in noktalar:
            x, y = H.xy(lo, la)
            ax.text(x, y, str(n), fontsize=4.2, ha="center", va="center",
                    fontweight="bold", zorder=7.6)
            kayitlar.append(et); n += 1
    C.liste_paneli(kax, [str(i) for i in range(1, n)], kayitlar, mod, sutun=5,
                   baslik="OVALAR (▲ delta · ■ kıyı · ● iç) ve PLATOLAR (◆)", yazi=5.3)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


# ------------------------------------------------------------------ 08
def _akarsu_katman(ax, lw_ana=1.15, lw_yan=0.6):
    """NE 10m küresel + Avrupa akarsu katmanlarının birleşimi."""
    import geopandas as gpd
    import os
    a = H.ne_akarsular()
    yol = os.path.join(H.VERI, "ne_10m_rivers_europe.zip")
    b = gpd.read_file(f"zip://{yol}").cx[24:47, 34:44].to_crs(H.LCC)
    tr = H.turkiye().buffer(12000)
    for g, lw in ((a, lw_ana), (b, lw_yan)):
        try:
            kes = g[g.intersects(tr)].copy()
            kes["geometry"] = kes.geometry.intersection(tr)
            kes.plot(ax=ax, color="#54606a", linewidth=lw, zorder=4.5)
        except Exception:
            pass


def harita_akarsular(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.2, ulke_lw=1.2)
    _akarsu_katman(ax)
    H.ne_goller().plot(ax=ax, facecolor="#c9d2d9", edgecolor="#6f7c85",
                       linewidth=0.35, zorder=4.6)
    kayitlar = [(f"{a} — {d} ({u} km)", 0, 0) for a, u, d, n in F.AKARSU_KUNYE]
    # akarsu adlarını haritada değil panelde göster; haritada numara konumu
    yerler = {
        "Kızılırmak": (34.30, 39.60), "Fırat": (38.60, 39.10), "Sakarya": (30.90, 40.10),
        "Murat": (41.60, 39.10), "Aras": (43.30, 40.10), "Seyhan": (35.30, 37.60),
        "B. Menderes": (28.30, 37.70), "Dicle": (40.60, 37.60), "Yeşilırmak": (36.20, 40.50),
        "Ceyhan": (36.40, 37.40), "Meriç": (26.40, 41.20), "Çoruh": (41.30, 40.60),
        "Gediz": (28.10, 38.60), "Kelkit": (38.20, 40.30), "Susurluk": (28.20, 39.80),
        "Göksu": (33.30, 36.90), "Asi": (36.30, 36.30),
    }
    for i, (a, u, d, n) in enumerate(F.AKARSU_KUNYE, 1):
        if a in yerler:
            x, y = H.xy(*yerler[a])
            ax.scatter([x], [y], s=46, marker="o", facecolor="white",
                       edgecolor="#1c1c1c", linewidth=0.85, zorder=7)
            ax.text(x, y, str(i), fontsize=4.6, ha="center", va="center",
                    fontweight="bold", zorder=7.6)
    C.liste_paneli(kax, [str(i) for i in range(1, len(F.AKARSU_KUNYE) + 1)],
                   [f"{a} · {d} · {u} km" for a, u, d, n in F.AKARSU_KUNYE], mod,
                   sutun=3, baslik="AKARSULAR — adı · döküldüğü yer · uzunluk",
                   yazi=5.5, genislik=0.68)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.71, 0.96, 0.29, "KAPALI HAVZALAR",
                       [f"· {a}" + (f" — {n}" if n else "") for a, lo, la, n in F.KAPALI_HAVZALAR],
                       yazi=5.2)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
    C.harita_notu(ax, "Akarsu çizgileri Natural Earth 10m verisidir")


# ------------------------------------------------------------------ 10
def harita_barajlar(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.2, ulke_lw=1.2)
    _akarsu_katman(ax, 0.85, 0.45)
    H.ne_goller().plot(ax=ax, facecolor="#c9d2d9", edgecolor="#6f7c85",
                       linewidth=0.35, zorder=4.6)
    kayitlar = [(f"{a} ({ak})", lo, la) for a, lo, la, ak, n in F.BARAJLAR]
    C.numarali_noktalar(ax, kax, kayitlar, mod, sutun=4, isaret="D", boyut=48,
                        baslik="BARAJLAR — adını ve üzerinde kurulu olduğu akarsuyu yaz",
                        numara_yazi=4.6)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


# ------------------------------------------------------------------ 11
def harita_volkanik_karstik(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.22, ulke_lw=1.2)
    gruplar = [("Volkanik alan", "^", [(a, lo, la) for a, lo, la in F.VOLKANIK_ALANLAR]),
               ("Karstik alan", "o", [(a, lo, la) for a, lo, la in F.KARSTIK_ALANLAR]),
               ("Volkanik dağ", "*",
                [(a, lo, la) for a, lo, la, y, t, il in F.ZIRVELER if t == "volkanik"])]
    C.sembol_katmani(ax, gruplar, boyut=52)
    n, kayitlar = 1, []
    for ad, isaret, noktalar in gruplar:
        for et, lo, la in noktalar:
            x, y = H.xy(lo, la)
            ax.text(x, y, str(n), fontsize=4.2, ha="center", va="center",
                    fontweight="bold", zorder=7.6)
            kayitlar.append(f"{et}"); n += 1
    C.liste_paneli(kax, [str(i) for i in range(1, n)], kayitlar, mod, sutun=4,
                   baslik="VOLKANİK ALANLAR (▲ alan · ★ volkanik dağ) ve KARSTİK ALANLAR (●)",
                   yazi=5.6)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)


# ------------------------------------------------------------------ 12
def harita_faylar(fig, ax, kax, mod):
    H.temel(ax, il_lw=0.25, ulke_lw=1.2)
    stiller = {"KUZEY ANADOLU FAY HATTI (KAF)": ("-", 2.0),
               "DOĞU ANADOLU FAY HATTI (DAF)": ("-", 2.0),
               "BATI ANADOLU GRABENLERİ": ((0, (5, 2)), 1.5)}
    for i, (ad, hat) in enumerate(F.FAYLAR, 1):
        xs, ys = zip(*[H.xy(lo, la) for lo, la in hat])
        ls, lw = stiller.get(ad, ("-", 1.6))
        ax.plot(xs, ys, color="#101010", lw=lw, ls=ls, zorder=6.5,
                solid_capstyle="round")
        # fay dişleri yerine kalın hat + numaralı etiket
        j = len(xs) // 2
        ax.scatter([xs[j]], [ys[j]], s=90, marker="o", facecolor="white",
                   edgecolor="#101010", linewidth=1.0, zorder=7)
        ax.text(xs[j], ys[j], str(i), fontsize=5.6, ha="center", va="center",
                fontweight="bold", zorder=7.5)
    C.liste_paneli(kax, ["1", "2", "3"], [a for a, _ in F.FAYLAR], mod, sutun=1,
                   baslik="DİRİ FAY KUŞAKLARI", yazi=6.2, genislik=0.42)
    if mod == "dolu":
        C.bilgi_kutusu(kax, 0.45, 0.96, 0.54, "DEPREM RİSKİ — SINAVDA SORULANLAR", [
            "· KAF: Saros Körfezi'nden başlar, Marmara–Bolu–Erzincan üzerinden Karlıova'ya uzanır.",
            "· DAF: Karlıova'dan başlar, Elazığ–Adıyaman–K.Maraş üzerinden Hatay'a (Amik Ovası) iner.",
            "· İkisi Bingöl-KARLIOVA'da birleşir (Karlıova üçlü eklemi).",
            "· Batı Anadolu'da graben (çöküntü) sistemleri: Bakırçay, Gediz, K. ve B. Menderes.",
            "· Deprem riski EN AZ olan yerler: Konya–Karaman çevresi, Taşeli Platosu ve",
            "  Mardin–Şanlıurfa çevresi (Güneydoğu Anadolu'nun büyük bölümü).",
        ], yazi=5.4)
    H.denizleri_yaz(ax); H.olcek_kuzey(ax)
