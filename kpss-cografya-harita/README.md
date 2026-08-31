# KPSS Lisans — Türkiye Coğrafyası Harita Portföyü

Baskıya hazır, siyah-beyaz yazıcıya göre optimize edilmiş **61 sayfalık** harita seti.
29 konunun her biri iki sayfadır: **etiketli cevap anahtarı** + aynı numaralandırmaya
sahip **dilsiz çalışma haritası**.

**Çıktı:** [`cikti/KPSS-Cografya-Harita-Portfoyu.pdf`](cikti/KPSS-Cografya-Harita-Portfoyu.pdf)
— tek dosya, 61 sayfa, A4 yatay (841,89 × 595,28 pt), gerçek gri tonlama, ~13 MB.
Hepsi tek seferde basılacak şekilde hazırlanmıştır: tüm sayfalar aynı boyutta ve
yönde olduğu için yazıcıda tek kâğıt ayarı yeterlidir.

## Neden bu haritalara güvenebilirsiniz

Hiçbir harita elle çizilmedi; hepsi gerçek coğrafi veriden üretildi ve her veri
bağımsız bir kaynakla çapraz doğrulandı:

| Katman | Kaynak | Doğrulama |
|---|---|---|
| İl sınırları (81 il) | Resmî sınır verisi (GeoJSON) | Toplam yüzölçümü **780.263 km²** — resmî 783.562 km² ile %0,4 fark |
| Yükselti / kabartma | AWS Terrain Tiles (SRTM/ASTER türevi), z9 ≈ 300 m | Veri maksimumu **5.126 m** = Ağrı Dağı (gerçek 5.137 m) |
| Kıyı · göl · akarsu | Natural Earth 10m (küresel + Avrupa) | 17 kıyı kentinin doğru denize atandığı test edildi |
| İklim | WorldClim v2.1, 1970–2000 normalleri, ~4,6 km | Türkiye ortalama yağışı **594 mm** (gerçek ~574 mm) |
| Nüfus | TÜİK ADNKS 2025 | İstanbul yoğunluğu 3.310 kişi/km², ülke ortalaması 110 |

**Zirve koordinatları yükselti verisinden bulundu**, tahmin edilmedi: her zirve için
bölgesel maksimum arandı, sonuç resmî yükseklikle ve zirvenin bulunduğu il poligonuyla
karşılaştırıldı. Bu yöntem üç hatalı koordinatı (Ilgaz, Karacadağ, Bingöl Dağı) yakaladı.

### Doğrulama betiği

```bash
python3 src/dogrula.py      # 107 denetim
```

İdari tutarlılık, geometri, uç noktalar, zirve koordinatları, 386 tesis/yer noktasının
sınır içinde oluşu, ürün–il eşlemeleri, kıyı sınıflandırması, kuşak kapsaması ve iklim
verisinin istasyon normalleriyle uyumunu test eder.

## Üretim

```bash
pip install numpy matplotlib shapely pyproj pyogrio geopandas Pillow
apt-get install ghostscript   # baskı geçişi için (yoksa adım atlanır)

python3 src/uret.py           # sadece PDF
python3 src/uret.py --png     # PDF + sayfa PNG'leri
```

Üretimin son adımı **baskı geçişidir**: matplotlib rasterleri DeviceRGB olarak gömer,
oysa haritalar zaten gri tonludur. Ghostscript geçişi renk uzayını DeviceGray'e çevirip
sürekli tonlu kabartmayı DCT ile sıkıştırır — metin ve çizgiler vektör kalır, sayfa
boyutu değişmez. Sonuç: 29 MB → 13 MB. Ghostscript kurulu değilse adım atlanır ve
sıkıştırılmamış dosya bırakılır.

`veri/` klasöründeki kaynak dosyalar depoya dâhil edilmemiştir (bkz. `veri/INDIR.md`).

## Dosyalar

| Dosya | İçerik |
|---|---|
| `src/harita_temel.py` | Projeksiyon (Lambert konik), sayfa düzeni, DEM warp, kabartma |
| `src/zonlar.py` | İklim/bitki/toprak kuşaklarını gerçek kıyı çizgisinden üretir |
| `src/cizim.py` | Çizim yardımcıları: numaralı semboller, çakışma önleyici etiketleme, ızgara |
| `src/veri_idari.py` | 81 il, plaka, nüfus, 7 bölge, 21 bölüm, uç noktalar, sınır kapıları |
| `src/veri_fiziki.py` | Zirveler, sıradağlar, ovalar, platolar, göller, barajlar, faylar |
| `src/veri_ekonomi.py` | Tarım, hayvancılık, madenler, enerji, sanayi, ulaşım, turizm |
| `src/h_*.py` | Harita çizim modülleri (konum, fiziki, iklim, beşeri, ekonomi) |
| `src/uret.py` | Portföyü birleştirir |
| `src/dogrula.py` | Doğrulama paketi |

## Bilinçli tercihler ve sınırlar

- **Coğrafi bölge / bölüm haritaları il bazlı şematiktir.** Gerçek 1941 bölge sınırları
  il sınırlarını keser; bu yüzden taralı alanların oranları resmî yüzölçümü paylarıyla
  birebir aynı değildir. Bu, ilgili sayfalarda ayrıca not edilmiştir.
- **İklim/bitki/toprak kuşakları gerçek kıyı çizgisinden tampon alınarak** üretilmiştir
  (il sınırlarına bağlı değildir); geçişler doğada kademelidir, haritadaki çizgiler
  keskin görünse de sınır bir şerittir.
- **WorldClim ızgara ortalamasıdır**: Rize (~2.300 mm) ve Antalya gibi yerel uç değerler
  istasyon ölçümlerinden daha yumuşak görünür. Uç değerler ilgili sayfada ayrıca yazılıdır.
- **Ürün sıralamaları yıllara göre değişir.** ★ işareti KPSS'de klasik olarak 1. sırada
  sorulan ili gösterir; harita üzerindeki koyu iller başlıca üretim alanlarıdır.
- **UNESCO listesi 2023 sonu itibarıyladır** (21 varlık).
