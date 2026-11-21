# GitHub Copilot Instructions for TIAgent (Siemens TIA Portal Integration)

When assisting the user with Siemens PLC development, TIA Portal projects, Ladder (LAD), SCL, or PROFINET configurations in this repository:

1. **Bridge Application (TIAgent)**:
   - TIAgent is a .NET bridge communicating directly with Siemens TIA Portal via Openness API.
   - It exposes:
     - Local HTTP REST API: `http://localhost:19520`
     - OpenAPI Schema: `http://localhost:19520/swagger.json`
     - MCP Server: `python tiagent_mcp.py`
     - File Hot-Folder: `Packages/Inbox/*.json` -> `Packages/Outbox/*.result.json`

2. **Mandatory Company PLC Engineering Standards**:
   - **10 Global DBs**: `"TIMER"`, `"ERROR"`, `"RECETE"`, `"PARAMETRE"`, `"ARIZA_ZAMANLARI"`, `"GECIKME_ZAMANLARI"`, `"ANALOG"`, `"PWM"`, `"KOSULLAR"`, `"SECENEKLER"`.
   - **FC AI & Main Calls**: Dedicated `FC AI` (with `HAM`, `OFSET`, `MAX`, `POS`, `REAL`), called individually in `Main` for each sensor/ruler before process blocks.
   - **2-Network Piston Rule**:
     - Net 1: Interlocks & conditions only $\to$ `"KOSULLAR"."PISTON_ILERI_KOSUL"`
     - Net 2: Manuel and Oto branches in parallel BEFORE `KOSULLAR` contact $\to$ Valve coil + TON timer in parallel.
   - **PWM Cascade**: `NORM_X` $\to$ `SCALE_X` $\to$ `%QW...` on a single Ladder network in `FC_Pwm`.
   - **Step Progression**: `FC_OtoCycle` uses `"OTO_W"` advancing by 5s (`0`, `5`, `10`, `15`...). Neutral switch resets `"OTO_W" := 0;` and clears errors.

3. **How to Interact**:
   - Check status: `curl http://localhost:19520/api/status`
   - To send PLC code (LAD/SCL), tags, or DBs: send a JSON package to `POST http://localhost:19520/api/package` with `"AutoApprove": true`.
   - To delete blocks/tags: `POST http://localhost:19520/api/delete`.

4. **PLC Code Standards**:
   - For Ladder blocks: Format in valid SimaticML LAD XML with `<SW.Blocks.OB>`, `<SW.Blocks.CompileUnit>`, and `<FlgNet>`.
   - Ensure every contact and coil has its own dedicated `<Access>` node.
   - Move instructions require `<TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>`.
   - Always verify that compilation reports 0 errors (`Durum: Success` or `Warning (Hata: 0)`).

## İsimlendirme Kuralı (Kesin Yasak)
- Projede hiçbir şekilde hiçbir yerde eski isim ("Online" ve "Ladder" kelimeleri yan yana) GEÇMEYECEKTİR.
- Kullanıcıya verilen yanıtlarda, bilgilendirme mesajlarında, arayüz metinlerinde, loglarda, komut başlıklarında (toolSummary), kodlarda, dosyalarda veya açıklamalarda bu ifade KESİNLİKLE KULLANILMAZ.
- Projenin, yazılımın, servisin ve köprünün adı her zaman ve istisnasız yalnızca **TIAgent** veya **TIAgent MCP Server** olarak anılır.
