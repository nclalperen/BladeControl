# -*- coding: utf-8 -*-
"""Fiziki coğrafya verileri.

Zirve koordinatları DEM'den (AWS Terrain Tiles z9) bölgesel maksimum
aranarak DOĞRULANMIŞTIR; yükseklikler resmî değerlerdir.
"""

# (ad, lon, lat, yükseklik m, tip, il)
#   tip: "volkanik" | "kivrim" | "kutle"
ZIRVELER = [
 ("Ağrı Dağı",        44.305, 39.702, 5137, "volkanik", "Ağrı/Iğdır"),
 ("Cilo (Reşko)",     43.959, 37.502, 4135, "kivrim",   "Hakkâri"),
 ("Süphan Dağı",      42.833, 38.931, 4058, "volkanik", "Bitlis"),
 ("Kaçkar Dağı",      41.162, 40.834, 3937, "kivrim",   "Rize/Artvin"),
 ("Erciyes Dağı",     35.449, 38.532, 3917, "volkanik", "Kayseri"),
 ("Demirkazık",       35.149, 37.806, 3756, "kivrim",   "Niğde"),
 ("Tendürek Dağı",    43.876, 39.357, 3584, "volkanik", "Ağrı/Van"),
 ("Medetsiz (Bolkar)",34.630, 37.393, 3524, "kivrim",   "Niğde/Mersin"),
 ("Munzur (Akbaba)",  39.539, 39.533, 3462, "kivrim",   "Tunceli/Erzincan"),
 ("Palandöken",       41.231, 39.782, 3271, "kutle",    "Erzurum"),
 ("Hasan Dağı",       34.166, 38.127, 3268, "volkanik", "Aksaray"),
 ("Bingöl Dağı",      41.386, 39.358, 3250, "volkanik", "Bingöl/Muş"),
 ("Mescit Dağı",      41.190, 40.373, 3239, "kutle",    "Erzurum"),
 ("Kısır Dağı",       43.080, 40.946, 3197, "volkanik", "Ardahan/Kars"),
 ("Kızlarsivrisi",    30.122, 36.605, 3086, "kivrim",   "Antalya"),
 ("Nemrut Dağı",      42.256, 38.654, 2948, "volkanik", "Bitlis"),
 ("Davraz Dağı",      30.721, 37.752, 2637, "kivrim",   "Isparta"),
 ("Ilgaz Dağı",       33.864, 41.100, 2587, "kivrim",   "Kastamonu/Çankırı"),
 ("Honaz Dağı",       29.285, 37.678, 2571, "kivrim",   "Denizli"),
 ("Uludağ",           29.219, 40.071, 2543, "kutle",    "Bursa"),
 ("Köroğlu Dağları",  31.869, 40.515, 2378, "kivrim",   "Bolu"),
 ("Nemrut D. (tarihî)",38.836,38.045, 2150, "kutle",    "Adıyaman"),
 ("Bozdağ",           28.101, 38.323, 2159, "kutle",    "İzmir/Manisa"),
 ("Karacadağ",        39.830, 37.713, 1957, "volkanik", "Ş.urfa/Diyarbakır"),
 ("Kazdağı (İda)",    26.862, 39.702, 1774, "kutle",    "Balıkesir/Çanakkale"),
]

# Sıradağ eksenleri — DEM kabartması üzerinde doğrulanan hat noktaları
# (ad, etiket_lon, etiket_lat, eksen noktaları) — eksenler DEM kabartmasıyla doğrulandı
SIRADAGLAR = [
 ("KUZEY ANADOLU DAĞLARI", 36.10, 41.55, [
   (28.9,40.75),(30.6,40.85),(32.2,40.95),(33.7,41.15),(35.2,41.35),
   (36.9,40.95),(38.6,40.75),(40.3,40.75),(41.8,41.0),(42.9,41.15)]),
 ("BATI TOROSLAR", 30.05, 37.45, [
   (29.2,37.15),(29.9,36.85),(30.6,36.95),(31.5,37.15),(32.4,37.05)]),
 ("ORTA TOROSLAR", 34.20, 38.05, [
   (32.4,37.05),(33.5,37.15),(34.6,37.45),(35.6,37.85),(36.4,38.25)]),
 ("GÜNEYDOĞU TOROSLAR", 40.20, 38.85, [
   (36.4,38.25),(37.8,38.35),(39.3,38.35),(40.8,38.25),(42.3,37.85),(43.8,37.55)]),
]

# (ad, lon, lat, tip, not)   tip: "delta" | "kiyi" | "ic"
OVALAR = [
 ("Çukurova",        35.35,36.85,"delta","Seyhan-Ceyhan deltası; Türkiye'nin en büyük delta ovası"),
 ("Bafra Ovası",     35.90,41.50,"delta","Kızılırmak deltası"),
 ("Çarşamba Ovası",  36.72,41.30,"delta","Yeşilırmak deltası"),
 ("Silifke Ovası",   33.95,36.36,"delta","Göksu deltası"),
 ("Amik Ovası",      36.42,36.32,"ic","Asi Nehri; kurutulan göl tabanı"),
 ("B. Menderes Ov.", 27.60,37.72,"kiyi","Grabende kurulmuş; pamuk-incir"),
 ("K. Menderes Ov.", 27.45,38.10,"kiyi",""),
 ("Gediz (Menemen)", 27.15,38.55,"kiyi",""),
 ("Bakırçay Ovası",  27.15,39.05,"kiyi",""),
 ("Antalya Ovası",   30.75,36.95,"kiyi","Traverten üzerinde"),
 ("Adapazarı Ovası", 30.42,40.75,"ic","Sakarya; en verimli tarım alanlarından"),
 ("Bursa Ovası",     28.95,40.15,"ic",""),
 ("Konya Ovası",     32.60,37.85,"ic","Kapalı havza; en geniş iç ova"),
 ("Ereğli Ovası",    33.85,37.55,"ic",""),
 ("Harran Ovası",    39.03,36.95,"ic","GAP ile sulandı; pamuk"),
 ("Diyarbakır Ov.",  40.20,37.85,"ic",""),
 ("Muş Ovası",       41.55,38.78,"ic","Doğu Anadolu'nun en büyük ovası"),
 ("Erzurum Ovası",   41.30,39.95,"ic",""),
 ("Erzincan Ovası",  39.45,39.75,"ic","KAF üzerinde, deprem riski yüksek"),
 ("Iğdır Ovası",     44.05,39.92,"ic","Mikroklima — Doğu Anadolu'da pamuk yetişir"),
 ("Malatya Ovası",   38.30,38.35,"ic","Kayısı"),
 ("Elbistan Ovası",  37.20,38.20,"ic","Linyit havzası"),
 ("Ergene Ovası",    26.95,41.28,"ic","Ayçiçeği-çeltik"),
 ("Erbaa-Niksar Ov.",36.75,40.65,"ic",""),
]

PLATOLAR = [
 ("Cihanbeyli Pl.",  32.90,38.65,"İç Anadolu"),
 ("Obruk Platosu",   33.30,38.05,"İç Anadolu — karstik obruklar"),
 ("Haymana Pl.",     32.50,39.40,"İç Anadolu"),
 ("Bozok Platosu",   34.80,39.75,"İç Anadolu"),
 ("Uzunyayla",       36.60,38.75,"İç Anadolu"),
 ("Taşeli Platosu",  33.10,36.85,"Akdeniz — karstik"),
 ("Teke Platosu",    29.90,37.10,"Akdeniz — karstik"),
 ("Erzurum-Kars Pl.",42.20,40.45,"Doğu Anadolu — volkanik, çayır"),
 ("Ardahan Platosu", 42.85,41.05,"Doğu Anadolu"),
 ("Gaziantep Pl.",   37.40,37.05,"Güneydoğu — Antep fıstığı"),
 ("Şanlıurfa Pl.",   39.10,37.35,"Güneydoğu"),
 ("Adıyaman Pl.",    38.30,37.70,"Güneydoğu"),
 ("Çatalca-Kocaeli", 29.35,41.05,"Marmara"),
 ("Yazılıkaya Pl.",  30.55,39.10,"Ege — İç Batı Anadolu"),
]

# (ad, lon, lat, oluşum, alan km2 veya None, not)
GOLLER = [
 ("Van Gölü",       42.95,38.63,"tektonik + volkanik set",3713,"Türkiye'nin en büyük gölü; sodalı"),
 ("Tuz Gölü",       33.40,38.75,"tektonik",1500,"2. büyük; çok sığ, tuz üretimi"),
 ("Beyşehir Gölü",  31.55,37.75,"karstik-tektonik",656,"En büyük tatlı su gölü"),
 ("Eğirdir Gölü",   30.87,38.05,"karstik-tektonik",468,""),
 ("İznik Gölü",     29.52,40.44,"tektonik",298,""),
 ("Burdur Gölü",    30.17,37.72,"tektonik",153,"Acı-tuzlu, kapalı havza"),
 ("Acıgöl",         29.88,37.80,"tektonik",153,"Sodyum sülfat"),
 ("Ulubat Gölü",    28.60,40.17,"alüvyal set",134,""),
 ("Manyas (Kuş) G.",27.97,40.19,"alüvyal set",166,"Kuş Cenneti Millî Parkı"),
 ("Sapanca Gölü",   30.27,40.71,"tektonik",45,"KAF çukurluğunda"),
 ("Hazar Gölü",     39.40,38.48,"tektonik",86,"DAF üzerinde"),
 ("Çıldır Gölü",    43.28,41.05,"volkanik set",123,"Türkiye'nin en yüksek büyük gölü (1959 m)"),
 ("Erçek Gölü",     43.65,38.65,"volkanik set",107,""),
 ("Nazik Gölü",     42.28,38.85,"volkanik set",44,""),
 ("Haçlı (Bulanık)",42.15,39.03,"volkanik set",16,""),
 ("Balık Gölü",     43.60,39.75,"volkanik set",34,""),
 ("Nemrut Krater G.",42.23,38.62,"krater",12,"Türkiye'nin en büyük krater gölü"),
 ("Meke Gölü",      33.64,37.68,"krater",None,"Konya-Karapınar; maar"),
 ("Tortum Gölü",    41.62,40.60,"heyelan set",7,"Tortum Şelalesi"),
 ("Sera Gölü",      39.53,41.00,"heyelan set",None,"Trabzon"),
 ("Abant Gölü",     31.28,40.61,"heyelan set",1,"Bolu"),
 ("Yedigöller",     31.75,40.95,"heyelan set",None,"Bolu"),
 ("Zinav Gölü",     36.70,40.35,"heyelan set",None,"Tokat"),
 ("Borabay Gölü",   35.90,40.75,"heyelan set",None,"Amasya"),
 ("Bafa Gölü",      27.45,37.49,"alüvyal set (lagün)",60,"Eski Latmos Körfezi"),
 ("Köyceğiz Gölü",  28.65,36.92,"alüvyal set (lagün)",52,""),
 ("Büyükçekmece G.",28.57,41.03,"alüvyal set (lagün)",29,""),
 ("Küçükçekmece G.",28.76,40.99,"alüvyal set (lagün)",16,""),
 ("Terkos (Durusu)",28.61,41.33,"alüvyal set (lagün)",25,"İstanbul'a içme suyu"),
 ("Akyatan Gölü",   35.30,36.63,"alüvyal set (lagün)",49,"Çukurova kıyı lagünü"),
 ("Sarıkum Gölü",   34.90,42.02,"alüvyal set (lagün)",None,"Sinop"),
 ("Salda Gölü",     29.68,37.55,"tektonik",44,"Beyaz kumu ve mikrobiyalitleriyle korunan alan"),
 ("Akşehir Gölü",   31.45,38.52,"tektonik",353,"Kuruma tehdidi"),
 ("Eber Gölü",      31.10,38.63,"tektonik",126,""),
 ("Suğla Gölü",     32.15,37.35,"karstik",None,""),
 ("Kovada Gölü",    30.87,37.63,"karstik",9,""),
 ("Cilo buzul gölleri",43.95,37.53,"buzul",None,"Hakkâri — Türkiye'nin başlıca buzul gölleri"),
]

# (ad, lon, lat, akarsu, not)
BARAJLAR = [
 ("Atatürk Barajı",   38.32,37.49,"Fırat","Gövde hacmi en büyük; en geniş yapay göl (817 km²)"),
 ("Keban Barajı",     38.76,38.80,"Fırat","İlk büyük GAP barajı"),
 ("Karakaya Barajı",  38.90,38.23,"Fırat",""),
 ("Birecik Barajı",   37.98,37.05,"Fırat",""),
 ("Ilısu Barajı",     41.85,37.53,"Dicle","Hasankeyf'i sular altında bıraktı"),
 ("Hirfanlı Barajı",  33.55,39.22,"Kızılırmak",""),
 ("Altınkaya Barajı", 35.72,41.32,"Kızılırmak",""),
 ("Almus Barajı",     36.90,40.37,"Yeşilırmak",""),
 ("Hasan Uğurlu B.",  36.62,40.98,"Yeşilırmak",""),
 ("Sarıyar Barajı",   31.42,40.02,"Sakarya",""),
 ("Gökçekaya Barajı", 31.15,40.00,"Sakarya",""),
 ("Seyhan Barajı",    35.32,37.05,"Seyhan",""),
 ("Aslantaş Barajı",  36.20,37.30,"Ceyhan",""),
 ("Menzelet Barajı",  36.85,37.75,"Ceyhan",""),
 ("Oymapınar Barajı", 31.48,36.92,"Manavgat",""),
 ("Demirköprü B.",    28.32,38.65,"Gediz",""),
 ("Adıgüzel Barajı",  28.95,38.05,"B. Menderes",""),
 ("Deriner Barajı",   41.77,41.28,"Çoruh","Türkiye'nin en yüksek barajı (249 m)"),
 ("Yusufeli Barajı",  41.63,40.80,"Çoruh","Türkiye'nin en yüksek 2. barajı"),
 ("Kralkızı Barajı",  40.20,38.25,"Dicle",""),
]

# (ad, uzunluk km, döküldüğü yer, not)  — çizgiler NE verisinden gelir
AKARSU_KUNYE = [
 ("Kızılırmak", 1355, "Karadeniz", "Tamamı Türkiye'de olan EN UZUN akarsu"),
 ("Fırat",      1263, "Basra Körfezi", "Türkiye'deki en uzun akarsu (toplam ~2800 km)"),
 ("Sakarya",     824, "Karadeniz", "Porsuk ve Ankara Çayı kolları"),
 ("Murat",       720, "Fırat'a", "Fırat'ın ana kolu; Ağrı'dan doğar"),
 ("Aras",        548, "Hazar Denizi", "Kapalı havza — denize ulaşmaz"),
 ("Seyhan",      560, "Akdeniz", "Zamantı + Göksu birleşimi"),
 ("B. Menderes", 584, "Ege Denizi", "Menderesleriyle ünlü; graben içinde"),
 ("Dicle",       523, "Basra Körfezi", "Türkiye'de 523 km"),
 ("Yeşilırmak",  519, "Karadeniz", "Çarşamba deltası"),
 ("Ceyhan",      509, "Akdeniz", ""),
 ("Meriç",       490, "Ege Denizi", "Türkiye-Yunanistan sınırını çizer"),
 ("Çoruh",       466, "Karadeniz (Gürcistan'da)", "Akıntısı en hızlı; rafting"),
 ("Gediz",       401, "Ege Denizi", ""),
 ("Kelkit",      373, "Yeşilırmak'a", ""),
 ("Susurluk",    321, "Marmara Denizi", "Simav Çayı olarak da anılır"),
 ("Göksu",       260, "Akdeniz", "Silifke deltası"),
 ("Asi",         246, "Akdeniz", "Türkiye'ye GÜNEYDEN girer (ters akış); Samandağ'dan dökülür"),
]

# Kapalı havzalar
KAPALI_HAVZALAR = [
 ("Konya Kapalı Havzası", 32.80,38.00, "En büyük kapalı havza"),
 ("Van Gölü Havzası",     42.95,38.63, "Sodalı göl"),
 ("Tuz Gölü Havzası",     33.40,38.75, ""),
 ("Göller Yöresi",        30.60,37.85, "Burdur-Isparta-Acıgöl"),
 ("Akarçay (Afyon)",      30.60,38.70, ""),
]

# Diri fay hatları — genel eksenler
FAYLAR = [
 ("KUZEY ANADOLU FAY HATTI (KAF)", [
   (26.5,40.55),(27.6,40.72),(28.9,40.72),(30.0,40.73),(31.0,40.72),(32.3,40.78),
   (33.6,41.02),(34.8,40.90),(36.0,40.72),(37.2,40.55),(38.4,40.10),(39.5,39.78),
   (40.6,39.62),(41.0,39.32)]),
 ("DOĞU ANADOLU FAY HATTI (DAF)", [
   (41.0,39.32),(40.1,38.85),(39.3,38.55),(38.6,38.35),(37.9,37.95),(37.2,37.55),
   (36.6,37.05),(36.2,36.50),(36.15,36.00)]),
 ("BATI ANADOLU GRABENLERİ", [
   (26.9,38.60),(28.2,38.55),(29.4,38.45)]),
]

VOLKANIK_ALANLAR = [
 ("Kula (Manisa)",            28.65,38.55),
 ("Kapadokya volkanik alanı", 34.70,38.45),
 ("Karacadağ",                39.83,37.71),
 ("Doğu Anadolu volkanikleri",43.20,39.20),
 ("Isparta (Gölcük)",         30.50,37.73),
 ("Karapınar (Konya)",        33.60,37.70),
]

KARSTIK_ALANLAR = [
 ("Taşeli Platosu",   33.10,36.85), ("Teke Platosu",     29.90,37.10),
 ("Göller Yöresi",    30.60,37.85), ("Obruk Platosu",    33.30,38.05),
 ("Sivas jipsli karst",37.10,39.60), ("Pamukkale (traverten)",29.12,37.92),
 ("Antalya travertenleri",30.70,36.90),
]
