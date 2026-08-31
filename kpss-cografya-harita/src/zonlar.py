# -*- coding: utf-8 -*-
"""İklim / bitki örtüsü / toprak kuşaklarını GERÇEK GEOMETRİDEN üretir.

Yöntem: Türkiye'nin sınır çizgisinin hangi parçasının hangi denize baktığı
belirlenir (kara sınırı komşu ülkelere olan uzaklıkla ayıklanır), sonra her
deniz için kıyıdan içeri doğru tamponlar alınıp Türkiye ile kesiştirilir.
Böylece kuşak sınırları il sınırlarına değil, GERÇEK KIYI ÇİZGİSİNE oturur.
Yükseltiye bağlı kuşaklar (dağ çayırı vb.) doğrudan DEM'den çizilir.
"""
from __future__ import annotations

import numpy as np
from shapely.geometry import LineString, MultiLineString, Point
from shapely.ops import unary_union, linemerge

import harita_temel as H

_C: dict = {}

# Türkiye kıyılarının deniz bazında ayrım kuralları — SIRA ÖNEMLİDİR.
# Marmara önce ayıklanır, çünkü kuzey kıyısı (Tekirdağ–İstanbul, ~41,0° K)
# Karadeniz eşiğiyle çakışır. Karadeniz eşiği 40,85°'dir: Ordu (40,98°) ve
# Trabzon (41,00°) gibi kıyılar tam 41,0° sınırında olduğu için daha yukarıda
# bir eşik bu kıyıların Akdeniz'e düşmesine yol açıyordu.
def _deniz_sinifi(lon, lat):
    if 26.55 <= lon <= 30.30 and 40.15 <= lat <= 41.10:
        return "Marmara"
    if lat >= 40.85 and lon >= 26.60:
        return "Karadeniz"
    if lon <= 28.40 and lat >= 36.60:
        return "Ege"
    return "Akdeniz"


def _deniz_kiyisi_serit(tol_m=9000):
    """GERÇEK kıyı çizgisinin (Natural Earth 10m coastline) tampon şeridi.

    Bir sınır verteksi 'kıyı' sayılır ancak ve ancak gerçek kıyı çizgisine
    `tol_m` kadar yakınsa. Komşu ülke poligonlarından kara sınırı çıkarmaya
    çalışmak kırılgandır (poligonlar birebir çakışmaz); bu test doğrudandır.
    """
    if "kiyi_serit" not in _C:
        import geopandas as gpd
        import os
        g = gpd.read_file(f"zip://{os.path.join(H.VERI, 'ne_10m_coastline.zip')}")
        g = g.cx[24:48, 33:45].to_crs(H.LCC)
        _C["kiyi_serit"] = g.geometry.union_all().buffer(tol_m)
    return _C["kiyi_serit"]


def kiyilar() -> dict:
    """{deniz adı: MultiLineString} — Türkiye'nin gerçek kıyı çizgileri (LCC)."""
    if "kiyi" in _C:
        return _C["kiyi"]
    import pyproj
    tr = H.turkiye()
    deniz = _deniz_kiyisi_serit()
    inv = pyproj.Transformer.from_crs(H.LCC, "EPSG:4326", always_xy=True)

    parcalar: dict[str, list] = {}
    halkalar = []
    for g in (tr.geoms if tr.geom_type == "MultiPolygon" else [tr]):
        halkalar.append(g.exterior)
    for ring in halkalar:
        pts = list(ring.coords)
        akt_ad, akt = None, []
        for x, y in pts:
            p = Point(x, y)
            if not deniz.contains(p):      # gerçek kıyı çizgisine uzak = kara sınırı
                ad = None
            else:
                lo, la = inv.transform(x, y)
                ad = _deniz_sinifi(lo, la)
            if ad != akt_ad:
                if akt_ad and len(akt) > 1:
                    parcalar.setdefault(akt_ad, []).append(LineString(akt))
                akt_ad, akt = ad, ([ (x, y) ] if ad else [])
            elif ad:
                akt.append((x, y))
        if akt_ad and len(akt) > 1:
            parcalar.setdefault(akt_ad, []).append(LineString(akt))

    _C["kiyi"] = {k: linemerge(MultiLineString(v)) if len(v) > 1 else v[0]
                  for k, v in parcalar.items()}
    return _C["kiyi"]


def kiyi_kusagi(deniz: str, km: float):
    """Bir denizin kıyısından `km` içeriye uzanan Türkiye parçası."""
    k = kiyilar().get(deniz)
    if k is None:
        return None
    return k.buffer(km * 1000).intersection(H.turkiye()).buffer(0)


def temizle(g, tol=400):
    """Birleşim sonrası kalan kılcal boşlukları/iç halkaları kapatır."""
    from shapely.geometry import MultiPolygon, Polygon
    if g is None or g.is_empty:
        return g
    g = g.buffer(tol).buffer(-tol).buffer(0)
    pars = list(g.geoms) if g.geom_type == "MultiPolygon" else [g]
    pars = [Polygon(p.exterior) for p in pars if p.area > 4e6]   # >4 km²
    if not pars:
        return g
    return MultiPolygon(pars) if len(pars) > 1 else pars[0]


def _oncelikli(katmanlar):
    """[(ad, geom)] listesini üst üste binmeyecek şekilde kırpar."""
    out, kullanilan = [], None
    for ad, g in katmanlar:
        if g is None or g.is_empty:
            continue
        if kullanilan is not None:
            g = g.difference(kullanilan).buffer(0)
        if not g.is_empty:
            out.append((ad, g))
            kullanilan = g if kullanilan is None else unary_union([kullanilan, g])
    return out, kullanilan


# ------------------------------------------------------------ İKLİM
def iklim_kusaklari():
    """KPSS sınıflandırmasına göre iklim kuşakları (gerçek kıyı tamponlarından)."""
    if "iklim" in _C:
        return _C["iklim"]
    kat = [
        ("Karadeniz iklimi",           kiyi_kusagi("Karadeniz", 80)),
        ("Marmara (geçiş) iklimi",     kiyi_kusagi("Marmara", 70)),
        ("Akdeniz iklimi (Ege kıyısı)",kiyi_kusagi("Ege", 95)),
        ("Akdeniz iklimi",             kiyi_kusagi("Akdeniz", 90)),
    ]
    yerlesik, kullanilan = _oncelikli(kat)
    karasal = H.turkiye().difference(kullanilan).buffer(0)

    # Karasalın alt tipleri — boylam/enlem eşiğiyle değil, il gruplarıyla ayrılır
    import veri_idari as V
    il = H.iller()
    def grup(iller):
        return unary_union([il.loc[i].geometry for i in iller if i in il.index]).buffer(0)
    dogu = grup(V._B["Doğu Anadolu"])
    gda  = grup(V._B["Güneydoğu Anadolu"])
    sonuc = yerlesik + [
        ("Sert karasal iklim (D. Anadolu)", temizle(karasal.intersection(dogu))),
        ("Karasal-Akdeniz geçişi (G.Doğu)",
         temizle(karasal.intersection(gda).difference(dogu))),
    ]
    sonuc.append(("Karasal iklim (İç Anadolu)",
                  temizle(karasal.difference(unary_union([dogu, gda])))))
    _C["iklim"] = sonuc
    return sonuc


# ------------------------------------------------------- BİTKİ ÖRTÜSÜ
def bitki_kusaklari():
    if "bitki" in _C:
        return _C["bitki"]
    kat = [
        ("Maki (Akdeniz–Ege kıyı kuşağı)",
         unary_union([g for g in [kiyi_kusagi("Ege", 45), kiyi_kusagi("Akdeniz", 40)] if g]).buffer(0)),
        ("Kızılçam ormanı (maki üstü)",
         unary_union([g for g in [kiyi_kusagi("Ege", 100), kiyi_kusagi("Akdeniz", 95)] if g]).buffer(0)),
        ("Nemli orman (Karadeniz)", kiyi_kusagi("Karadeniz", 80)),
        ("Kuru orman / psödomaki (Marmara)", kiyi_kusagi("Marmara", 70)),
    ]
    yerlesik, kullanilan = _oncelikli(kat)
    yerlesik.append(("Bozkır (step)", temizle(H.turkiye().difference(kullanilan))))
    _C["bitki"] = yerlesik
    return yerlesik


# ------------------------------------------------------------ TOPRAK
def toprak_kusaklari():
    if "toprak" in _C:
        return _C["toprak"]
    import veri_idari as V
    il = H.iller()
    def grup(iller):
        return unary_union([il.loc[i].geometry for i in iller if i in il.index]).buffer(0)
    kat = [
        ("Terra rossa (kırmızı Akdeniz t.)",
         unary_union([g for g in [kiyi_kusagi("Akdeniz", 55), kiyi_kusagi("Ege", 50)] if g]).buffer(0)),
        ("Kahverengi orman toprağı",
         unary_union([g for g in [kiyi_kusagi("Karadeniz", 80), kiyi_kusagi("Marmara", 55)] if g]).buffer(0)),
        ("Çernozyom (kara toprak)", temizle(grup(["Erzurum", "Kars", "Ardahan"]))),
    ]
    yerlesik, kullanilan = _oncelikli(kat)
    yerlesik.append(("Kestane rengi / kahverengi bozkır toprağı",
                     temizle(H.turkiye().difference(kullanilan))))
    _C["toprak"] = yerlesik
    return yerlesik


# --------------------------------------------- YÜKSELTİYE BAĞLI KUŞAK
def yukseklik_maskesi(ax, esik=2000, hatch=None, renk="#9a9a9a", lw=0.0, alpha=0.55,
                      zorder=4.2):
    """DEM'den `esik` metre üzerini doğrudan çizer (dağ çayırı vb.)."""
    elev, ext, pm = H.dem_lcc(620)
    # hafif yumuşatma: eş yükselti eğrisinin binlerce kırık parçaya bölünmesini önler
    k = np.ones((3, 3), dtype=np.float32) / 9.0
    z0 = np.nan_to_num(elev, nan=-1000.0)
    pad = np.pad(z0, 1, mode="edge")
    elev = sum(pad[i:i + z0.shape[0], j:j + z0.shape[1]] * k[i, j]
               for i in range(3) for j in range(3))
    ny, nx = elev.shape
    X = np.linspace(ext[0], ext[1], nx)
    Y = np.linspace(ext[3], ext[2], ny)
    z = np.nan_to_num(elev, nan=-1000.0)
    kw = {"hatches": [hatch]} if hatch else {}
    cs = ax.contourf(X, Y, z, levels=[esik, 10000], colors=[renk],
                     zorder=zorder, alpha=alpha, **kw)
    # dağ çayırı yalnız Türkiye içinde gösterilir
    from matplotlib.path import Path
    from matplotlib.patches import PathPatch
    tr = H.turkiye()
    yollar = [Path(np.array(g.exterior.coords))
              for g in (tr.geoms if tr.geom_type == "MultiPolygon" else [tr])]
    yama = PathPatch(Path.make_compound_path(*yollar), transform=ax.transData,
                     facecolor="none", lw=0)
    ax.add_patch(yama)
    cs.set_clip_path(yama)
    return cs
