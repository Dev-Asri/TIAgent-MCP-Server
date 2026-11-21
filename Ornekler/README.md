# TIA Portal Kullanıcı Kod Şablonları & Eğitim Kütüphanesi (Ornekler)

Bu klasör, TIA Portal projeleri için standart kod şablonlarını, eğitim dokümanlarını, SimaticML kurallarını, otomatik doğrulama parametrelerini ve yapay zekâ modellerinin (Antigravity, Cursor, Claude vb.) sizin veya firmanızın PLC mimarisini öğrenmesi için hazırlanmış eğitim deposunu içerir.

---

### 🚀 Otomatik Şablon Çıkarıcı: TIAgent Template Creator

Artık projelerinizi tek tek elle dışa aktarmanıza gerek yoktur! Projenin kök dizinindeki **`template_creator.bat`** dosyasını çalıştırarak:
1. Açık olan herhangi bir TIA Portal projesine (V14 - V21) tek tıkla bağlanabilirsiniz.
2. Hedef PLC'yi seçip **"Şablonları Çıkar & Eğitime Hazırla"** butonuna basmanız yeterlidir.
3. Program saniyeler içinde:
   - Tüm **Program Bloklarını (OB, FB, FC, DB)** SimaticML XML formatında dışa aktarır.
   - SCL bloklarını ve DB interface'lerini temiz **`.scl`** kodlarına dönüştürür.
   - Tüm **Kullanıcı Veri Tiplerini (UDT)** `UDTs/` klasörüne XML ve SCL Struct olarak aktarır.
   - Tüm **PLC Tag Tablolarını** `Tags/PLC_Tags.csv` ve `Tags/PLC_Tags.xml` olarak derler.
   - CPU ve bağlı modülleri `00_Hardware_Config.md` olarak özetler.
   - Yapay zekânın projeyi 5 saniyede anlaması için **`AI_Project_Summary.md`** ve **`AI_Training_Manifest.json`** üretir.

---

### 📂 Klasör Mimarisi
Her proje bu klasör altına kendi adıyla otomatik yerleşir:
```text
Ornekler/
  ├── PaketlemeHatti_PLC1/
  │     ├── AI_Project_Summary.md         <-- Yapay zekânın ilk okuduğu stil ve kural kılavuzu
  │     ├── AI_Training_Manifest.json      <-- Yapılandırılmış blok ve tip kataloğu
  │     ├── 00_Hardware_Config.md         <-- CPU, modüller ve IP adresleri
  │     ├── Blocks/                       <-- OB, FB, FC, DB (XML + SCL)
  │     ├── UDTs/                         <-- Veri tipleri (XML + SCL Struct)
  │     └── Tags/                         <-- Tag tabloları (XML + CSV)
  ├── DolumIstasyonu_PLC2/
  └── ...
```

---

### 📚 Eğitim & Kural Dosyaları ve Çalışan Referans Paketler:
- **[Egitim_Norm_Scale_Degisken_Tipleri.md](Egitim_Norm_Scale_Degisken_Tipleri.md):** `NORM_X` ve `SCALE_X` bloklarında `Real` vs `Int` veri tipi kuralları, 16-bit taşma ve `ENO=False` çözümü.
- **[Paket_Dogrulama_Kurallari.json](Paket_Dogrulama_Kurallari.json):** TIAgent MCP Server'ın gelen her paketi denetlerken başvurduğu makine tarafından okunabilir doğrulama ve otomatik düzeltme kuralları.
- **[SimaticML_Ve_Paket_Tecrubeleri.md](SimaticML_Ve_Paket_Tecrubeleri.md):** TIA Portal Openness SimaticML derleyici kısıtlamaları, pin/wire bağlantıları ve tecrübe havuzu.
- **[Asansor_5Katli_LAD_Calisan_Paket.json](Asansor_5Katli_LAD_Calisan_Paket.json):** 38 Network'lük 5 katlı asansör, 0-10V/PWM motor hız kontrolü, yaklaşım ve stop switchli çalışan referans paketi.
- **[Tup_Kalinlik_Olcum_LAD_Calisan_Paket.json](Tup_Kalinlik_Olcum_LAD_Calisan_Paket.json):** 30 Network'lük çalışan referans kalibrasyon ve tüp ölçüm paketi.
- **[Piston_Basinc_Testi_LAD_Calisan_Paket.json](Piston_Basinc_Testi_LAD_Calisan_Paket.json):** Çalışan basınç testi ve piston otomasyon paketi.

### TIAgent Bu Dosyaları Nasıl Kullanır?
1. **Otomatik Paket Doğrulama (Pre-Flight Check):** TIAgent MCP Server, bir paket geldiğinde `Paket_Dogrulama_Kurallari.json` ve eğitim kurallarına bakarak sorunlu/hatalı paketlerin PLC'ye aktarılmasını önler; değişken tiplerini ve SimaticML etiketlerini otomatik olarak düzeltir.
2. **Yapay Zekâ Eğitimi:** AI modelleri kod üretmeden önce bu kurallara uyarak sıfır hatalı paketler oluşturur.

---

### 💡 Yapay Zekâ Bu Şablonları Nasıl Kullanır?
Siz asistanınıza:
> *"Ornekler/PaketlemeHatti_PLC1 şablonundaki valf kontrol FB'si ve UDT yapısını incele. Aynı mantıkta 4 istasyonlu yeni bir istasyon FC'si yaz."*

dediğinizde yapay zekâ:
1. `AI_Project_Summary.md` dosyasını okuyarak projedeki giriş (`i_`), çıkış (`q_`), bayrak (`x`) isimlendirme formatınızı öğrenir.
2. `UDTs/` klasöründeki struct tiplerini doğrudan kodun interface'inde kullanır.
3. Kendi kafasına göre değil, tamamen **sizin şirketinizin standartlarında ve mimarisinde** kod üretir.

---

## 📐 Kullanıcının Katı PLC Mühendislik & Mimari Standartları

Tüm şablonlarda ve üretilen yeni kodlarda aşağıdaki 6 temel mühendislik kuralı zorunludur:

### 1. Aktüatör / Valf Hareket Ağları (Movement Networks):
- Her bir hareket bobini (`Coil`) tek bir Ladder network'ünde sürülür. Çift bobin (duplicate coil) **ASLA** kullanılmaz.
- Network içinde **iki paralel kol (Branch / OR)** yer alır:
  - **Kol 1 (Manuel):** `"EL"` AND NOT `"ERRORDB"."GENEL_ARIZA"` AND `"MANUEL ILERI B"` ...
  - **Kol 2 (Otomatik):** `"OTO"` AND NOT `"ERRORDB"."GENEL_ARIZA"` AND (`"OTO_W" == 5` veya ilgili adımlar) ...
  - Bu iki paralel kol birleşerek doğrudan aktüatör çıkışına bağlanır (`"PISTON ILERI VN"` Coil).

### 2. Otomatik Çevrim (`OTO_W` Step Machine / Cycle):
- Otomatik çalışma `"OTO_W"` (Word veya Int) adım numarası üzerinden yönetilir.
- Adımlar **5'er 5'er** artırılır:
  - `0`: Beklemede / Stop
  - `5`: 1. Hareket (Örn: Piston İleri)
  - `10`: 2. Aşama (Örn: İleri Bekleme / Basınçta Tutma)
  - `15`: 3. Hareket (Örn: Dekompresyon / Tahliye)
  - `20`: 4. Hareket (Örn: Piston Geri)
  - `25`: 5. Aşama (Örn: Geri Bekleme)
  - `30`: 6. Hareket / Döngü Kontrolü
- Adım bitiş koşulu (sensör veya hedef basınç) gerçekleştiğinde `"OTO_W"` bir sonraki adıma (örn: `"OTO_W" := 10;` veya `ADD 5`) geçirilir.

### 3. Harekete Özgü Arıza Zamanlayıcıları (Motion Timeout Alarms):
- Her hareket için bir `TON` zamanlayıcısı çalışır.
- Zamanlayıcı koşulu: `"OTO"` AND (`"OTO_W" == AdımNo`).
- Belirlenen sürede (`PT`, örn. `T#5S`) hareket tamamlanamazsa `TON.Q` aktif olur ve `ERRORDB` içindeki ilgili arıza biti **SET** edilir:
  - `IF "TM"."TON_Piston_Ileri_ZamanAsimi".Q THEN "ERRORDB"."PISTON_ILERI_GIDEMEDI" := TRUE; END_IF;`
- Eğer hareket süresi dolmadan tamamlanırsa, `"OTO_W"` bir sonraki adıma geçeceğinden (`"OTO_W" == AdımNo`) derhal `FALSE` olur ve zamanlayıcı sıfırlanarak arızaya düşmez.

### 4. Arıza Yönetimi (`ERRORDB`):
- Tüm arızalar `ERRORDB` (Global DB) içinde tutulur.
- Arızalar `SCoil` (Set) ve `RCoil` (Reset) mantığı ile yönetilir.
- Herhangi bir hata aktif olduğunda genel arıza bayrağı çekilir, `"OTO_W" := 0` yapılır ve tüm hareket çıkışları emniyetle kapatılır.

### 5. PWM ve Analog Çevrim Standardı (Tek Dalda NORM_X + SCALE_X + PWM Cascade):
- PWM ve oransal valf / analog basınç kontrol devrelerinde `NORM_X` (SimaticML LAD: `Normalize`), `SCALE_X` (SimaticML LAD: `Scale_X`) ve PWM çıkışı / modülü **KESİNLİKLE TEK BİR LADDER AĞINDA / TEK DALDA (Single Branch / Cascade)** bağlanır.
- Asla ayrı ayrı network'lere veya bağımsız dallara bölünmez.
- Seri akış bağlantısı (Power Flow):
  - `Powerrail` (veya çalışma şartı) $\rightarrow$ `Normalize.en`
  - `Normalize.eno` $\rightarrow$ `Scale_X.en`
  - `Scale_X.eno` $\rightarrow$ `CTRL_PWM.en` (veya PWM çıkışına / `Move.en`)
- Sinyal / Veri akışı:
  - `Normalize.min` $\rightarrow$ Ham analog min (örn: `0`)
  - `Normalize.value` $\rightarrow$ Ham analog giriş (örn: `%IW64` veya `"Basinc_Ayar"`)
  - `Normalize.max` $\rightarrow$ Ham analog max (örn: `27648` veya `250.0`)
  - `Normalize.out` $\rightarrow$ Doğrudan `Scale_X.value` girişine bağlanır (ara tag veya direkt tel).
  - `Scale_X.min` $\rightarrow$ Hedef skala min (örn: `0` veya `0.0`)
  - `Scale_X.max` $\rightarrow$ Hedef skala max (örn: `27648` veya `100.0`)
  - `Scale_X.out` $\rightarrow$ Doğrudan PWM çıkışına (`%QW...`, PWM duty cycle veya PWM modülü `PWM` parametresi).

### 6. Mod Değişimi & Nötr Emniyet Sıfırlaması (`NOT "EL" AND NOT "OTO"`):
- `Main` [OB1] bloğunda veya ana mod denetim ağında, 3 konumlu seçici şalter nötr (0 / Stop) konumundayken (`NOT "EL" AND NOT "OTO"`):
  - **Otomatik Çevrim Sıfırlama:** `"OTO_W" := 0;` (devam eden tüm adımlar derhal durdurulur ve sıfırlanır).
  - **Arıza Sıfırlama:** `"ERRORDB"."GENEL_ARIZA" := FALSE;` ve `ERRORDB` içindeki tüm arıza bitleri resetlenir (`RCoil` / Reset).
  - Şalter nötr konuma alındığında hem arızalar temizlenir hem de sistem sıfırlanarak bir sonraki çalışma için tam emniyete alınır.
