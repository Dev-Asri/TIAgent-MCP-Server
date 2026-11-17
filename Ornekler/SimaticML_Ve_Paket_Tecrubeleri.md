# Siemens TIA Portal & SimaticML Paket Hazırlama Tecrübeleri ve Kuralları

Bu kılavuz, TIAgent üzerinden TIA Portal'a paket (LAD / SCL / DB / Tag) gönderilirken karşılaşılan hatalardan, derleyici kısıtlamalarından ve kazanılan tecrübelerden derlenmiştir.

> [!IMPORTANT]
> **YENİ BİR PAKET VEYA PROJE HAZIRLAMADAN ÖNCE:**
> Her paket hazırlığı öncesinde bu dokümandaki kurallar kontrol edilmeli ve paket doğrudan bu kurallara uygun olarak hatasız inşa edilmelidir.

---

## 1. SimaticML Çok Dilli Metin ve Proje Kültürü (`<Culture>`)

### Karşılaşılan Hata:
```text
Cannot import multilingual text with culture 'tr-TR' at line number ... at line position ...:
the specified culture does not exist within the current project.
```

### Kök Neden:
TIA Portal projelerinde "Languages & Resources" altında projenin varsayılan düzenleme dilleri bulunur (çoğunlukla `en-US` veya `de-DE`). Projede `tr-TR` dili tanımlı/etkin değilse Openness XML içindeki `tr-TR` etiketlerini reddeder.

### Çözüm & Standart Kural:
- SimaticML içindeki tüm `<MultilingualTextItem>` ve `<AttributeList>` bloklarında kültür etiketini **kesinlikle `en-US`** olarak belirleyin:
  ```xml
  <AttributeList>
    <Culture>en-US</Culture>
    <Text>Network Başlığı veya Açıklaması</Text>
  </AttributeList>
  ```

---

## 2. LAD SimaticML Komut ve Parça İsimleri (`<Part Name="...">`)

### Karşılaşılan Hatalar:
```text
An instruction with the name 'NORM_X' cannot be found at the object with UID '...'
An instruction with the name 'Norm_X' cannot be found at the object with UID '...'
An instruction with the name 'Scale' cannot be found at the object with UID '...'
```

### Kök Neden:
TIA Portal LAD SimaticML şemasında komut kutularının (`Part`) XML `Name` öznitelikleri, ekranda görünen kısaltmalardan farklıdır. Örneğin SCL'deki `NORM_X()` fonksiyonu LAD SimaticML'de `NORM_X` veya `Norm_X` değil, **`Normalize`** adını taşır.

### Çözüm & Doğrulanmış Komut Eşleme Tablosu:

| Komut / İşlem | SCL Gösterimi | SimaticML LAD `<Part Name="...">` | Şablon / Parametreler |
|---|---|---|---|
| **Normalize** | `NORM_X(...)` | `Normalize` | `<TemplateValue Name="SrcType" Type="Type">Int/Real</TemplateValue>`<br/>`<TemplateValue Name="DestType" Type="Type">Real</TemplateValue>` |
| **Ölçekleme** | `SCALE_X(...)` | `Scale_X` | `<TemplateValue Name="SrcType" Type="Type">Real</TemplateValue>`<br/>`<TemplateValue Name="DestType" Type="Type">Int/Real</TemplateValue>` |
| **Taşıma (Move)** | `:=` | `Move` | `<TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>` |
| **Zamanlayıcı (TON)** | `TON(...)` | `TON` | `Version="1.0"`<br/>`<Instance Scope="GlobalVariable">...` |
| **Eşittir** | `==` veya `=` | `Eq` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Eşit Değildir** | `<>` | `Ne` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Küçüktür** | `<` | `Lt` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Küçük Eşittir** | `<=` | `Le` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Büyüktür** | `>` | `Gt` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Büyük Eşittir** | `>=` | `Ge` | `<TemplateValue Name="SrcType" Type="Type">Int/Real/DInt</TemplateValue>` |
| **Veya (OR)** | `OR` | `O` | `<TemplateValue Name="Card" Type="Cardinality">2</TemplateValue>` |
| **Ve (AND)** | `AND` | Seri kontak bağlantısı (Doğrudan tel / Wire) |
| **Normal Kontak (NO)** | Contact | `Contact` | Giriş (`in`) ve Çıkış (`out`) telleri |
| **Ters Kontak (NC)** | Negated Contact | `Contact` | `<Negated Name="operand" />` |
| **Normal Bobin** | Coil | `Coil` | Giriş (`in`) ve İşlenen (`operand`) telleri |
| **Set Bobini** | `S` Coil | `SCoil` | Giriş (`in`) ve İşlenen (`operand`) telleri |
| **Reset Bobini** | `R` Coil | `RCoil` | Giriş (`in`) ve İşlenen (`operand`) telleri |

---

## 3. XML Karakter Kaçışları (Escaping)

### Karşılaşılan Hata:
```text
Invalid XML encountered while reading SimaticML: Name cannot begin with the '=' character (0x3D)
```

### Kök Neden:
Network başlıklarında veya açıklamalarında yer alan `<`, `>`, `<=`, `>=`, `&`, `"` gibi karakterlerin doğrudan XML içine basılması XML parser hatasına yol açar.

### Çözüm & Standart Kural:
- SimaticML metin alanlarına gidecek tüm başlık ve açıklamalar mutlaka XML-escape edilmelidir:
  - `<` yerine `&lt;`
  - `>` yerine `&gt;`
  - `&` yerine `&amp;`
  - `"` yerine `&quot;`
- C# tarafında: `System.Security.SecurityElement.Escape(text)`
- PowerShell tarafında: `[System.Security.SecurityElement]::Escape($text)`

---

## 4. PLC Tag Adresleme & Siemens Veri Tipi Uyumu

### Karşılaşılan Hata:
```text
PLC Yazılım Derlemesi: Durum: Error (Hata: 3, Uyarı: 28)
```

### Kök Neden:
Analog giriş/çıkış veya tamsayı (`Int`, `Word`) tagları `%I0.0` gibi bir bit adresine tanımlanırsa veya tag modeli JSON deserialization sırasında `Address` yerine `LogicalAddress` beklediğinde varsayılan `%I0.0` adresine düşerse, Siemens derleyicisi **veri tipi ile mantıksal adres uyuşmazlığı** nedeniyle hata üretir.

### Çözüm & Standart Kural:
1. **Adresleme Standartları:**
   - **Dijital Girişler (Bool):** `%I0.0` .. `%I0.7`, `%I1.0` vb.
   - **Dijital Çıkışlar (Bool):** `%Q0.0` .. `%Q0.7`, `%Q1.0` vb.
   - **Analog Girişler (Int / 0..27648):** `%IW64`, `%IW66`, `%IW68` vb. (Kesinlikle bit adresi değil, Word adresi!).
   - **Analog / PWM Çıkışları (Int / 0..27648):** `%QW1000`, `%QW64` vb.
   - **Dahili Bellek (Bool / Word):** `%M0.0`, `%MW10` vb.
2. **Model Eşleşmesi:**
   - `PlcTagEntry` sınıfında hem `LogicalAddress` hem de `Address` özellikleri tanımlanmıştır. JSON paketinde her ikisi de kullanılabilir.
3. **Openness Atomik Tag Güncellemesi:**
   - Siemens Openness API'sinde var olan bir tag'ın hem tipi hem de adresi değiştiğinde (örneğin `%I0.0 Bool`'dan `%IW64 Int`'e), arka arkaya `SetValue` çağrısı istisna fırlatır (çünkü `%I0.0` geçici olarak `Int` olamaz).
   - Bu nedenle `TiaOpennessService.EnsurePlcTagsCreatedInternal` metodu, tip veya adresi değişen eski tag'ı önce siler, ardından `Create(name, dataType, address)` ile atomik olarak temiz bir şekilde yeniden üretir.

---

## 5. Global Veri Blokları (DB) ve IEC Zamanlayıcılar

### Kurallar:
1. **Zamanlayıcı Değişkenleri:**
   - Global DB içinde TON/TOF zamanlayıcı instance'ları için `DataType = "IEC_TIMER"` tanımlanmalıdır.
2. **Bağımlılık Sırası:**
   - Önce PLC Tag tablosu oluşturulmalıdır.
   - Ardından Global Veri Blokları (`DB`) oluşturulmalı ve derlenmelidir (`CompileBlock`).
   - Son olarak bu DB ve Tag'ları referans alan `Main [OB1]` veya diğer FB/FC blokları içe aktarılmalıdır.

---

## 6. Paket Hazırlama Kontrol Listesi (Pre-Flight Checklist)

Yeni bir paket oluştururken veya gönderirken şu adımları sırayla doğrula:

- [ ] **Kültür:** Tüm `<Culture>` etiketleri `en-US` mi?
- [ ] **Komut İsimleri:** `NORM_X` için `Normalize`, `SCALE_X` için `Scale_X` kullanıldı mı?
- [ ] **Karakter Kaçışı:** Başlık ve açıklamalardaki `<`, `>`, `&` karakterleri escape edildi mi?
- [ ] **Tag Adresleri:** Analog giriş/çıkışlar `%IW...` ve `%QW...` şeklinde Word olarak verildi mi?
- [ ] **DB Değişkenleri:** Timer değişkenleri `IEC_TIMER`, süreler `Time`, analog değerler `Real` veya `Int` mi?
- [ ] **OB1 Wires:** Her `<Part>` giriş/çıkışı geçerli bir `<Wire>` ve `<IdentCon>` / `<NameCon>` ile bağlı mı?
