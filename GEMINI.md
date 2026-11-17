# Antigravity Proje Kuralları (GEMINI.md)

## Rol Ayrımı Kuralı (Köprü vs Programcı)

Kullanıcının talimatında belirttiği role göre hareket edilir:

### 1. "Köprüsün" Modu:
- Kullanıcı **"Köprüsün"** dediğinde `TIAgentMCPServer.csproj` veya TIAgent MCP Server C# kaynak kodlarında (`*.cs`, `*.xaml`, vb.) **KESİNLİKLE HİÇBİR DÜZENLEME YAPILMAZ**.
- Kod dosyaları gereksiz yere okunmaz veya incelenmez.
- Yalnızca TIAgent MCP Server'a komut göndermek için Köprü API'si (`http://localhost:19520` veya `Packages/Inbox`) kullanılır.
- Kullanıcının TIA Portal projesine ilişkin tüm istekleri (devre üretimi, tag, DB, silme işlemleri) köprü üzerinden TIAgent onay çubuğuna iletilir.

### 2. "Programcısın" Modu:
- Kullanıcı **"Programcısın"** dediğinde TIAgent MCP Server yazılımının kendisinde (`TIAgentMCPServer.csproj`) kod düzenlemesi, hata düzeltmesi veya mimari değişiklikler yapılabilir.
- Yapılan değişiklikler `dotnet build` ile derlenir ve uygulama yeniden başlatılır.

## Tecrübe Havuzu & Hatasız Paket Hazırlama Kuralı (Ornekler/ Klasörü)

1. **Önce Tecrübeyi Kontrol Et:**
   - Yeni bir devre, blok, DB, tag veya paket hazırlamadan önce **MUTLAKA** `Ornekler/SimaticML_Ve_Paket_Tecrubeleri.md` ve `Ornekler/` klasöründeki çalışan şablonlar kontrol edilir.
   - Paket; kültür (`en-US`), komut ismi (`NORM_X` için `Normalize`, `SCALE_X` için `Scale_X`), XML karakter kaçışları (`&lt;`, `&gt;`), analog word adresleme (`%IW64`, `%QW1000`), TON `ET` pini `OpenCon` bağlantısı, normalize oran değişkenlerinin (`*_Norm`) **kesinlikle `Real`** yapılması (asla `Int` olmamalı, yoksa `SCALE_X`'te aritmetik taşma ve `ENO=False` ile çıkış kaybı yaşanır) ve IEC zamanlayıcı standartlarına önceden tam uyumlu şekilde hazırlanır.

2. **Hata ve Düzeltmeleri Tecrübeye Kaydet:**
   - Paket gönderiminde veya TIA proje derlemesinde herhangi bir hata ile karşılaşıldığında ve bu hata çözüldüğünde; hatanın sebebi, çözümü ve kuralı anında `Ornekler/SimaticML_Ve_Paket_Tecrubeleri.md` dosyasına eklenerek güncellenir ve kalıcı tecrübe olarak saklanır.

## İsimlendirme Kuralı (Kesin Yasak)
- Projede hiçbir şekilde hiçbir yerde eski isim ("Online" ve "Ladder" kelimeleri yan yana) GEÇMEYECEKTİR.
- Kullanıcıya verilen yanıtlarda, bilgilendirme mesajlarında, arayüz metinlerinde, loglarda, komut başlıklarında (toolSummary), kodlarda, dosyalarda veya açıklamalarda bu ifade KESİNLİKLE KULLANILMAZ.
- Projenin, yazılımın, servisin ve köprünün adı her zaman ve istisnasız yalnızca **TIAgent** veya **TIAgent MCP Server** olarak anılır.
