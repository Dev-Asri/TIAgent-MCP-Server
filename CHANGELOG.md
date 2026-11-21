# Changelog / Değişiklik Günlüğü

All notable changes to the **TIAgent MCP Server** project will be documented in this file.

---

## [Unreleased] - 2026-11-21

### 🇬🇧 English

#### Added
- **3-Stage Safe Package Import Architecture (`SplitIntoStages` & `ImportFullPackage`)**:
  - Automatically mitigates Siemens TIA Portal Openness API crashes and deadlocks when importing large or complex packages (such as 3-axis CNC router projects containing multiple SINAMICS V90 PN servo drives, Telegram 111 submodules, dozens of I/O tags, 11 Global DBs, Know-How-Protected SINA_POS FB284, instance DBs, and multiple LAD FCs).
  - **Stage 1 (Hardware & I/O Tags)**: Imports PROFINET devices (GSDML), rack expansion modules, and PLC I/O tag tables, followed by CPU hardware compilation.
  - **Stage 2 (Global DataBlocks Layer)**: Imports Standard (Non-Optimized) and Optimized Global DBs as native SimaticML GlobalDB XML, strictly preserving initial values (`<StartValue>`) for S7-TCP (Port 102) byte-offset communication.
  - **Stage 3 (Program Blocks & Main [OB1])**: Imports SINA_POS FB284, Instance DBs, process LAD FCs, and updates Main [OB1] before running full PLC software compilation.
  - Introduces explicit garbage collection and Openness COM handle release (`GC.Collect()` / `GC.WaitForPendingFinalizers()`) between stages to prevent memory exhaustion and handle leaks.
- **Large Package Auto-Detection (`IsLargePackage`)**:
  - Automatically identifies whether an incoming package qualifies as large/multi-stage based on hardware content, block count (>= 5), multiple DBs, or protected FBs (e.g., SINA_POS / FB284).
  - Displays a visual safe-import badge and warning banner on the TIAgent WPF approval UI.
- **Standard (Non-Optimized) Memory Layout & `<StartValue>` Support for Global DBs**:
  - Added `MemoryLayout` ("Standard" vs "Optimized") and `BlockNumber` properties to `PlcDbEntry`.
  - Added overloaded `BuildGlobalDbSimaticMlXml` in `TiaOpennessService` to generate valid SimaticML with `<MemoryLayout>Standard</MemoryLayout>` and `<StartValue>` for each member.
- **New REST API Endpoints**:
  - `POST /api/split`: Pre-splits any monolithic package into isolated stages without executing them.
  - `GET /api/stages`: Queries the active status of ongoing multi-stage imports.
- **TIAgent Template Creator Tool (`template_creator.bat`)**:
  - Standalone WPF extraction utility (`TemplateCreator/`) to connect to any open TIA Portal project (V14–V21) and export program blocks (OB, FB, FC, DB), UDT data types, PLC tag tables, hardware configurations, and AI training manifests (`AI_Project_Summary.md` & `AI_Training_Manifest.json`).

#### Changed
- `MainWindow.xaml.cs`: Updated approval countdown and auto-execution header to clearly indicate 3-stage safe import progress.
- Enhanced package validation and documentation rules across `.cursorrules`, `.github/copilot-instructions.md`, `CLAUDE.md`, and `GEMINI.md`.

---

### 🇹🇷 Türkçe

#### Eklendi
- **3 Aşamalı Güvenli Aktarım Mimarisi (`SplitIntoStages` ve `ImportFullPackage`)**:
  - Büyük ve karmaşık otomasyon paketlerinin (örneğin 3 Eksenli CNC G-Code Router projesindeki gibi çoklu SINAMICS V90 PN servo sürücüler, Telegram 111 alt modülleri, PLC I/O tagları, 11 Global DB, Know-How korumalı SINA_POS FB284, Instance DB'ler ve LAD FC blokları) tek seferde monolitik aktarılırken TIA Portal Openness API'sinin çökmesini (crash) veya kilitlenmesini önler.
  - **Aşama 1 (Donanım ve I/O Tagları)**: PROFINET saha cihazları (GSDML), modüller ve PLC I/O tag tabloları aktarılır ve CPU donanım derlemesi yapılır.
  - **Aşama 2 (Global Veri Blokları)**: S7-TCP (Port 102) bayt ofset haberleşmesi için Standard (Non-Optimized) ve Optimized Global DB'ler, `<StartValue>` başlangıç değerleri korunarak doğrudan SimaticML GlobalDB XML formatında aktarılır.
  - **Aşama 3 (Program Blokları ve Main [OB1])**: SINA_POS FB284, Instance DB'ler, LAD FC blokları ve Main [OB1] aktarılır; ardından tüm PLC yazılımı nihai olarak derlenir.
  - Aşamalar arasında Openness COM tutamaçları ve bellek sızıntılarını önlemek için zorunlu GC (`GC.Collect()`) temizliği uygulanır.
- **Büyük Paket Otomatik Tespiti (`IsLargePackage`)**:
  - Donanım, korumalı bloklar veya yüksek DB/blok sayısı içeren paketleri otomatik algılar ve TIAgent onay ekranında "⚡ Güvenli Aktarım" uyarısı verir.
- **Standard (Non-Optimized) Global DB & `<StartValue>` Desteği**:
  - `PlcDbEntry` modeline `MemoryLayout` ve `BlockNumber` eklendi.
  - `BuildGlobalDbSimaticMlXml` metodu güncellenerek Standard bellek yerleşimi ve başlangıç değerleri tam SimaticML uyumlu hale getirildi.
- **Yeni Köprü REST API Uç Noktaları**:
  - `POST /api/split`: Büyük paketleri güvenli 3 aşamaya böler.
  - `GET /api/stages`: Çok aşamalı aktarımların anlık durumunu döndürür.
- **Otomatik Şablon Çıkarıcı: TIAgent Template Creator (`template_creator.bat`)**:
  - Açık TIA Portal projelerinden tüm blokları, UDT'leri, tagları ve donanımı dışa aktararak yapay zekâ eğitimi için manifest hazırlayan bağımsız masaüstü aracı.
