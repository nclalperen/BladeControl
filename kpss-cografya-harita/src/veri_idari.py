# -*- coding: utf-8 -*-
"""İdari bölünüş, coğrafi bölgeler/bölümler ve nüfus verileri.

Nüfus: TÜİK ADNKS (2025 il nüfusları).
Bölge/bölüm ayrımı: 1941 Birinci Türk Coğrafya Kongresi sınıflandırması.
DİKKAT: Gerçek bölge sınırları il sınırlarını KESER. Buradaki il eşlemesi,
her ilin *ağırlıklı olarak* içinde bulunduğu bölgeyi/bölümü verir; haritada
bu durum ayrıca not edilir (bkz. IKI_BOLGELI_ILLER).
"""

PLAKA = {
 1:"Adana",2:"Adıyaman",3:"Afyonkarahisar",4:"Ağrı",5:"Amasya",6:"Ankara",7:"Antalya",
 8:"Artvin",9:"Aydın",10:"Balıkesir",11:"Bilecik",12:"Bingöl",13:"Bitlis",14:"Bolu",
 15:"Burdur",16:"Bursa",17:"Çanakkale",18:"Çankırı",19:"Çorum",20:"Denizli",
 21:"Diyarbakır",22:"Edirne",23:"Elazığ",24:"Erzincan",25:"Erzurum",26:"Eskişehir",
 27:"Gaziantep",28:"Giresun",29:"Gümüşhane",30:"Hakkâri",31:"Hatay",32:"Isparta",
 33:"Mersin",34:"İstanbul",35:"İzmir",36:"Kars",37:"Kastamonu",38:"Kayseri",
 39:"Kırklareli",40:"Kırşehir",41:"Kocaeli",42:"Konya",43:"Kütahya",
 44:"Malatya",45:"Manisa",46:"Kahramanmaraş",47:"Mardin",48:"Muğla",49:"Muş",
 50:"Nevşehir",51:"Niğde",52:"Ordu",53:"Rize",54:"Sakarya",55:"Samsun",56:"Siirt",
 57:"Sinop",58:"Sivas",59:"Tekirdağ",60:"Tokat",61:"Trabzon",62:"Tunceli",
 63:"Şanlıurfa",64:"Uşak",65:"Van",66:"Yozgat",67:"Zonguldak",68:"Aksaray",
 69:"Bayburt",70:"Karaman",71:"Kırıkkale",72:"Batman",73:"Şırnak",74:"Bartın",
 75:"Ardahan",76:"Iğdır",77:"Yalova",78:"Karabük",79:"Kilis",80:"Osmaniye",81:"Düzce",
}
IL_PLAKA = {v: k for k, v in PLAKA.items()}

# TÜİK ADNKS 2025
NUFUS = {
 "Adana":2283609,"Adıyaman":617821,"Afyonkarahisar":751808,"Ağrı":491489,
 "Aksaray":441136,"Amasya":342242,"Ankara":5910320,"Antalya":2777677,
 "Ardahan":90392,"Artvin":167531,"Aydın":1172107,"Balıkesir":1284517,
 "Bartın":206663,"Batman":662626,"Bayburt":82836,"Bilecik":228995,
 "Bingöl":282299,"Bitlis":360423,"Bolu":327173,"Burdur":277226,"Bursa":3263011,
 "Çanakkale":573976,"Çankırı":200549,"Çorum":519590,"Denizli":1060975,
 "Diyarbakır":1852356,"Düzce":415622,"Edirne":422438,"Elazığ":605678,
 "Erzincan":239625,"Erzurum":736877,"Eskişehir":927956,"Gaziantep":2222415,
 "Giresun":455074,"Gümüşhane":138807,"Hakkâri":279681,"Hatay":1577531,
 "Iğdır":205071,"Isparta":445303,"İstanbul":15754053,"İzmir":4504185,
 "Kahramanmaraş":1146278,"Karabük":249614,"Karaman":262355,"Kars":268991,
 "Kastamonu":379934,"Kayseri":1458991,"Kırıkkale":282830,"Kırklareli":379595,
 "Kırşehir":242777,"Kilis":157363,"Kocaeli":2161171,"Konya":2343409,
 "Kütahya":570478,"Malatya":755854,"Manisa":1477756,"Mardin":903576,
 "Mersin":1956428,"Muğla":1099547,"Muş":389127,"Nevşehir":320150,
 "Niğde":374492,"Ordu":768087,"Osmaniye":564123,"Rize":346947,
 "Sakarya":1123693,"Samsun":1392403,"Siirt":332369,"Sinop":225848,
 "Sivas":631401,"Şanlıurfa":2265800,"Şırnak":573666,"Tekirdağ":1208441,
 "Tokat":614141,"Trabzon":823323,"Tunceli":85083,"Uşak":374405,
 "Van":1112013,"Yalova":311635,"Yozgat":413208,"Zonguldak":585203,
}

BOLGELER = ["Marmara", "Ege", "Akdeniz", "İç Anadolu",
            "Karadeniz", "Doğu Anadolu", "Güneydoğu Anadolu"]

IL_BOLGE = {}
_B = {
 "Marmara": ["Edirne","Kırklareli","Tekirdağ","İstanbul","Kocaeli","Sakarya",
             "Yalova","Bursa","Balıkesir","Çanakkale","Bilecik"],
 "Ege": ["İzmir","Manisa","Aydın","Denizli","Muğla","Kütahya","Uşak","Afyonkarahisar"],
 "Akdeniz": ["Antalya","Isparta","Burdur","Mersin","Adana","Osmaniye","Hatay",
             "Kahramanmaraş"],
 "İç Anadolu": ["Ankara","Konya","Karaman","Aksaray","Niğde","Nevşehir","Kırşehir",
                "Kırıkkale","Yozgat","Sivas","Kayseri","Eskişehir","Çankırı"],
 "Karadeniz": ["Zonguldak","Bartın","Karabük","Kastamonu","Sinop","Samsun","Amasya",
               "Tokat","Çorum","Ordu","Giresun","Trabzon","Rize","Artvin",
               "Gümüşhane","Bayburt","Bolu","Düzce"],
 "Doğu Anadolu": ["Erzurum","Erzincan","Kars","Ardahan","Ağrı","Iğdır","Van","Bitlis",
                  "Muş","Bingöl","Elazığ","Malatya","Tunceli","Hakkâri"],
 "Güneydoğu Anadolu": ["Gaziantep","Kilis","Şanlıurfa","Adıyaman","Diyarbakır",
                       "Mardin","Batman","Siirt","Şırnak"],
}
for _b, _iller in _B.items():
    for _i in _iller:
        IL_BOLGE[_i] = _b

# (bölüm adı, bağlı olduğu bölge, iller)
BOLUMLER = [
 ("Yıldız Dağları Bölümü",    "Marmara", ["Kırklareli"]),
 ("Ergene Bölümü",            "Marmara", ["Edirne","Tekirdağ"]),
 ("Çatalca-Kocaeli Bölümü",   "Marmara", ["İstanbul","Kocaeli","Sakarya"]),
 ("Güney Marmara Bölümü",     "Marmara", ["Bursa","Balıkesir","Çanakkale","Bilecik","Yalova"]),
 ("Asıl Ege Bölümü",          "Ege", ["İzmir","Manisa","Aydın","Denizli","Muğla"]),
 ("İç Batı Anadolu Bölümü",   "Ege", ["Kütahya","Uşak","Afyonkarahisar"]),
 ("Antalya Bölümü",           "Akdeniz", ["Antalya","Isparta","Burdur"]),
 ("Adana (Çukurova) Bölümü",  "Akdeniz", ["Adana","Mersin","Osmaniye","Hatay","Kahramanmaraş"]),
 ("Konya Bölümü",             "İç Anadolu", ["Konya","Karaman","Aksaray"]),
 ("Yukarı Sakarya Bölümü",    "İç Anadolu", ["Eskişehir","Ankara"]),
 ("Orta Kızılırmak Bölümü",   "İç Anadolu", ["Kırıkkale","Kırşehir","Nevşehir","Niğde","Kayseri"]),
 ("Yukarı Kızılırmak Bölümü", "İç Anadolu", ["Sivas","Yozgat","Çankırı"]),
 ("Batı Karadeniz Bölümü",    "Karadeniz", ["Zonguldak","Bartın","Karabük","Kastamonu",
                                            "Sinop","Bolu","Düzce"]),
 ("Orta Karadeniz Bölümü",    "Karadeniz", ["Samsun","Amasya","Çorum","Tokat","Ordu"]),
 ("Doğu Karadeniz Bölümü",    "Karadeniz", ["Giresun","Trabzon","Rize","Artvin",
                                            "Gümüşhane","Bayburt"]),
 ("Yukarı Fırat Bölümü",      "Doğu Anadolu", ["Elazığ","Malatya","Tunceli","Erzincan","Bingöl"]),
 ("Erzurum-Kars Bölümü",      "Doğu Anadolu", ["Erzurum","Kars","Ardahan","Ağrı","Iğdır"]),
 ("Yukarı Murat-Van Bölümü",  "Doğu Anadolu", ["Van","Bitlis","Muş"]),
 ("Hakkâri Bölümü",           "Doğu Anadolu", ["Hakkâri"]),
 ("Orta Fırat Bölümü",        "Güneydoğu Anadolu", ["Gaziantep","Kilis","Şanlıurfa","Adıyaman"]),
 ("Dicle Bölümü",             "Güneydoğu Anadolu", ["Diyarbakır","Mardin","Batman",
                                                    "Siirt","Şırnak"]),
]

# Bölge sınırı il sınırını kestiği için birden fazla bölgede toprağı olan iller
IKI_BOLGELI_ILLER = [
 ("Sivas",         "İç Anadolu + Karadeniz"),
 ("Çorum",         "Karadeniz + İç Anadolu"),
 ("Bolu / Düzce",  "Karadeniz + Marmara'ya yakın geçiş"),
 ("Kahramanmaraş", "Akdeniz + Doğu Anadolu"),
 ("Malatya",       "Doğu Anadolu + İç Anadolu'ya geçiş"),
 ("Adıyaman",      "Güneydoğu Anadolu + Doğu Anadolu"),
 ("Denizli",       "Ege + Akdeniz"),
 ("Muğla",         "Ege + Akdeniz"),
 ("Afyonkarahisar","Ege + İç Anadolu"),
 ("Eskişehir",     "İç Anadolu + Ege/Marmara"),
 ("Ankara",        "İç Anadolu + Karadeniz (kuzeyi)"),
 ("Antalya",       "Akdeniz + İç Anadolu (kuzeyi)"),
 ("Konya",         "İç Anadolu + Akdeniz (güneyi)"),
 ("Erzincan",      "Doğu Anadolu + Karadeniz"),
 ("Gaziantep",     "Güneydoğu Anadolu + Akdeniz"),
]

# Bölge künyesi: (yüzölçümü payı %, yaklaşık km², sıra notu)
BOLGE_ALAN_PAY = {
 "Doğu Anadolu": 21, "İç Anadolu": 19, "Karadeniz": 18, "Akdeniz": 15,
 "Ege": 11, "Marmara": 8.5, "Güneydoğu Anadolu": 7.5,
}

# (yön, yer, lon, lat, koordinat, etiket kaydırma dx/dy metre)
UC_NOKTALAR = [
 ("En Kuzey", "Sinop – İnceburun",                 35.163, 42.098, "42° 06′ K", -215000,  18000),
 ("En Güney", "Hatay – Topraktutan (Beysun) köyü", 36.150, 35.816, "35° 51′ K", -230000,  -8000),
 ("En Batı",  "Çanakkale – Gökçeada, Avlaka Br.",  25.665, 40.190, "25° 40′ D",  150000, -78000),
 ("En Doğu",  "Iğdır – Dilucu (Aralık)",           44.820, 39.750, "44° 48′ D", -150000,  62000),
]

SINIR_KAPILARI = [
 ("Kapıkule",   26.360, 41.740, "Bulgaristan", "Edirne"),
 ("Dereköy",    27.243, 41.990, "Bulgaristan", "Kırklareli"),
 ("İpsala",     26.383, 40.923, "Yunanistan",  "Edirne"),
 ("Pazarkule",  26.520, 41.700, "Yunanistan",  "Edirne"),
 ("Sarp",       41.545, 41.520, "Gürcistan",   "Artvin"),
 ("Türkgözü",   42.850, 41.440, "Gürcistan",   "Ardahan"),
 ("Alican",     43.930, 39.980, "Ermenistan",  "Iğdır (kapalı)"),
 ("Dilucu",     44.639, 39.805, "Nahçıvan",    "Iğdır"),
 ("Gürbulak",   44.400, 39.470, "İran",        "Ağrı"),
 ("Kapıköy",    44.320, 38.520, "İran",        "Van"),
 ("Esendere",   44.487, 37.683, "İran",        "Hakkâri"),
 ("Habur",      42.400, 37.180, "Irak",        "Şırnak"),
 ("Nusaybin",   41.210, 37.100, "Suriye",      "Mardin"),
 ("Akçakale",   38.947, 36.710, "Suriye",      "Şanlıurfa"),
 ("Karkamış",   37.995, 36.836, "Suriye",      "Gaziantep"),
 ("Öncüpınar",  37.100, 36.660, "Suriye",      "Kilis"),
 ("Cilvegözü",  36.620, 36.250, "Suriye",      "Hatay"),
]

# (ülke, yaklaşık kara sınırı km, konum)
KOMSU_SINIR = [
 ("Suriye", 911, "güney — en uzun kara sınırımız"),
 ("İran", 560, "doğu — sınırı hiç değişmeyen komşu"),
 ("Irak", 384, "güneydoğu"),
 ("Ermenistan", 328, "doğu — sınır kapıları kapalı"),
 ("Gürcistan", 276, "kuzeydoğu"),
 ("Bulgaristan", 269, "kuzeybatı"),
 ("Yunanistan", 203, "batı"),
 ("Nahçıvan (Azerbaycan)", 18, "doğu — en kısa kara sınırımız"),
]
