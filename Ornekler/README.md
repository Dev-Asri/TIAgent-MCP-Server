# TIA Portal Kullanıcı Kod Şablonları, Eğitimleri & Kuralları (Ornekler)

Bu klasör, TIA Portal projeleri için standart kod şablonlarını, eğitim dokümanlarını, SimaticML kurallarını ve otomatik doğrulama parametrelerini içerir.

### Eğitim & Kural Dosyaları:
- **[Egitim_Norm_Scale_Degisken_Tipleri.md](Egitim_Norm_Scale_Degisken_Tipleri.md):** `NORM_X` ve `SCALE_X` bloklarında `Real` vs `Int` veri tipi kuralları, 16-bit taşma ve `ENO=False` çözümü.
- **[Paket_Dogrulama_Kurallari.json](Paket_Dogrulama_Kurallari.json):** TIAgent MCP Server'ın gelen her paketi denetlerken başvurduğu makine tarafından okunabilir doğrulama ve otomatik düzeltme kuralları.
- **[SimaticML_Ve_Paket_Tecrubeleri.md](SimaticML_Ve_Paket_Tecrubeleri.md):** TIA Portal Openness SimaticML derleyici kısıtlamaları, pin/wire bağlantıları ve tecrübe havuzu.
- **[Tup_Kalinlik_Olcum_LAD_Calisan_Paket.json](Tup_Kalinlik_Olcum_LAD_Calisan_Paket.json):** 30 Network'lük çalışan referans kalibrasyon ve tüp ölçüm paketi.
- **[Piston_Basinc_Testi_LAD_Calisan_Paket.json](Piston_Basinc_Testi_LAD_Calisan_Paket.json):** Çalışan basınç testi ve piston otomasyon paketi.

### Buraya Neleri Ekleyebilirsiniz?
- **SimaticML XML Dosyaları (`*.xml`):** TIA Portal'dan export edilmiş OB, FB, FC, DB veya UDT dosyaları.
- **SCL Kaynak Dosyaları (`*.scl`):** Yazdığınız SCL algoritmaları, hesaplama fonksiyonları veya veri bloğu şablonları.
- **PLC Tag Listeleri (`*.xml`, `*.xlsx`):** Standart tag isimlendirme formatlarınız.

### TIAgent Bu Dosyaları Nasıl Kullanır?
1. **Otomatik Paket Doğrulama (Pre-Flight Check):** TIAgent MCP Server, bir paket geldiğinde `Paket_Dogrulama_Kurallari.json` ve eğitim kurallarına bakarak sorunlu/hatalı paketlerin PLC'ye aktarılmasını önler; değişken tiplerini ve SimaticML etiketlerini otomatik olarak düzeltir.
2. **Yapay Zekâ Eğitimi:** AI modelleri kod üretmeden önce bu kurallara uyarak sıfır hatalı paketler oluşturur.
