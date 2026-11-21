---
name: tia-agent-operation-mode
description: >-
  Strict role enforcement and workflow distinction between 'Köprüsün' (Bridge) mode 
  and 'Programcısın' (Developer) mode for TIAgent MCP Server, along with company-standard 
  PLC architecture guidelines (10 Global DBs, FC AI, 2-Network Piston motion, PWM, OTO_W).
---

# TIAgent MCP Server Çalışma Modları ve Standart PLC Mühendislik Kuralları

Bu belge, TIAgent ve Siemens TIA Portal projeleri üzerinde çalışan tüm yapay zeka ajanları (Antigravity, Gemini, Claude, Cursor, Copilot vb.) için bağlayıcı ve kalıcı ana referanstır.

---

## 1. "Köprüsün" Modu (Köprü / Bridge Görevi)

Kullanıcı komutuna **"Köprüsün"** diyerek başladığında veya köprü olduğunu belirttiğinde:

### KESİN YASAKLAR:
- **ASLA `TIAgentMCPServer.csproj` veya TIAgent MCP Server C# kaynak kodlarını (`*.cs`, `*.xaml`, vb.) düzenleme, değiştirme veya oluşturma.**
- **ASLA kod dosyalarını inceleme amaçlı dahi olsa gereksiz yere okuma.** Kodlara müdahale ediyormuş intibası verilmemelidir.
- **ASLA TIAgent MCP Server yazılımına kod müdahalesinde bulunma.**

### GÖREVLER:
- **YALNIZCA Köprü API'sini kullan:**
  - `http://localhost:19520` HTTP REST uç noktalarına istek gönder (veya `Packages/Inbox` klasörüne JSON paket/komut yaz).
  - Kullanıcının Siemens TIA Portal projesine ilişkin isteklerini (LAD devresi ekleme, DB/tag oluşturma, silme, temizleme) doğrudan köprüye ilet.
  - TIAgent onay çubuğunu (`Kabul Et` / `İptal Et`) tetikle ve kullanıcının masaüstü arayüzünden onay vermesini bekle (veya `AutoApprove: true` ile gönder).
  - TIAgent durumunu (`http://localhost:19520/api/status` veya `Packages/status.json`) kontrol et.

---

## 2. "Programcısın" Modu (Yazılımcı / Developer Görevi)

Kullanıcı açıkça **"Programcısın"** dediğinde veya TIAgent MCP Server yazılımının kendisini düzenlemeyi emrettiğinde:

### GÖREVLER:
- `TIAgentMCPServer.csproj` ve TIAgent MCP Server projesinin C# kaynak kodlarını (`Services/`, `Models/`, `MainWindow.xaml`, `MainWindow.xaml.cs`) düzenle, hata ayıkla ve refactor et.
- Projeyi `dotnet build` ile derle.
- Derlenen yeni binary'yi (`TIAgent.exe`) başlat.

---

## 3. Sabit Firma Mühendislik ve PLC Yazım Standartları (Tüm Agent'lar İçin Zorunlu)

Kullanıcının projelerindeki tüm Ladder ve PLC otomasyon mantığı aşağıdaki 5 katı standarda uygun üretilmelidir. Hiçbir agent bu standartların dışına çıkamaz:

### Standart 1: 10 Sabit Global DB Mimarisi
Tüm projelerde değişkenler bu 10 standart DB altında toplanır:
1. `"TIMER"`: Tüm TON / TOF / TP zamanlayıcı instanceları burada tanımlanır (`TON_Piston_Ileri`, `TON_Bekleme`, `TON_Kacak` vb.).
2. `"ERROR"`: Tüm arızalar (`GENEL_ARIZA`, `PISTON_ILERI_GIDEMEDI`, `BASINC_ULASILAMADI`, `KACAK_VAR` vb.) Set/Reset mantığıyla burada tutulur.
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
- Normalize ara değişkeni **kesinlikle `Real` (0.0 .. 1.0)** olmalıdır (asla `Int` olamaz, aksi halde 16-bit taşma ve çıkış kaybı oluşur).

---

### Standart 5: `OTO_W` Çevrim Adımları ve Emniyet Sıfırlaması
- Otomatik adımlar `FC_OtoCycle` içinde `"OTO_W"` değişkeni üzerinden 5'er 5'er artırılarak (`0`, `5`, `10`, `15`...) yönetilir.
- Seçici şalter Stop/Nötr konuma alındığında (`NOT "EL" AND NOT "OTO"`):
  - `"OTO_W" := 0;` yapılır.
  - `"ERROR"."GENEL_ARIZA" := FALSE;` ve tüm hata bitleri resetlenir (`RCoil`).

---

## 4. İsimlendirme Kuralı (Kesin Yasak)
- Projede hiçbir şekilde hiçbir yerde eski isim ("Online" ve "Ladder" kelimeleri yan yana) GEÇMEYECEKTİR.
- Tüm ajanlar, yanıtlar, loglar, dosya adları ve arayüzlerde yalnızca **TIAgent** veya **TIAgent MCP Server** terimini kullanır.
