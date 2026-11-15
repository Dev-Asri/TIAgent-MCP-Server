# GitHub Copilot Instructions for TIAgent (Siemens TIA Portal Integration)

When assisting the user with Siemens PLC development, TIA Portal projects, Ladder (LAD), SCL, or PROFINET configurations in this repository:

1. **Bridge Application (TIAgent)**:
   - TIAgent is a .NET bridge communicating directly with Siemens TIA Portal via Openness API.
   - It exposes:
     - Local HTTP REST API: `http://localhost:19520`
     - OpenAPI Schema: `http://localhost:19520/swagger.json`
     - MCP Server: `python tiagent_mcp.py`
     - File Hot-Folder: `Packages/Inbox/*.json` -> `Packages/Outbox/*.result.json`

2. **How to Interact**:
   - Check status: `curl http://localhost:19520/api/status` or `Invoke-RestMethod -Uri "http://localhost:19520/api/status"`
   - To send PLC code (LAD/SCL), tags, or DBs: send a JSON package to `POST http://localhost:19520/api/package`. Always include `"AutoApprove": true` for autonomous operation.
   - To delete blocks/tags: `POST http://localhost:19520/api/delete`.

3. **PLC Code Standards**:
   - For Ladder blocks: Format in valid SimaticML LAD XML with `<SW.Blocks.OB>`, `<SW.Blocks.CompileUnit>`, and `<FlgNet>`.
   - Ensure every contact and coil has its own dedicated `<Access>` node.
   - Move instructions require `<TemplateValue Name="Card" Type="Cardinality">1</TemplateValue>`.
   - Always verify that compilation reports 0 errors (`Durum: Success` or `Warning (Hata: 0)`).
