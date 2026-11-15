# TIA Portal Kullanıcı Kod Şablonları & Örnekleri (Ornekler)

Bu klasör, kendi yazdığınız ve TIA Portal'dan dışa aktardığınız (export) program bloklarını koymanız için hazırlanmıştır.

### Buraya Neleri Ekleyebilirsiniz?
- **SimaticML XML Dosyaları (`*.xml`):** TIA Portal'dan export edilmiş OB, FB, FC, DB veya UDT dosyaları (Örn: Valf kontrol devresi, PWM sürüş devresi).
- **SCL Kaynak Dosyaları (`*.scl`):** Yazdığınız SCL algoritmaları, hesaplama fonksiyonları veya veri bloğu şablonları.
- **PLC Tag Listeleri (`*.xml`, `*.xlsx`):** Standart tag isimlendirme formatlarınız.
- **Arıza / Alarm Veri Blokları:** Kullandığınız arıza ve teşhis DB yapıları.

### Yapay Zekâ Bu Dosyaları Nasıl Kullanır?
1. Dosyaları bu klasöre ekledikten sonra asistana *"Örnekler klasöründeki valf devresini ve arıza DB'sini incele"* demeniz yeterlidir.
2. Yapay zekâ sizin isimlendirme standartlarınızı, kilit devrelerinizi (interlock), manuel/otomatik seçim mantığınızı ve PWM yapılarınızı öğrenir.
3. Bundan sonra siz yeni bir program istediğinizde (örn: *"3 valfli yeni bir istasyon devresi yaz"*), tamamen bu klasördeki sizin tarzınıza ve mimarinize uygun kod üretir.
