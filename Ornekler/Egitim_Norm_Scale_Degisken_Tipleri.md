# TIA Portal Eğitim Rehberi: NORM_X, SCALE_X ve Değişken Tipi Kuralları

Bu doküman, Siemens S7-1200 / S7-1500 PLC sistemlerinde analog ve PWM sinyal işleme (`NORM_X` ve `SCALE_X`) sırasında yapılan kritik değişken tipi hatalarını, bunların matematiksel sebeplerini ve TIAgent tarafından uygulanan doğrulama kurallarını açıklamaktadır.

---

## 1. Problem Tanımı ve Belirtiler

### Karşılaşılan Durum:
Bir DC motor hız kontrolü veya analog vana kontrolü devresinde, operatör veya AI `0 - 49` (veya `0 - 100`) arasında bir hız/ayar değeri girdiğinde, PLC'den veya PWM çıkışından (`%QW...`) hiçbir sinyal alınamamaktadır (`Çıkış = 0`).

```text
Giriş: 49  ===> [ NORM_X / SCALE_X ] ===> Çıkış (%QW1000): 0  (Sinyal Yok!)
```

---

## 2. Kök Neden: Matematiksel Aritmetik Taşma (Overflow) ve `ENO=False`

Siemens TIA Portal'da `NORM_X` ve `SCALE_X` blokları iki aşamalı bir matematiksel dönüşüm yapar:

### Aşama 1: Normalize Etme (`NORM_X`)
$$\text{NORM\_X Çıkışı} = \frac{\text{Giriş} - \text{MIN}}{\text{MAX} - \text{MIN}}$$

- Giriş: `0.0 .. 100.0` (Yüzde veya mühendislik birimi)
- Çıkış (`*_Norm`): Kesinlikle **`0.0` ile `1.0` arasında bir oran** olmalıdır.
- **Zorunlu Veri Tipi: `Real`** (Kayan noktalı sayı).

### Aşama 2: Yeniden Ölçekleme (`SCALE_X`)
$$\text{SCALE\_X Çıkışı} = \text{MIN} + (\text{Normalize Giriş} \times (\text{MAX} - \text{MIN}))$$

- Normalize Giriş: `0.0 .. 1.0` (`Real`)
- Hedef Sınırlar: `MIN = 0`, `MAX = 27648` (Siemens standart analog/PWM tam skalası)
- Çıkış (`%QW...`): `Int` (`0 .. 27648`)

### Hatanın Mekanizması:
Eğer `*_Norm` değişkeni `Real` yerine **`Int` (Tamsayı)** yapılırsa veya bu değişkene doğrudan `49` gibi bir tamsayı değeri girilirse:

$$\text{SCALE\_X Çıkışı} = 0 + (49 \times 27648) = 1.354.752$$

> [!CAUTION]
> **Aritmetik Taşma (16-bit Signed Integer Overflow):**
> Siemens PLC'lerde standart `Int` (ve analog çıkış Word'ü `%QW`) **16-bit işaretli tamsayıdır**.
> - En büyük pozitif `Int` değeri: **$+32.767$**
> - Hesaplanmak istenen değer: **$1.354.752$**
> 
> $1.354.752 > 32.767$ olduğu için Siemens derleyicisi ve CPU çalışma zamanı **aritmetik taşma** tespit eder.
> Bloğun **`ENO` (Enable Out) bayrağı anında `False`** olur.
> `ENO` `False` olduğunda çıkış bobini veya taşıma komutu devreden çıkar, `%QW1000` donanım çıkışına hiçbir değer aktarılamaz!

---

## 3. Doğru Değişken Mimarisi

Hatasız bir analog/PWM sürücü devresinde değişkenlerin tipleri ve aralıkları şu şekilde yapılandırılmalıdır:

| Değişken Adı | Görevi | Veri Tipi | Değer Aralığı | Örnek |
|---|---|---|---|---|
| `DC_Motor_Hiz_Yuzde` | Operatör / HMI Hız Girişi | `Real` | `0.0 .. 100.0` | `50.0 %` |
| `DC_Motor_Hiz_Norm` | Ara Normalize Oran Değeri | **`Real` (ZORUNLU)** | **`0.0 .. 1.0`** | `0.50` |
| `QW_DC_Motor_PWM` | Fiziksel PLC Donanım Çıkışı | `Int` | `0 .. 27648` | `13824` (%50 PWM) |

### SimatikML / LAD Akış Şeması:
```text
[DC_Motor_Hiz_Yuzde] (0.0 .. 100.0 Real)
       |
       v
 +-----------+
 | Normalize |
 |  (NORM_X) |
 | MIN:  0.0 |
 | MAX:100.0 |
 +-----+-----+
       |
       v
 [DC_Motor_Hiz_Norm] (0.0 .. 1.0 Real)  <--- ASLA INT OLAMAZ!
       |
       v
 +-----------+
 |  Scale_X  |
 |  MIN:   0 |
 |  MAX:27648|
 +-----+-----+
       |
       v
 [QW_DC_Motor_PWM] (%QW1000 Int)
```

---

## 4. TIAgent Otomatik Paket Doğrulayıcısı (Pre-Flight Validator)

TIAgent MCP Server, gelen veya üretilen her paketi PLC'ye göndermeden önce `Ornekler/Paket_Dogrulama_Kurallari.json` kurallarına göre denetler:

1. **`*Norm*` İsimli Değişkenlerin Kontrolü:**
   - DB içinde veya PLC Tag tablosunda `Norm` ifadesi geçen (örn: `DC_Motor_Hiz_Norm`, `Basinc_Norm`) bir değişken `Int` veya `Word` olarak tanımlanmışsa, TIAgent bunu tespit eder.
   - Paketi reddetmek yerine güvenli bir şekilde tipini otomatik olarak **`Real`** yapar ve loglara şu kaydı düşer:
     `[DÜZELTME] '{varName}' tipi 'Int' yerine 'Real' olarak otomatik düzeltildi (NORM_X/SCALE_X taşma koruması).`
2. **SimaticML Kültür Düzeltmesi:**
   - `<Culture>tr-TR</Culture>` görüldüğünde otomatik olarak `<Culture>en-US</Culture>` yapılır.
3. **SimaticML Komut İsimleri:**
   - `NORM_X` ve `Norm_X` -> `Normalize`
   - `Scale` ve `SCALE_X` -> `Scale_X`
4. **TON Zamanlayıcı `ET` Pini:**
   - Boşta kalan `ET` pinlerine otomatik `<OpenCon />` eklenir.
5. **Çıkarma (`Sub`) Komutu:**
   - Geçersiz `<TemplateValue Name="Card">` parametresi temizlenir.
