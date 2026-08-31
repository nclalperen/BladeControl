# -*- coding: utf-8 -*-
"""Önizleme sayfasını üretir: sayfa PNG'lerinden JPEG küçük görseller çıkarır ve
hepsini gömülü olarak taşıyan tek dosyalık bir HTML yazar.

Önce `python3 src/uret.py --png` çalıştırılmış olmalıdır.

    python3 src/hazirla_onizleme.py

Çıktı:  cikti/jpg/*.jpg  ve  cikti/kpss-harita-onizleme.html
"""
import base64
import glob
import json
import os
import sys

from PIL import Image

KOK = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(KOK, "src"))
import uret  # noqa: E402

PNG = os.path.join(KOK, "cikti", "png")
JPG = os.path.join(KOK, "cikti", "jpg")
OUT = os.path.join(KOK, "cikti", "kpss-harita-onizleme.html")
GENISLIK, KALITE = 1000, 74


def kucult():
    """Sayfa PNG'lerini gömmeye uygun JPEG'lere indirger."""
    os.makedirs(JPG, exist_ok=True)
    toplam = 0
    for p in sorted(glob.glob(os.path.join(PNG, "*.png"))):
        hedef = os.path.join(JPG, os.path.basename(p).replace(".png", ".jpg"))
        im = Image.open(p).convert("RGB")
        im.thumbnail((GENISLIK, GENISLIK), Image.LANCZOS)
        im.save(hedef, "JPEG", quality=KALITE, optimize=True)
        toplam += os.path.getsize(hedef)
    print(f"{len(glob.glob(os.path.join(JPG, '*.jpg')))} küçük görsel · "
          f"{toplam / 1e6:.2f} MB (base64 ~{toplam * 1.34 / 1e6:.2f} MB)")


kucult()

def b64(p):
    return 'data:image/jpeg;base64,' + base64.b64encode(open(p,'rb').read()).decode()

# --- sayfa listesi
sayfalar = []
on = [('00-kapak.jpg','Kapak','—','on'),
      ('01-icindekiler.jpg','İçindekiler','—','on'),
      ('02-nasil-calisilir.jpg','Nasıl Çalışılır + Konu Dağılımı','—','on')]
for i,(dosya,baslik,bolum,tip) in enumerate(on, 0):
    sayfalar.append(dict(no=i, dosya=dosya, baslik=baslik, bolum='Ön Sayfalar', mod='on', konu=0))

sno = 3
for idx,(bolum, baslik, alt, fn, duzen) in enumerate(uret.HARITALAR, 1):
    for mod in ('dolu','bos'):
        ad = f'{sno:02d}-{idx:02d}-{mod}.jpg'
        sayfalar.append(dict(no=sno, dosya=ad, baslik=baslik, bolum=bolum, mod=mod, konu=idx, alt=alt))
        sno += 1

eksik = [s['dosya'] for s in sayfalar if not os.path.exists(os.path.join(JPG, s['dosya']))]
assert not eksik, f'eksik: {eksik}'

for s in sayfalar:
    s['src'] = b64(os.path.join(JPG, s['dosya']))

bolumler = []
for s in sayfalar:
    if s['bolum'] not in bolumler: bolumler.append(s['bolum'])

PLAN = [
 ("1. gün","Konum · komşular · iller · bölgeler · bölümler","Bölge sınırlarını ve hangi ilin hangi bölgede olduğunu oturt."),
 ("2. gün","Dağlar · ovalar · platolar","Dağ sıralarının yönü ile iklim arasındaki bağı kur."),
 ("3. gün","Akarsular · göller · barajlar · faylar","Göllerin oluşum tipi ve akarsuların döküldüğü deniz kritik."),
 ("4. gün","İklim · yağış · sıcaklık · bitki · toprak","En çok/az yağış ve sıcaklık uçlarını ezberle."),
 ("5. gün","Nüfus · göç · tarım I–II","Sık ve seyrek nüfusun nedenleri sorulur."),
 ("6. gün","Tarım III · hayvancılık · su ürünleri · madenler","Ürün–iklim ve maden–yer eşleşmelerini çalış."),
 ("7. gün","Enerji · sanayi · ulaşım · turizm + tüm dilsiz sayfalar","Son gün yalnızca dilsiz haritaları baştan sona doldur."),
]

DOGRULAMA = [
 ("81 il", "780.263 km²", "hesaplanan toplam yüzölçümü — resmî 783.562 km² ile %0,4 fark"),
 ("Yükselti verisi", "5.126 m", "veri maksimumu = Ağrı Dağı; gerçek zirve 5.137 m"),
 ("İklim verisi", "594 mm", "Türkiye ortalama yağışı — gerçek ~574 mm"),
 ("Nokta konumu", "386", "tesis, liman, maden ve yerin tamamı sınır içinde doğrulandı"),
 ("Kıyı sınıflandırması", "17/17", "kıyı kenti doğru denize atandı"),
 ("Toplam denetim", "107/107", "dogrula.py testlerinin tamamı geçiyor"),
]

kartlar = []
for s in sayfalar:
    rozet = {'dolu':'Cevap anahtarı','bos':'Dilsiz','on':'Ön sayfa'}[s['mod']]
    kartlar.append(f'''<button class="kart" data-mod="{s['mod']}" data-no="{s['no']}" data-bolum="{s['bolum']}" data-ara="{(s['baslik']+' '+s['bolum']).lower()}">
<span class="kart-gorsel"><img src="{s['src']}" alt="{s['baslik']} — sayfa {s['no']}" loading="lazy"></span>
<span class="kart-alt"><span class="kart-no">{s['no']:02d}</span><span class="kart-ad">{s['baslik']}</span><span class="rozet r-{s['mod']}">{rozet}</span></span>
<span class="isaret" aria-hidden="true"></span></button>''')

plan_html = ''.join(
    f'<li><span class="gun">{g}</span><span class="plan-konu">{k}</span><span class="plan-not">{n}</span></li>'
    for g,k,n in PLAN)
dog_html = ''.join(
    f'<div class="dog"><dt>{a}</dt><dd class="dog-deger">{b}</dd><dd class="dog-not">{c}</dd></div>'
    for a,b,c in DOGRULAMA)
filtre_html = ''.join(f'<button class="cip" data-f="bolum" data-v="{b}">{b}</button>' for b in bolumler)

HTML = f'''<title>KPSS Coğrafya Harita Portföyü</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Archivo:wght@500;600;700&family=IBM+Plex+Mono:wght@400;500&family=Source+Serif+4:opsz,wght@8..60,400;8..60,600&display=swap">
<style>
:root {{
  --paper:#f5f7f8; --card:#ffffff; --ink:#141a1f; --ink2:#3b464f; --muted:#6a7883;
  --line:#dde3e8; --line2:#eef2f5; --accent:#155e78; --accent-soft:#e2eef3;
  --chip-ink:#141a1f; --accent-on:#ffffff;
  --shadow:0 1px 2px rgba(20,26,31,.05), 0 8px 24px rgba(20,26,31,.06);
  --disp:'Archivo', 'Helvetica Neue', Arial, sans-serif;
  --body:'Source Serif 4', Georgia, 'Times New Roman', serif;
  --mono:'IBM Plex Mono', ui-monospace, 'SF Mono', Menlo, monospace;
}}
@media (prefers-color-scheme: dark) {{
  :root:not([data-theme="light"]) {{
    --paper:#0f1418; --card:#171d23; --ink:#e6ecf0; --ink2:#b2bec8; --muted:#7d8b96;
    --line:#262f38; --line2:#1e262d; --accent:#63b4d3; --accent-soft:#152e3a;
    --chip-ink:#e6ecf0; --accent-on:#0d1216;
    --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px rgba(0,0,0,.35);
  }}
}}
:root[data-theme="dark"] {{
  --paper:#0f1418; --card:#171d23; --ink:#e6ecf0; --ink2:#b2bec8; --muted:#7d8b96;
  --line:#262f38; --line2:#1e262d; --accent:#63b4d3; --accent-soft:#152e3a;
  --chip-ink:#e6ecf0; --accent-on:#0d1216;
    --shadow:0 1px 2px rgba(0,0,0,.4), 0 8px 24px rgba(0,0,0,.35);
}}
*, *::before, *::after {{ box-sizing:border-box; }}
body {{ background:var(--paper); color:var(--ink); font-family:var(--body);
  font-size:16px; line-height:1.6; -webkit-font-smoothing:antialiased; }}
.kap {{ max-width:1180px; margin:0 auto; padding:0 24px; }}

/* ---------- başlık ---------- */
header {{ border-bottom:1px solid var(--line); background:var(--card); }}
.hd {{ padding:44px 0 34px; display:grid; gap:22px; }}
.gozeyaz {{ font-family:var(--mono); font-size:11px; letter-spacing:.16em;
  text-transform:uppercase; color:var(--accent); }}
h1 {{ font-family:var(--disp); font-weight:700; font-size:clamp(30px,4.6vw,46px);
  line-height:1.08; letter-spacing:-.018em; margin:0; text-wrap:balance; }}
.ozet {{ font-size:17px; color:var(--ink2); max-width:62ch; margin:0; }}
.meta {{ display:flex; flex-wrap:wrap; gap:0; border-top:1px solid var(--line);
  border-bottom:1px solid var(--line); }}
.meta div {{ padding:14px 22px 14px 0; margin-right:22px; border-right:1px solid var(--line2); }}
.meta div:last-child {{ border-right:0; }}
.meta dt {{ font-family:var(--mono); font-size:10.5px; letter-spacing:.12em;
  text-transform:uppercase; color:var(--muted); }}
.meta dd {{ margin:2px 0 0; font-family:var(--disp); font-weight:600; font-size:19px;
  font-variant-numeric:tabular-nums; }}
.uyari {{ display:flex; gap:12px; align-items:flex-start; padding:14px 16px;
  background:var(--accent-soft); border-left:3px solid var(--accent); border-radius:0 6px 6px 0;
  font-size:14.5px; color:var(--ink2); }}
.uyari b {{ color:var(--ink); font-family:var(--disp); font-weight:600; }}

/* ---------- plan ---------- */
.blok {{ padding:44px 0; border-bottom:1px solid var(--line); }}
h2 {{ font-family:var(--disp); font-weight:600; font-size:13px; letter-spacing:.13em;
  text-transform:uppercase; color:var(--muted); margin:0 0 20px; }}
.plan {{ list-style:none; margin:0; padding:0; display:grid; gap:0;
  border-top:1px solid var(--line); }}
.plan li {{ display:grid; grid-template-columns:76px minmax(0,1fr) minmax(0,1.15fr);
  gap:18px; align-items:baseline; padding:13px 0; border-bottom:1px solid var(--line2); }}
.gun {{ font-family:var(--mono); font-size:12px; color:var(--accent); font-weight:500; }}
.plan-konu {{ font-family:var(--disp); font-weight:500; font-size:15px; }}
.plan-not {{ font-size:14px; color:var(--muted); }}
@media (max-width:700px) {{ .plan li {{ grid-template-columns:64px minmax(0,1fr); }}
  .plan-not {{ grid-column:2; }} }}

/* ---------- araç çubuğu ---------- */
.arac {{ position:sticky; top:0; z-index:20; background:var(--paper);
  border-bottom:1px solid var(--line); padding:12px 0; }}
.arac-ic {{ display:flex; flex-wrap:wrap; gap:10px; align-items:center; }}
.cip {{ font-family:var(--disp); font-weight:500; font-size:12.5px; padding:6px 13px;
  border:1px solid var(--line); background:var(--card); color:var(--ink2);
  border-radius:999px; cursor:pointer; transition:.14s; }}
.cip:hover {{ border-color:var(--accent); color:var(--accent); }}
.cip[aria-pressed="true"] {{ background:var(--accent); border-color:var(--accent);
  color:var(--accent-on); }}
.ayir {{ width:1px; height:22px; background:var(--line); margin:0 4px; }}
.ilerleme {{ margin-left:auto; display:flex; align-items:center; gap:10px;
  font-family:var(--mono); font-size:12px; color:var(--muted); }}
.cubuk {{ width:96px; height:5px; background:var(--line); border-radius:3px; overflow:hidden; }}
.cubuk i {{ display:block; height:100%; width:0; background:var(--accent); transition:width .3s; }}

/* ---------- ızgara ---------- */
.grup {{ padding:34px 0 6px; }}
.grup h3 {{ font-family:var(--disp); font-weight:600; font-size:15px; margin:0 0 4px;
  letter-spacing:-.005em; }}
.grup p {{ margin:0 0 18px; font-size:13.5px; color:var(--muted); }}
.izgara {{ display:grid; grid-template-columns:repeat(auto-fill, minmax(268px,1fr));
  gap:20px; }}
.kart {{ display:flex; flex-direction:column; gap:0; padding:0; border:1px solid var(--line);
  background:var(--card); border-radius:8px; overflow:hidden; cursor:pointer; text-align:left;
  font:inherit; color:inherit; position:relative; transition:.16s; box-shadow:var(--shadow); }}
.kart:hover {{ border-color:var(--accent); transform:translateY(-2px); }}
.kart:focus-visible {{ outline:2px solid var(--accent); outline-offset:2px; }}
.kart-gorsel {{ display:block; background:#fff; border-bottom:1px solid var(--line2);
  aspect-ratio:297/210; }}
.kart-gorsel img {{ width:100%; height:100%; object-fit:contain; display:block; }}
.kart-alt {{ display:grid; grid-template-columns:auto minmax(0,1fr); gap:4px 10px;
  padding:11px 13px 12px; align-items:center; }}
.kart-no {{ font-family:var(--mono); font-size:11.5px; color:var(--muted);
  font-variant-numeric:tabular-nums; }}
.kart-ad {{ font-family:var(--disp); font-weight:500; font-size:13.5px; line-height:1.3;
  overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }}
.rozet {{ grid-column:2; justify-self:start; font-family:var(--mono); font-size:9.5px;
  letter-spacing:.09em; text-transform:uppercase; padding:2px 7px; border-radius:3px; }}
.r-dolu {{ background:var(--chip-ink); color:var(--card); }}
.r-bos {{ background:transparent; color:var(--muted); border:1px solid var(--line); }}
.r-on {{ background:var(--accent-soft); color:var(--accent); }}
.isaret {{ position:absolute; top:9px; right:9px; width:19px; height:19px; border-radius:50%;
  border:1.5px solid var(--line); background:var(--card); }}
.kart.bitti .isaret {{ background:var(--accent); border-color:var(--accent); }}
.kart.bitti .isaret::after {{ content:""; position:absolute; inset:0;
  background:no-repeat center/10px 8px url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 8'%3E%3Cpath d='M1 4l2.5 2.5L9 1' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E"); }}
.kart.bitti .kart-gorsel {{ opacity:.55; }}
.gizli {{ display:none !important; }}

/* ---------- doğrulama ---------- */
.dogrulama {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(240px,1fr)); gap:0;
  border-top:1px solid var(--line); }}
.dog {{ padding:18px 20px 18px 0; border-bottom:1px solid var(--line2);
  border-right:1px solid var(--line2); margin:0; }}
.dog dt {{ font-family:var(--mono); font-size:10.5px; letter-spacing:.11em;
  text-transform:uppercase; color:var(--muted); }}
.dog-deger {{ margin:5px 0 3px; font-family:var(--disp); font-weight:700; font-size:25px;
  color:var(--accent); font-variant-numeric:tabular-nums; letter-spacing:-.02em; }}
.dog-not {{ margin:0; font-size:13.5px; color:var(--ink2); }}
.kaynaklar {{ margin-top:26px; font-size:14px; color:var(--ink2); }}
.kaynaklar li {{ margin-bottom:5px; }}
.kaynaklar code {{ font-family:var(--mono); font-size:12.5px; background:var(--line2);
  padding:1px 5px; border-radius:3px; }}

footer {{ padding:32px 0 56px; font-size:13.5px; color:var(--muted); }}

/* ---------- büyütme ---------- */
.buyut {{ position:fixed; inset:0; z-index:60; background:rgba(10,14,18,.9);
  display:none; align-items:center; justify-content:center; padding:22px; }}
.buyut[open] {{ display:flex; }}
.buyut img {{ max-width:100%; max-height:calc(100vh - 118px); object-fit:contain;
  background:#fff; border-radius:4px; box-shadow:0 20px 60px rgba(0,0,0,.5); }}
.buyut-kutu {{ display:flex; flex-direction:column; gap:14px; align-items:center; }}
.buyut-bar {{ display:flex; align-items:center; gap:14px; color:#e9eef2;
  font-family:var(--disp); font-size:13.5px; }}
.buyut-bar .no {{ font-family:var(--mono); color:#9fb2bf; }}
.gez {{ background:rgba(255,255,255,.1); border:1px solid rgba(255,255,255,.22); color:#fff;
  width:34px; height:34px; border-radius:50%; cursor:pointer; font-size:16px; line-height:1;
  display:grid; place-items:center; }}
.gez:hover {{ background:rgba(255,255,255,.2); }}
.gez:focus-visible {{ outline:2px solid #fff; outline-offset:2px; }}
.kapat {{ position:absolute; top:16px; right:18px; }}
@media (prefers-reduced-motion: reduce) {{ * {{ transition:none !important; }} }}
</style>

<header>
  <div class="kap hd">
    <div class="gozeyaz">KPSS Lisans · Genel Kültür · 18 soru</div>
    <h1>Türkiye Coğrafyası Harita Portföyü</h1>
    <p class="ozet">29 konunun her biri iki sayfa: etiketli <b>cevap anahtarı</b> ve aynı
    numaralandırmayı taşıyan <b>dilsiz çalışma haritası</b>. Bak, sayfayı çevir, hafızandan
    doldur, karşılaştır.</p>
    <dl class="meta">
      <div><dt>Sayfa</dt><dd>61</dd></div>
      <div><dt>Konu</dt><dd>29</dd></div>
      <div><dt>Kâğıt</dt><dd>A4 yatay</dd></div>
      <div><dt>Baskı</dt><dd>Siyah-beyaz</dd></div>
      <div><dt>Doğrulama</dt><dd>107/107</dd></div>
    </dl>
    <p class="uyari"><span>📄</span><span><b>Baskı kaynağı PDF'tir.</b> Bu sayfa hızlıca göz
    atmak ve hangi sayfayı basacağına karar vermek için. Asıl dosya sohbete eklendi ve
    depoda <code style="font-family:var(--mono);font-size:12.5px">kpss-cografya-harita/cikti/</code>
    altında duruyor.</span></p>
  </div>
</header>

<main>
<section class="blok"><div class="kap">
  <h2>Sınava bir hafta — çalışma sırası</h2>
  <ol class="plan">{plan_html}</ol>
</div></section>

<div class="arac"><div class="kap arac-ic">
  <button class="cip" data-f="mod" data-v="hepsi" aria-pressed="true">Tümü</button>
  <button class="cip" data-f="mod" data-v="dolu" aria-pressed="false">Cevap anahtarı</button>
  <button class="cip" data-f="mod" data-v="bos" aria-pressed="false">Dilsiz</button>
  <span class="ayir"></span>
  {filtre_html}
  <span class="ilerleme"><span class="cubuk"><i id="cubuk"></i></span><span id="sayac">0 / 61</span></span>
</div></div>

<section class="blok"><div class="kap" id="izgaralar"></div></section>

<section class="blok"><div class="kap">
  <h2>Bu haritalara neden güvenebilirsin</h2>
  <dl class="dogrulama">{dog_html}</dl>
  <ul class="kaynaklar">
    <li><b>İl sınırları</b> — resmî sınır verisi; 81 il, alan hesabı ile çapraz kontrol edildi.</li>
    <li><b>Yükselti</b> — AWS Terrain Tiles (SRTM/ASTER türevi), yaklaşık 300 m çözünürlük.
      Zirve koordinatları tahmin edilmedi: her zirve için bölgesel maksimum arandı ve resmî
      yükseklikle karşılaştırıldı. Bu yöntem üç hatalı koordinatı yakaladı.</li>
    <li><b>Kıyı, göl, akarsu</b> — Natural Earth 10m.</li>
    <li><b>İklim</b> — WorldClim v2.1, 1970–2000 normalleri. Izgara ortalaması olduğu için
      Rize (~2.300 mm) gibi yerel uçlar istasyon ölçümünden yumuşak görünür; uç değerler
      ilgili sayfada ayrıca yazılıdır.</li>
    <li><b>Nüfus</b> — TÜİK ADNKS 2025, gerçek il alanlarına bölünerek yoğunluk.</li>
    <li>Denetimleri kendin çalıştırabilirsin: <code>python3 src/dogrula.py</code></li>
  </ul>
</div></section>
</main>

<footer><div class="kap">
  Coğrafi bölge ve bölüm haritaları il bazlı şematiktir — gerçek 1941 bölge sınırları il
  sınırlarını keser; bu ilgili sayfalarda not edilmiştir. UNESCO listesi 2023 sonu itibarıyladır.
  Ürün sıralamaları yıllara göre değişebilir; ★ klasik olarak 1. sırada sorulan ili gösterir.
</div></footer>

<div class="buyut" id="buyut" role="dialog" aria-modal="true" aria-label="Sayfa görüntüleyici">
  <button class="gez kapat" id="kapat" aria-label="Kapat">✕</button>
  <div class="buyut-kutu">
    <img id="buyut-img" alt="">
    <div class="buyut-bar">
      <button class="gez" id="onceki" aria-label="Önceki sayfa">‹</button>
      <span class="no" id="buyut-no"></span><span id="buyut-ad"></span>
      <button class="gez" id="sonraki" aria-label="Sonraki sayfa">›</button>
    </div>
  </div>
</div>

<script>
const SAYFALAR = {json.dumps([{k:v for k,v in s.items() if k!='src'} for s in sayfalar], ensure_ascii=False)};
const KARTLAR = {json.dumps(kartlar, ensure_ascii=False)};
const BOLUMLER = {json.dumps(bolumler, ensure_ascii=False)};

// ızgarayı bölümlere ayırarak kur
const hedef = document.getElementById('izgaralar');
BOLUMLER.forEach(b => {{
  const ilgili = SAYFALAR.map((s,i)=>[s,i]).filter(([s])=>s.bolum===b);
  const grup = document.createElement('div');
  grup.className = 'grup';
  grup.innerHTML = `<h3>${{b}}</h3><p>${{ilgili.length}} sayfa</p>` +
    `<div class="izgara">${{ilgili.map(([,i])=>KARTLAR[i]).join('')}}</div>`;
  hedef.appendChild(grup);
}});

const kartlar = [...document.querySelectorAll('.kart')];
const ANAHTAR = 'kpss-harita-bitti-v1';
let bitti = new Set();
try {{ bitti = new Set(JSON.parse(localStorage.getItem(ANAHTAR) || '[]')); }} catch (e) {{}}

function kaydet() {{
  try {{ localStorage.setItem(ANAHTAR, JSON.stringify([...bitti])); }} catch (e) {{}}
}}
function ilerlemeYaz() {{
  const n = bitti.size;
  document.getElementById('sayac').textContent = `${{n}} / ${{kartlar.length}}`;
  document.getElementById('cubuk').style.width = (n / kartlar.length * 100) + '%';
}}
function isaretleriKur() {{
  kartlar.forEach(k => k.classList.toggle('bitti', bitti.has(k.dataset.no)));
  ilerlemeYaz();
}}
isaretleriKur();

kartlar.forEach((k, i) => {{
  k.addEventListener('click', ev => {{
    if (ev.target.classList.contains('isaret')) {{
      const no = k.dataset.no;
      bitti.has(no) ? bitti.delete(no) : bitti.add(no);
      kaydet(); isaretleriKur();
      return;
    }}
    ac(kartlar.indexOf(k));
  }});
}});

// --- filtreler
let fMod = 'hepsi', fBolum = null;
function suz() {{
  kartlar.forEach(k => {{
    const modOK = fMod === 'hepsi' || k.dataset.mod === fMod;
    const bolOK = !fBolum || k.dataset.bolum === fBolum;
    k.classList.toggle('gizli', !(modOK && bolOK));
  }});
  document.querySelectorAll('.grup').forEach(g => {{
    const gorunur = [...g.querySelectorAll('.kart')].some(k => !k.classList.contains('gizli'));
    g.classList.toggle('gizli', !gorunur);
  }});
}}
document.querySelectorAll('.cip').forEach(c => {{
  c.addEventListener('click', () => {{
    if (c.dataset.f === 'mod') {{
      fMod = c.dataset.v;
      document.querySelectorAll('[data-f="mod"]').forEach(o =>
        o.setAttribute('aria-pressed', String(o === c)));
    }} else {{
      const acik = c.getAttribute('aria-pressed') === 'true';
      fBolum = acik ? null : c.dataset.v;
      document.querySelectorAll('[data-f="bolum"]').forEach(o =>
        o.setAttribute('aria-pressed', String(o === c && !acik)));
    }}
    suz();
  }});
}});

// --- büyütme
const kutu = document.getElementById('buyut');
const gimg = document.getElementById('buyut-img');
let aktif = 0;
function ac(i) {{
  aktif = i;
  const k = kartlar[i];
  gimg.src = k.querySelector('img').src;
  gimg.alt = k.querySelector('img').alt;
  document.getElementById('buyut-no').textContent = k.dataset.no.padStart(2, '0');
  document.getElementById('buyut-ad').textContent = k.querySelector('.kart-ad').textContent;
  kutu.setAttribute('open', '');
  document.getElementById('kapat').focus();
}}
function kapatDialog() {{ kutu.removeAttribute('open'); kartlar[aktif].focus(); }}
function kaydir(d) {{
  let i = aktif;
  do {{ i = (i + d + kartlar.length) % kartlar.length; }}
  while (kartlar[i].classList.contains('gizli') && i !== aktif);
  ac(i);
}}
document.getElementById('kapat').addEventListener('click', kapatDialog);
document.getElementById('onceki').addEventListener('click', () => kaydir(-1));
document.getElementById('sonraki').addEventListener('click', () => kaydir(1));
kutu.addEventListener('click', e => {{ if (e.target === kutu) kapatDialog(); }});
document.addEventListener('keydown', e => {{
  if (!kutu.hasAttribute('open')) return;
  if (e.key === 'Escape') kapatDialog();
  if (e.key === 'ArrowLeft') kaydir(-1);
  if (e.key === 'ArrowRight') kaydir(1);
}});
</script>'''

open(OUT, "w", encoding="utf-8").write(HTML)
print(f"yazıldı: {OUT} · {os.path.getsize(OUT) / 1e6:.2f} MB · {len(sayfalar)} sayfa")
