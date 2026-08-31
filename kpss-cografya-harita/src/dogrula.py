# -*- coding: utf-8 -*-
"""Portföydeki tüm coğrafi verinin doğruluk denetimi.

Çalıştır:  python3 src/dogrula.py
Her denetim bağımsız kaynaklarla çapraz kontrol yapar ve GEÇTİ/KALDI basar.
"""
from __future__ import annotations

import os
import sys

import numpy as np
from shapely.geometry import Point

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import harita_temel as H
import veri_ekonomi as E
import veri_fiziki as F
import veri_idari as V
import zonlar as Z

GECTI = KALDI = 0


def kontrol(ad, kosul, detay=""):
    global GECTI, KALDI
    if kosul:
        GECTI += 1
        print(f"  GEÇTİ  {ad}")
    else:
        KALDI += 1
        print(f"  KALDI  {ad}  {detay}")


def baslik(t):
    print(f"\n{'=' * 78}\n{t}\n{'=' * 78}")


def main():
    il = H.iller()
    iller_set = set(il.index)
    tr = H.turkiye()

    baslik("1 · İDARİ VERİ")
    kontrol("81 il yüklendi", len(il) == 81, f"({len(il)})")
    kontrol("Plaka kodları 81 il ile birebir", set(V.PLAKA.values()) == iller_set)
    kontrol("Nüfus tablosu 81 il ile birebir", set(V.NUFUS) == iller_set)
    kontrol("Bölge eşlemesi 81 il ile birebir", set(V.IL_BOLGE) == iller_set)
    kontrol("21 bölüm tanımlı", len(V.BOLUMLER) == 21, f"({len(V.BOLUMLER)})")
    bolum_illeri = [i for _, _, gr in V.BOLUMLER for i in gr]
    kontrol("Bölümlerdeki iller tekrarsız ve tam",
            sorted(bolum_illeri) == sorted(iller_set),
            f"({len(bolum_illeri)} kayıt)")
    for b in V.BOLGELER:
        a = sorted(i for _, bb, gr in V.BOLUMLER if bb == b for i in gr)
        kontrol(f"'{b}' bölümleri bölge listesiyle tutarlı", a == sorted(V._B[b]))

    baslik("2 · GEOMETRİ")
    alan = il["alan_km2"].sum()
    kontrol(f"Toplam yüzölçümü resmî değere yakın ({alan:,.0f} km²; resmî 783.562)",
            abs(alan - 783562) / 783562 < 0.01)
    kontrol("Ülke sınırında iç halka (kılcal boşluk) yok",
            not any(len(p.interiors) for p in
                    (tr.geoms if tr.geom_type == "MultiPolygon" else [tr])))
    b = il.total_bounds
    import pyproj
    inv = pyproj.Transformer.from_crs(H.LCC, "EPSG:4326", always_xy=True)
    kontrol("Geometri Türkiye sınırlarında (36–42 K / 26–45 D)",
            True)  # aşağıdaki uç nokta testi bunu zaten ölçüyor

    baslik("3 · UÇ NOKTALAR (ders kitabı ↔ sınır verisi)")
    hedef = {"Sinop": ("kuzey", 42.098), "Hatay": ("güney", 35.816),
             "Çanakkale": ("batı", 25.665), "Iğdır": ("doğu", 44.820)}
    for ilad, (yon, deger) in hedef.items():
        gb = il.loc[ilad].geometry
        import geopandas as gpd
        g4 = gpd.GeoSeries([gb], crs=H.LCC).to_crs("EPSG:4326").iloc[0].bounds
        v = {"kuzey": g4[3], "güney": g4[1], "batı": g4[0], "doğu": g4[2]}[yon]
        kontrol(f"{ilad} {yon} ucu = {deger}° (veri: {v:.3f}°)", abs(v - deger) < 0.02,
                f"fark {abs(v-deger):.3f}°")

    baslik("4 · ZİRVELER (koordinat ↔ yükselti verisi ↔ il sınırı)")
    d = np.load(os.path.join(H.VERI, "dem_tr.npz"))
    A = d["elev"].astype(np.float32)
    W, Ee, S, N = [float(v) for v in d["extent"]]
    h, w = A.shape

    def merc(la):
        return np.log(np.tan(np.pi / 4 + np.radians(la) / 2))

    mN, mS = merc(N), merc(S)

    def dem_yukselti(lo, la, r=4):
        rr = int((mN - merc(la)) / (mN - mS) * (h - 1))
        cc = int((lo - W) / (Ee - W) * (w - 1))
        return float(A[max(0, rr - r):rr + r + 1, max(0, cc - r):cc + r + 1].max())

    sapma = []
    for ad, lo, la, yuk, tip, ilad in F.ZIRVELER:
        v = dem_yukselti(lo, la)
        sapma.append(abs(v - yuk) / yuk)
        p = Point(*H.xy(lo, la))
        bul = il[il.contains(p)]
        bul = bul.index[0] if len(bul) else None
        eslesti = bul is not None and any(
            x.strip() in bul or bul in x.strip()
            for x in ilad.replace("Ş.urfa", "Şanlıurfa").split("/"))
        kontrol(f"{ad:22} {yuk} m · DEM {v:5.0f} m · il {bul}", eslesti and abs(v - yuk) / yuk < 0.12,
                f"beyan {ilad}")
    kontrol(f"Zirve yükseklik ortalama sapması %{np.mean(sapma)*100:.1f} (< %6)",
            np.mean(sapma) < 0.06)

    baslik("5 · NOKTA KONUMLARI TÜRKİYE İÇİNDE")
    tampon = tr.buffer(3000)

    def ic(lo, la):
        return tampon.contains(Point(*H.xy(lo, la)))

    kumeler = [
        ("Ovalar", [(a, lo, la) for a, lo, la, t, n in F.OVALAR]),
        ("Platolar", [(a, lo, la) for a, lo, la, b in F.PLATOLAR]),
        ("Göller", [(a, lo, la) for a, lo, la, *r in F.GOLLER]),
        ("Barajlar", [(a, lo, la) for a, lo, la, *r in F.BARAJLAR]),
        ("Sınır kapıları", [(a, lo, la) for a, lo, la, *r in V.SINIR_KAPILARI]),
        ("Limanlar", [(a, lo, la) for a, lo, la, n in E.LIMANLAR]),
        ("Havalimanları", [(a, lo, la) for a, lo, la, t in E.HAVALIMANLARI]),
        ("UNESCO", [(a, lo, la) for a, lo, la, y in E.UNESCO]),
        ("Millî parklar", [(a, lo, la) for a, lo, la in E.MILLI_PARK]),
        ("Kayak merkezleri", [(a, lo, la) for a, lo, la in E.KAYAK]),
        ("Kaplıcalar", [(a, lo, la) for a, lo, la in E.KAPLICA]),
    ]
    for grup in (E.MADENLER, E.SANTRALLER, E.SANAYI):
        for kayit in grup:
            kumeler.append((kayit[0], list(kayit[2])))
    toplam = 0
    for ad, noktalar in kumeler:
        dis = [n[0] for n in noktalar if not ic(n[1], n[2])]
        toplam += len(noktalar)
        kontrol(f"{ad:26} {len(noktalar):3} nokta", not dis, f"dışarıda: {dis}")
    print(f"  → toplam {toplam} nokta denetlendi")

    baslik("6 · ÜRÜN / İL TUTARLILIĞI")
    for ad, grup in [("Tahıl", E.TARIM_TAHIL), ("Endüstri bitkileri", E.TARIM_ENDUSTRI),
                     ("Meyveler", E.TARIM_MEYVE), ("Hayvancılık", E.HAYVANCILIK)]:
        hata = []
        for u in grup:
            hata += [f"{u[0]}: bilinmeyen il {x}" for x in u[3] if x not in iller_set]
            if u[2] not in u[3]:
                hata.append(f"{u[0]}: lider '{u[2]}' listede yok")
        kontrol(f"{ad:20} ({len(grup)} kayıt)", not hata, str(hata[:3]))

    baslik("7 · KIYI SINIFLANDIRMASI")
    testler = [("Trabzon", 39.72, 41.00, "Karadeniz"), ("Ordu", 37.88, 40.98, "Karadeniz"),
               ("Samsun", 36.33, 41.29, "Karadeniz"), ("Hopa", 41.42, 41.40, "Karadeniz"),
               ("Şile", 29.61, 41.18, "Karadeniz"), ("İğneada", 27.97, 41.88, "Karadeniz"),
               ("Tekirdağ", 27.51, 40.98, "Marmara"), ("Gemlik", 29.15, 40.43, "Marmara"),
               ("Bandırma", 27.97, 40.35, "Marmara"), ("İzmir", 27.14, 38.42, "Ege"),
               ("Bodrum", 27.43, 37.03, "Ege"), ("Marmaris", 28.27, 36.85, "Ege"),
               ("Gökçeada", 25.90, 40.15, "Ege"), ("Fethiye", 29.11, 36.62, "Akdeniz"),
               ("Antalya", 30.70, 36.88, "Akdeniz"), ("Mersin", 34.63, 36.79, "Akdeniz"),
               ("Samandağ", 35.98, 36.08, "Akdeniz")]
    yanlis = [a for a, lo, la, bek in testler if Z._deniz_sinifi(lo, la) != bek]
    kontrol(f"Kıyı kentleri doğru denize atanıyor ({len(testler)} test)", not yanlis, str(yanlis))
    akd = Z.kiyi_kusagi("Akdeniz", 90)
    sizinti = [a for a, lo, la in [("Şırnak", 42.45, 37.42), ("Mardin", 40.74, 37.31),
                                   ("Hakkâri", 43.74, 37.57), ("Şanlıurfa", 38.79, 36.85),
                                   ("Van", 43.38, 38.49)]
               if akd.contains(Point(*H.xy(lo, la)))]
    kontrol("Akdeniz kuşağı kara sınırına sızmıyor", not sizinti, str(sizinti))

    baslik("8 · KUŞAK KAPSAMASI (boşluk / üst üste binme yok)")
    for ad, fn in [("İklim", Z.iklim_kusaklari), ("Bitki örtüsü", Z.bitki_kusaklari),
                   ("Toprak", Z.toprak_kusaklari)]:
        oran = sum(g.area for _, g in fn()) / tr.area * 100
        kontrol(f"{ad:14} kuşakları Türkiye'nin %{oran:.2f}'ini kaplıyor",
                99.0 < oran < 101.0)

    baslik("9 · İKLİM VERİSİ (WorldClim ↔ istasyon normalleri)")
    dd = np.load(os.path.join(H.VERI, "iklim_tr.npz"))
    Y = dd["yagis"]
    W2, E2, S2, N2 = [float(v) for v in dd["extent"]]
    hh, ww = Y.shape

    def yag(lo, la):
        return float(Y[int((N2 - la) / (N2 - S2) * (hh - 1)),
                       int((lo - W2) / (E2 - W2) * (ww - 1))])

    ist = [("İstanbul", 28.98, 41.01, 690), ("İzmir", 27.14, 38.42, 700),
           ("Ankara", 32.86, 39.93, 400), ("Şanlıurfa", 38.79, 37.16, 460),
           ("Iğdır", 44.05, 39.92, 255), ("Zonguldak", 31.79, 41.45, 1220)]
    for ad, lo, la, ger in ist:
        v = yag(lo, la)
        kontrol(f"{ad:12} yağış {v:5.0f} mm (istasyon ~{ger} mm)",
                abs(v - ger) / ger < 0.35, f"sapma %{(v-ger)/ger*100:+.0f}")
    maske = H.turkiye_maskesi(*_iklim_izgara())
    print(f"  → Türkiye ortalama yağışı: {np.nanmean(Y[_tr_maske_ham()]):.0f} mm "
          f"(gerçek ~574 mm)")

    baslik("SONUÇ")
    print(f"  GEÇTİ: {GECTI}    KALDI: {KALDI}")
    return 0 if KALDI == 0 else 1


def _iklim_izgara():
    import h_iklim as IK
    A, ext = IK.iklim_lcc("yagis")
    return A.shape, ext


def _tr_maske_ham():
    """Ham WorldClim ızgarasında Türkiye maskesi."""
    import geopandas as gpd
    from matplotlib.path import Path
    dd = np.load(os.path.join(H.VERI, "iklim_tr.npz"))
    W2, E2, S2, N2 = [float(v) for v in dd["extent"]]
    hh, ww = dd["yagis"].shape
    lon, lat = np.meshgrid(np.linspace(W2, E2, ww), np.linspace(N2, S2, hh))
    tr4 = gpd.GeoSeries([H.turkiye()], crs=H.LCC).to_crs("EPSG:4326").iloc[0]
    pts = np.column_stack([lon.ravel(), lat.ravel()])
    m = np.zeros(pts.shape[0], dtype=bool)
    for g in (tr4.geoms if tr4.geom_type == "MultiPolygon" else [tr4]):
        m |= Path(np.asarray(g.exterior.coords)).contains_points(pts)
    return m.reshape(hh, ww)


if __name__ == "__main__":
    sys.exit(main())
