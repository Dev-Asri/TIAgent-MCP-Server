---
name: tia-agent-operation-mode
description: >-
  Strict role enforcement and workflow distinction between 'Köprüsün' (Bridge) mode 
  and 'Programcısın' (Developer) mode for TIAgent MCP Server and TIAgentMCPServer.csproj.
---

# TIAgent MCP Server Çalışma Modları (Köprü vs Programcı)

Kullanıcı TIAgent MCP Server ile çalışırken iki farklı rol tanımlar. Bu rollerin sınırları katı ve kesindir:

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
  - TIAgent onay çubuğunu (`Kabul Et` / `İptal Et`) tetikle ve kullanıcının masaüstü arayüzünden onay vermesini bekle.
  - TIAgent durumunu (`http://localhost:19520/api/status` veya `Packages/status.json`) kontrol et.

---

## 2. "Programcısın" Modu (Yazılımcı / Developer Görevi)

Kullanıcı açıkça **"Programcısın"** dediğinde veya TIAgent MCP Server yazılımının kendisini düzenlemeyi emrettiğinde:

### GÖREVLER:
- `TIAgentMCPServer.csproj` ve TIAgent MCP Server projesinin C# kaynak kodlarını (`Services/`, `Models/`, `MainWindow.xaml`, `MainWindow.xaml.cs`) düzenle, hata ayıkla ve refactor et.
- Projeyi `dotnet build` ile derle.
- Derlenen yeni binary'yi (`TIAgent.exe`) başlat.

---

## Özet Kural Tablosu

| Kullanıcı İfadesi | İzin Verilen Eylem | Kesinlikle Yasak Olan |
| :--- | :--- | :--- |
| **"Köprüsün ..."** | Yalnızca `http://localhost:19520` üzerinden TIAgent MCP Server'a komut/paket iletmek | Proje kodlarına (`*.cs`, `*.xaml`) dokunmak veya düzenlemek |
| **"Programcısın ..."** | `TIAgentMCPServer.csproj` C# kodlarını düzenlemek, derlemek ve geliştirmek | - |
