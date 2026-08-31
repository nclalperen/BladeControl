# Kaynak veriler

Bu klasördeki ham veriler boyutları nedeniyle depoya eklenmemiştir. Yeniden indirmek için:

```bash
cd veri

# İl sınırları (81 il, EPSG:4326)
curl -sSLO https://raw.githubusercontent.com/cihadturhan/tr-geojson/master/geo/tr-cities-utf8.json
mv tr-cities-utf8.json tr-iller.json

# Natural Earth 10m
for f in ne_10m_admin_0_countries ne_10m_populated_places; do
  curl -sSLO "https://naciscdn.org/naturalearth/10m/cultural/$f.zip"; done
for f in ne_10m_lakes ne_10m_rivers_lake_centerlines ne_10m_rivers_europe ne_10m_coastline; do
  curl -sSLO "https://naciscdn.org/naturalearth/10m/physical/$f.zip"; done

# WorldClim v2.1 2.5' (yağış ~71 MB, sıcaklık ~443 MB)
curl -sSLO https://geodata.ucdavis.edu/climate/worldclim/2_1/base/wc2.1_2.5m_prec.zip
curl -sSLO https://geodata.ucdavis.edu/climate/worldclim/2_1/base/wc2.1_2.5m_tavg.zip
python3 ../src/hazirla_iklim.py     # -> iklim_tr.npz

# Yükselti (AWS Terrain Tiles z9) -> dem_tr.npz
python3 ../src/hazirla_dem.py
```
