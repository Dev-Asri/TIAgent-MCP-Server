# Antigravity & Gemini Proje Kuralları (GEMINI.md)

## 1. Rol Ayrımı Kuralı (Köprü vs Programcı)

Kullanıcının talimatında belirttiği role göre hareket edilir:

### 1. "Köprüsün" Modu:
- Kullanıcı **"Köprüsün"** dediğinde `TIAgentMCPServer.csproj` veya TIAgent MCP Server C# kaynak kodlarında (`*.cs`, `*.xaml`, vb.) **KESİNLİKLE HİÇBİR DÜZENLEME YAPILMAZ**.
- Kod dosyaları gereksiz yere okunmaz veya incelenmez.
- Yalnızca TIAgent MCP Server'a komut göndermek için Köprü API'si (`http://localhost:19520` veya `Packages/Inbox`) kullanılır.
- Kullanıcının TIA Portal projesine ilişkin tüm istekleri (devre üretimi, tag, DB, silme işlemleri) köprü üzerinden TIAgent onay çubuğuna iletilir.

### 2. "Programcısın" Modu:
- Kullanıcı **"Programcısın"** dediğinde TIAgent MCP Server yazılımının kendisinde (`TIAgentMCPServer.csproj`) kod düzenlemesi, hata düzeltmesi veya mimari değişiklikler yapılabilir.
- Yapılan değişiklikler `dotnet build` ile derlenir ve uygulama yeniden başlatılır.

---

## 2. Sabit Firma Mühendislik ve Standart PLC Yazım Mimarisi

Kullanıcının projelerindeki tüm Ladder ve PLC otomasyon mantığı aşağıdaki 5 katı standarda uygun üretilmelidir:

### Standart 1: 10 Sabit Global DB Mimarisi
Tüm projelerde değişkenler bu 10 standart DB altında toplanır:
1. `"TIMER"`: Tüm TON / TOF / TP zamanlayıcı instanceları (`TON_Piston_Ileri`, `TON_Bekleme`, `TON_Kacak` vb.).
2. `"ERROR"`: Tüm arızalar (`GENEL_ARIZA`, `PISTON_ILERI_GIDEMEDI`, `BASINC_ULASILAMADI`, `KACAK_VAR` vb.) Set/Reset mantığıyla tutulur.
3. `"RECETE"`: Reçeteye bağlı değişkenler (`HEDEF_BASINC_BAR`, `BEKLEME_SURESI_SN`, `DONGU_HEDEF_SAYISI` vb.).
4. `"PARAMETRE"`: Reçeteden bağımsız kalıcı ayarlar (`MAX_GUVENLIK_BASINCI`, `POMPA_BASLATMA_GECIKMESI` vb.).
5. `"ARIZA_ZAMANLARI"`: Zaman aşımı limitleri (`Time` tipinde, örn: `PISTON_ILERI_TIMEOUT := T#5s`).
6. `"GECIKME_ZAMANLARI"`: Proses gecikmeleri (`Time` tipinde, örn: `BASINC_DUSURME_GECIKME := T#1s`).
7. `"ANALOG"`: Cetvel ve basınç sensörü değerleri (`Int` tipinde: `ON_BASINC_MAX`, `ON_BASINC_OFSET`, `ON_BASINC_HAM`, `ON_BASINC_BAR`).
8. `"PWM"`: Oransal valf / PWM parametreleri (`MIN_PWM_DEGERI`, `MAX_PWM_DEGERI`, `CALISMA_DUTY`).
9. `"KOSULLAR"`: Tüm aktüatör hareket koşul bitleri (`PISTON_ILERI_KOSUL`, `PISTON_GERI_KOSUL`).
10. `"SECENEKLER"`: Çalışma seçenekleri ve bayraklar.

---

### Standart 2: Standart `FC AI` ve Main [OB1] Çağrı Şablonu
- Her projede analog cetvel ve basınç sensörleri için standart bir `FC AI` (veya `AI`) bloğu oluşturulur:
  - **Arayüz:**
    - `AI` (Input, Int): Ham analog giriş (`%IW...`)
    - `MAX` (Input, Int): Maksimum birim skalası (örn: 250)
    - `OFSET` (Input, Int): Kalibrasyon ofseti
    - `POS` (Output, Int): Ofset uygulanmış net proses değeri
    - `HAM` (InOut, Int): Ham ölçekli değer
    - `REAL` (Temp, Real): NORM_X ara normalize değeri (0.0 .. 1.0)
  - **Blok İçi Mantık:** `NORM_X` (0..27648 $\to$ 0.0..1.0 Real) $\to$ `SCALE_X` (0.0..1.0 Real $\to$ 0..\#MAX Int $\to$ \#HAM) $\to$ `SUB` (\#HAM - \#OFSET $\to$ \#POS).
  - **Main [OB1] Kuralı:** Main bloğunun en başında, kaç adet basınç sensörü ve cetvel varsa her biri için ayrı bir network'te `FC AI` çağrılır (`Call AI`). Proses blokları (`FC_Pistonlar`, `FC_OtoCycle`) hesaplanmış değerleri `"ANALOG"` DB'sinden okur.

---

### Standart 3: 2-Network Piston / Aktüatör Hareket Kuralı
Her silindir veya aktüatör hareketi istisnasız **2 ardışık Ladder network'ü** halinde yazılır:
- **Ağ 1 (Koşul Network'ü):** YALNIZCA emniyet şartları ve sınır kilitleri yer alır:
  - `NOT "ERROR"."GENEL_ARIZA"` AND `Geri_Limit` AND NOT `Ileri_Valf` $\to$ `( ) "KOSULLAR"."PISTON_ILERI_KOSUL"`
- **Ağ 2 (Hareket & Tetikleme Network'ü):** Manuel ve Otomatik tetikleyiciler **paralel (OR) olarak "KOSULLAR" kontağından ÖNCE** bağlanır:
  - `Branch 1 (Manuel):` `"EL"` AND `"MANUEL_ILERI_BUTON"`
  - `Branch 2 (Otomatik):` `"OTO"` AND (`"OTO_W" == 5`)
  - **Birleşim:** Paralel kollar birleşir $\to$ `---[ ] "KOSULLAR"."PISTON_ILERI_KOSUL"---` $\to$ `( ) "PISTON_ILERI_VALF"`
  - **Zaman Aşımı:** Valf bobinine paralel kolda `"TIMER"."TON_Piston_Ileri"` (PT: `"ARIZA_ZAMANLARI"."PISTON_ILERI_TIMEOUT"`) çalışır.

---

### Standart 4: PWM ve Oransal Valf Kaskad Standardı
- Oransal valf / PWM sürüş devresi `FC_Pwm` içinde tek bir Ladder network'ünde `NORM_X` $\to$ `SCALE_X` $\to$ PWM Çıkışı (`%QW...`) birbirine seri kaskad (ENO $\to$ EN) olarak bağlanır.
- Normalize ara değişkeni **kesinlikle `Real` (0.0 .. 1.0)** olmalıdır (asla `Int` olamaz).

---

### Standart 5: `OTO_W` Çevrim Adımları ve Emniyet Sıfırlaması
- Otomatik adımlar `FC_OtoCycle` içinde `"OTO_W"` değişkeni üzerinden 5'er 5'er artırılarak (`0`, `5`, `10`, `15`...) yönetilir.
- Seçici şalter Stop/Nötr konuma alındığında (`NOT "EL" AND NOT "OTO"`):
  - `"OTO_W" := 0;` yapılır.
  - `"ERROR"."GENEL_ARIZA" := FALSE;` ve tüm hata bitleri resetlenir (`RCoil`).

---

## 3. Tecrübe Havuzu & Hatasız Paket Hazırlama Kuralı (Ornekler/ Klasörü)

1. **Önce Tecrübeyi Kontrol Et:**
   - Yeni bir devre, blok, DB, tag veya paket hazırlamadan önce **MUTLAKA** `Ornekler/SimaticML_Ve_Paket_Tecrubeleri.md` ve `Ornekler/` klasöründeki çalışan şablonlar kontrol edilir.
   - Paket; kültür (`en-US`), komut ismi (`NORM_X` için `Normalize`, `SCALE_X` için `Scale_X`), XML karakter kaçışları (`&lt;`, `&gt;`), analog word adresleme (`%IW64`, `%QW1000`), TON `ET` pini `OpenCon` bağlantısı, normalize oran değişkenlerinin (`*_Norm`) **kesinlikle `Real`** yapılması (asla `Int` olmamalı, yoksa `SCALE_X`'te aritmetik taşma ve `ENO=False` ile çıkış kaybı yaşanır) ve IEC zamanlayıcı standartlarına önceden tam uyumlu şekilde hazırlanır.

2. **Hata ve Düzeltmeleri Tecrübeye Kaydet:**
   - Paket gönderiminde veya TIA proje derlemesinde herhangi bir hata ile karşılaşıldığında ve bu hata çözüldüğünde; hatanın sebebi, çözümü ve kuralı anında `Ornekler/SimaticML_Ve_Paket_Tecrubeleri.md` dosyasına eklenerek güncellenir ve kalıcı tecrübe olarak saklanır.

---

## 4. İsimlendirme Kuralı (Kesin Yasak)
- Projede hiçbir şekilde hiçbir yerde eski isim ("Online" ve "Ladder" kelimeleri yan yana) GEÇMEYECEKTİR.
- Kullanıcıya verilen yanıtlarda, bilgilendirme mesajlarında, arayüz metinlerinde, loglarda, komut başlıklarında (toolSummary), kodlarda, dosyalarda veya açıklamalarda bu ifade KESİNLİKLE KULLANILMAZ.
- Projenin, yazılımın, servisin ve köprünün adı her zaman ve istisnasız yalnızca **TIAgent** veya **TIAgent MCP Server** olarak anılır.
- Projenin, yazılımın, servisin ve köprünün adı her zaman ve istisnasız yalnızca **TIAgent** veya **TIAgent MCP Server** olarak anılır.
