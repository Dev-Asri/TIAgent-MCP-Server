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
