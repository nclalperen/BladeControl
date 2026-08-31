# -*- coding: utf-8 -*-
"""KPSS Coğrafya harita portföyü — temel çizim altyapısı.

Tüm haritalar gerçek coğrafi veriden üretilir:
  * il sınırları      : tr-iller.json (EPSG:4326, 81 il)
  * kıyı / göl / akarsu: Natural Earth 10m
  * yükselti          : AWS Terrain Tiles (terrarium, z8) → gerçek DEM
  * iklim             : WorldClim v2.1 10' (1970-2000 normalleri)

Projeksiyon: Lambert Konformal Konik (Türkiye için standart paraleller 37°/41.5°).
Sayfa: A4 yatay, siyah-beyaz yazıcıya göre optimize (gri tonlar + tarama desenleri).
"""
from __future__ import annotations

import math
import os
import zipfile

import geopandas as gpd
import matplotlib as mpl
import numpy as np
from matplotlib import pyplot as plt
from matplotlib.patches import FancyBboxPatch, Rectangle
from shapely.geometry import box

mpl.use("Agg")

KOK = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERI = os.path.join(KOK, "veri")
CIKTI = os.path.join(KOK, "cikti")

# --- projeksiyon --------------------------------------------------------
LCC = "+proj=lcc +lat_1=37 +lat_2=41.5 +lat_0=39 +lon_0=35.5 +x_0=0 +y_0=0 +datum=WGS84 +units=m +no_defs"

# --- sayfa ölçüleri (mm) ------------------------------------------------
SAYFA_W, SAYFA_H = 297.0, 210.0
MM = 1 / 25.4  # mm -> inch

# --- siyah-beyaz palet ---------------------------------------------------
# Yazıcıda ayırt edilebilir olması için 5'ten fazla gri ton kullanılmaz;
# fazlası tarama (hatch) desenleriyle ayrılır.
GRI = {
    "deniz":        "#e9edf0",
    "deniz_cizgi":  "#8a949c",
    "komsu":        "#f2f2f2",
    "komsu_cizgi":  "#b4b4b4",
    "kara":         "#ffffff",
    "il_cizgi":     "#c3c3c3",
    "ulke_cizgi":   "#1a1a1a",
    "metin":        "#111111",
    "soluk":        "#5c5c5c",
    "cerceve":      "#1a1a1a",
    "kutu":         "#f7f7f7",
    "kutu_cizgi":   "#9a9a9a",
}
DOLGU = ["#ffffff", "#e2e2e2", "#c4c4c4", "#a3a3a3", "#7f7f7f", "#5c5c5c"]
TARAMA = ["", "///", "...", "\\\\\\", "xxx", "+++", "ooo", "***", "|||", "---"]

plt.rcParams.update({
    "font.family": "DejaVu Sans",
    "pdf.fonttype": 42,      # gömülü TrueType — her yazıcıda aynı görünür
    "ps.fonttype": 42,
    "savefig.dpi": 400,
    "figure.dpi": 110,
})

_ONBELLEK: dict = {}


# ======================================================================
#  VERİ YÜKLEME
# ======================================================================
def iller() -> gpd.GeoDataFrame:
    """81 il, LCC'ye dönüştürülmüş. 'Afyon' -> 'Afyonkarahisar' düzeltmesi yapılır."""
    if "iller" not in _ONBELLEK:
        g = gpd.read_file(os.path.join(VERI, "tr-iller.json"))
        g["name"] = g["name"].replace({"Afyon": "Afyonkarahisar", "Hakkari": "Hakkâri"})
        g = g.to_crs(LCC)
        g["alan_km2"] = g.geometry.area / 1e6
        _ONBELLEK["iller"] = g.set_index("name", drop=False)
    return _ONBELLEK["iller"]


def turkiye():
    """Tüm illerin birleşimi = Türkiye kara sınırı (LCC).

    İl poligonları kenarlarını birebir paylaşmadığı için birleşim kılcal
    boşluklar (iç halkalar) bırakır; bunlar temizlenir, aksi hâlde ülke
    sınırı çizilirken yurt içinde kopuk çizgiler olarak görünür.
    """
    if "turkiye" not in _ONBELLEK:
        from shapely.geometry import MultiPolygon, Polygon
        g = iller().geometry.union_all().buffer(300).buffer(-300).buffer(0)
        pars = list(g.geoms) if g.geom_type == "MultiPolygon" else [g]
        pars = [Polygon(p.exterior) for p in pars if p.area > 2e6]   # >2 km²
        _ONBELLEK["turkiye"] = MultiPolygon(pars) if len(pars) > 1 else pars[0]
    return _ONBELLEK["turkiye"]


def _ne(ad: str) -> gpd.GeoDataFrame:
    if ad not in _ONBELLEK:
        _ONBELLEK[ad] = gpd.read_file(f"zip://{os.path.join(VERI, ad + '.zip')}")
    return _ONBELLEK[ad]


def komsular() -> gpd.GeoDataFrame:
    """Türkiye dışındaki ülkeler (LCC).

    Yalnızca harita penceresine kırpılır ve sadeleştirilir: ham Natural Earth
    verisi 80 bin köşe içerir ve her sayfada yeniden çizildiği için PDF'i
    gereksiz yere büyütür. 600 m tolerans bu ölçekte (1 mm ≈ 6,5 km) görünmez.
    """
    if "komsu" not in _ONBELLEK:
        g = _ne("ne_10m_admin_0_countries")
        g = g[g["ADM0_A3"] != "TUR"].copy()
        g = g.cx[18:56, 30:48].to_crs(LCC)
        x0, x1, y0, y1 = pencere()
        pay = (x1 - x0) * 0.03
        kutu = box(x0 - pay, y0 - pay, x1 + pay, y1 + pay)
        g["geometry"] = g.geometry.intersection(kutu).simplify(600)
        g = g[~g.geometry.is_empty]
        _ONBELLEK["komsu"] = g
    return _ONBELLEK["komsu"]


def ne_goller() -> gpd.GeoDataFrame:
    if "goller" not in _ONBELLEK:
        g = _ne("ne_10m_lakes").cx[24:47, 34:44].to_crs(LCC)
        _ONBELLEK["goller"] = g
    return _ONBELLEK["goller"]


def ne_akarsular() -> gpd.GeoDataFrame:
    if "akarsu" not in _ONBELLEK:
        g = _ne("ne_10m_rivers_lake_centerlines").cx[24:47, 34:44].to_crs(LCC)
        _ONBELLEK["akarsu"] = g
    return _ONBELLEK["akarsu"]


# ======================================================================
#  HARİTA PENCERESİ
# ======================================================================
HARITA_KUTU  = [0.038, 0.262, 0.924, 0.600]   # harita ekseni (figür oranı)
ANAHTAR_KUTU = [0.038, 0.068, 0.924, 0.178]   # alt açıklama paneli
HARITA_ORAN  = (HARITA_KUTU[2] * SAYFA_W) / (HARITA_KUTU[3] * SAYFA_H)


def pencere(oran: float | None = None):
    """Türkiye'yi ortalayan, verilen en/boy oranına tam oturan LCC penceresi."""
    oran = HARITA_ORAN if oran is None else oran
    key = f"pencere_{oran:.4f}"
    if key in _ONBELLEK:
        return _ONBELLEK[key]
    x0, y0, x1, y1 = turkiye().bounds
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    w, h = (x1 - x0) * 1.045, (y1 - y0) * 1.085
    if w / h < oran:
        w = h * oran
    else:
        h = w / oran
    _ONBELLEK[key] = (cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2)
    return _ONBELLEK[key]


# ======================================================================
#  GERÇEK YÜKSELTİ (DEM) — LCC'ye warp
# ======================================================================
def dem_lcc(nx: int = 1500):
    """Terrarium DEM'i LCC ızgarasına yeniden örnekler.

    Kaynak Web Mercator dizilimindedir; hedef ızgaranın her pikseli için
    ters dönüşümle (lon, lat) hesaplanıp bilineer olmadan en yakın komşu
    ile örneklenir — 4608x2560 kaynakta A4 baskı için fazlasıyla yeterli.
    """
    key = f"dem_{nx}"
    if key in _ONBELLEK:
        return _ONBELLEK[key]

    import pyproj
    d = np.load(os.path.join(VERI, "dem_tr.npz"))
    elev = d["elev"].astype(np.float32)
    W, E, S, N = [float(v) for v in d["extent"]]
    h, w = elev.shape

    x0, x1, y0, y1 = pencere()
    ny = int(nx * (y1 - y0) / (x1 - x0))
    xs = np.linspace(x0, x1, nx)
    ys = np.linspace(y1, y0, ny)          # üstten alta
    gx, gy = np.meshgrid(xs, ys)

    tf = pyproj.Transformer.from_crs(LCC, "EPSG:4326", always_xy=True)
    lon, lat = tf.transform(gx, gy)

    # Web Mercator satır indeksi (enlem doğrusal değildir)
    def merc_y(la):
        la = np.clip(la, -85.0, 85.0)
        return np.log(np.tan(np.pi / 4 + np.radians(la) / 2))

    myN, myS = merc_y(N), merc_y(S)
    row = (myN - merc_y(lat)) / (myN - myS) * (h - 1)
    col = (lon - W) / (E - W) * (w - 1)

    gecerli = (row >= 0) & (row <= h - 1) & (col >= 0) & (col <= w - 1)
    ri = np.clip(np.round(row), 0, h - 1).astype(np.int32)
    ci = np.clip(np.round(col), 0, w - 1).astype(np.int32)
    out = elev[ri, ci]
    out[~gecerli] = np.nan

    piksel_m = (x1 - x0) / nx
    _ONBELLEK[key] = (out, (x0, x1, y0, y1), piksel_m)
    return _ONBELLEK[key]


def turkiye_maskesi(shape, extent):
    """Verilen ızgara için Türkiye içi/dışı boolean maskesi (nokta-poligon testi)."""
    key = f"maske_{shape}_{tuple(round(v) for v in extent)}"
    if key in _ONBELLEK:
        return _ONBELLEK[key]
    from matplotlib.path import Path
    ny, nx = shape
    x0, x1, y0, y1 = extent
    gx, gy = np.meshgrid(np.linspace(x0, x1, nx), np.linspace(y1, y0, ny))
    pts = np.column_stack([gx.ravel(), gy.ravel()])
    tr = turkiye()
    m = np.zeros(pts.shape[0], dtype=bool)
    for g in (tr.geoms if tr.geom_type == "MultiPolygon" else [tr]):
        m |= Path(np.asarray(g.exterior.coords)).contains_points(pts)
    _ONBELLEK[key] = m.reshape(ny, nx)
    return _ONBELLEK[key]


def kabartma(elev, piksel_m, azimut=315.0, yukselim=45.0, abartma=6.0):
    """Gerçek DEM'den gölgelendirme (hillshade). 0..1 arası döner."""
    z = np.nan_to_num(elev, nan=0.0) * abartma
    dy, dx = np.gradient(z, piksel_m, piksel_m)
    egim = np.arctan(np.hypot(dx, dy))
    bakı = np.arctan2(-dx, dy)
    az, yu = np.radians(360.0 - azimut + 90.0), np.radians(yukselim)
    hs = (np.sin(yu) * np.cos(egim) +
          np.cos(yu) * np.sin(egim) * np.cos(az - bakı))
    return np.clip(hs, 0, 1)


# ======================================================================
#  SAYFA DÜZENİ
# ======================================================================
def sayfa(baslik: str, ustbaslik: str, altbaslik: str = "",
          mod: str = "dolu", sayfa_no: int | None = None, duzen: str = "tek"):
    """A4 yatay sayfa açar; (fig, harita_ekseni) döndürür."""
    fig = plt.figure(figsize=(SAYFA_W * MM, SAYFA_H * MM))
    fig.patch.set_facecolor("white")

    # üst başlık bandı
    fig.text(0.038, 0.955, " ".join(ustbaslik.upper()), fontsize=6.4,
             color=GRI["soluk"], fontweight="bold", va="center")
    fig.text(0.038, 0.916, baslik, fontsize=16.5, color=GRI["metin"],
             fontweight="bold", va="center")
    if altbaslik:
        fig.text(0.038, 0.878, altbaslik, fontsize=8.2, color=GRI["soluk"], va="center")

    etiket = "CEVAP ANAHTARI" if mod == "dolu" else "DİLSİZ ÇALIŞMA HARİTASI"
    fig.text(0.962, 0.9345, etiket, fontsize=8.0, color="white", ha="right",
             va="center", fontweight="bold",
             bbox=dict(boxstyle="round,pad=0.42",
                       facecolor="#2b2b2b" if mod == "dolu" else "#6f6f6f",
                       edgecolor="none"))

    fig.add_artist(mpl.lines.Line2D([0.038, 0.962], [0.862, 0.862],
                                    color=GRI["cerceve"], lw=1.05))

    # alt bilgi
    fig.add_artist(mpl.lines.Line2D([0.038, 0.962], [0.052, 0.052],
                                    color=GRI["kutu_cizgi"], lw=0.6))
    fig.text(0.038, 0.030, "KPSS Lisans · Genel Kültür · Türkiye Coğrafyası — Harita Portföyü",
             fontsize=6.6, color=GRI["soluk"], va="center")
    if sayfa_no is not None:
        fig.text(0.962, 0.030, str(sayfa_no), fontsize=7.6, color=GRI["metin"],
                 ha="right", va="center", fontweight="bold")

    if duzen == "izgara":
        return fig, None, None

    ax = fig.add_axes(HARITA_KUTU)
    ax.set_facecolor("white")
    for s_ in ax.spines.values():
        s_.set_visible(False)
    ax.set_xticks([]); ax.set_yticks([])
    x0, x1, y0, y1 = pencere()
    ax.set_xlim(x0, x1); ax.set_ylim(y0, y1)
    ax.set_aspect("equal", adjustable="datalim")

    kax = fig.add_axes(ANAHTAR_KUTU)
    kax.set_xlim(0, 1); kax.set_ylim(0, 1)
    kax.set_xticks([]); kax.set_yticks([])
    for s_ in kax.spines.values():
        s_.set_visible(False)
    kax.set_facecolor("white")
    return fig, ax, kax


def temel(ax, iller_cizgi=True, deniz=True, komsu_etiket=True,
          il_lw=0.28, ulke_lw=1.15, kabartma_goster=False, kabartma_alfa=0.42):
    """Her haritanın altına serilen ortak taban."""
    x0, x1, y0, y1 = pencere()

    if deniz:
        ax.add_patch(Rectangle((x0, y0), x1 - x0, y1 - y0,
                               facecolor=GRI["deniz"], edgecolor="none", zorder=0))
    komsular().plot(ax=ax, facecolor=GRI["komsu"], edgecolor=GRI["komsu_cizgi"],
                    linewidth=0.5, zorder=1)

    if kabartma_goster:
        elev, ext, pm = dem_lcc()
        hs = kabartma(elev, pm)
        gri = hs * 0.55 + 0.45
        gri = 1.0 - kabartma_alfa * (1.0 - gri)     # tek kanallı gri
        ax.imshow(gri, cmap="gray", vmin=0.0, vmax=1.0,
                  extent=(ext[0], ext[1], ext[2], ext[3]),
                  origin="upper", zorder=2, interpolation="bilinear")

    il = iller()
    il.plot(ax=ax, facecolor="none" if kabartma_goster else GRI["kara"],
            edgecolor="none", zorder=1.6)
    if iller_cizgi:
        il.boundary.plot(ax=ax, color=GRI["il_cizgi"], linewidth=il_lw, zorder=3)

    gpd.GeoSeries([turkiye()], crs=LCC).boundary.plot(
        ax=ax, color=GRI["ulke_cizgi"], linewidth=ulke_lw, zorder=6)

    if komsu_etiket:
        for ad, lon, lat in KOMSU_ETIKET:
            _metin_lonlat(ax, lon, lat, ad, fontsize=6.4, color="#8e8e8e",
                          style="italic", zorder=3.4)
    return ax


KOMSU_ETIKET = [
    ("BULGARİSTAN", 26.55, 42.10), ("YUNANİSTAN", 25.55, 40.85),
    ("GÜRCİSTAN", 42.60, 42.05),   ("ERMENİSTAN", 44.90, 40.35),
    ("NAHÇIVAN", 45.35, 39.30),    ("İRAN", 45.35, 38.20),
    ("IRAK", 44.10, 36.35),        ("SURİYE", 39.40, 35.75),
]

DENIZ_ETIKET = [
    ("K A R A D E N İ Z", 34.20, 42.45), ("A K D E N İ Z", 31.20, 35.85),
    ("EGE D.", 26.35, 38.75),            ("MARMARA D.", 28.30, 40.79),
]


# ======================================================================
#  YARDIMCI ÇİZİM
# ======================================================================
def _tf():
    import pyproj
    if "tf_fwd" not in _ONBELLEK:
        _ONBELLEK["tf_fwd"] = pyproj.Transformer.from_crs("EPSG:4326", LCC, always_xy=True)
    return _ONBELLEK["tf_fwd"]


def xy(lon, lat):
    """Coğrafi koordinat -> harita koordinatı."""
    return _tf().transform(lon, lat)


def _metin_lonlat(ax, lon, lat, s, **kw):
    x, y = xy(lon, lat)
    kw.setdefault("clip_on", False)
    return ax.text(x, y, s, ha=kw.pop("ha", "center"), va=kw.pop("va", "center"), **kw)


def denizleri_yaz(ax, fontsize=7.2):
    for ad, lon, lat in DENIZ_ETIKET:
        _metin_lonlat(ax, lon, lat, ad, fontsize=fontsize, color="#7c868e",
                      style="italic", fontweight="bold", zorder=5)


def olcek_kuzey(ax, uzunluk_km=200):
    """Ölçek çubuğu + kuzey oku (sol alt)."""
    x0, x1, y0, y1 = pencere()
    L = uzunluk_km * 1000.0
    bx, by = x0 + (x1 - x0) * 0.022, y0 + (y1 - y0) * 0.055
    h = (y1 - y0) * 0.011
    for i in range(4):
        ax.add_patch(Rectangle((bx + i * L / 4, by), L / 4, h,
                               facecolor="#2b2b2b" if i % 2 == 0 else "white",
                               edgecolor="#2b2b2b", lw=0.55, zorder=9))
    ax.text(bx, by + h * 1.7, "0", fontsize=5.6, ha="center", zorder=9)
    ax.text(bx + L, by + h * 1.7, f"{uzunluk_km} km", fontsize=5.6, ha="center", zorder=9)

    nx_, ny_ = x0 + (x1 - x0) * 0.022, y0 + (y1 - y0) * 0.115
    ax.annotate("", xy=(nx_, ny_ + (y1 - y0) * 0.052), xytext=(nx_, ny_),
                arrowprops=dict(arrowstyle="-|>", color="#2b2b2b", lw=1.05,
                                mutation_scale=8), zorder=9)
    ax.text(nx_, ny_ + (y1 - y0) * 0.068, "K", fontsize=6.6, ha="center",
            fontweight="bold", zorder=9)


def kutu(ax, x, y, w, h, baslik=None, zorder=8, alfa=0.94):
    """Harita üstüne yerleşen açıklama kutusu (eksen oranı 0-1 ile)."""
    tx = ax.transAxes
    p = FancyBboxPatch((x, y), w, h, boxstyle="round,pad=0.006,rounding_size=0.008",
                       transform=tx, facecolor="white", edgecolor=GRI["kutu_cizgi"],
                       lw=0.7, zorder=zorder, alpha=alfa)
    ax.add_patch(p)
    if baslik:
        ax.text(x + 0.012, y + h - 0.028, baslik, transform=tx, fontsize=7.0,
                fontweight="bold", color=GRI["metin"], zorder=zorder + 0.1, va="center")
    return p
