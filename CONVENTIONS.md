# TIAgent Conventions for AI Agents (Hermes, Aider, AutoGen, CrewAI, LangChain)

## System Architecture
TIAgent is an autonomous bridge communicating with Siemens TIA Portal via Openness API.
Agents can interact with TIAgent through 4 standardized interfaces:

1. **OpenAPI / Swagger**:
   - URL: `http://localhost:19520/swagger.json` or local `openapi.json`
   - LangChain: `agent.load_tools_from_openapi("http://localhost:19520/swagger.json")`
   - AutoGen / CrewAI: Import endpoints as function calling tools.

2. **Model Context Protocol (MCP)**:
   - Command: `python tiagent_mcp.py`
   - Standards-compliant MCP server providing `tiagent_status`, `tiagent_import_package`, `tiagent_send_command`, `tiagent_delete`, `tiagent_search_gsd`.

3. **HTTP REST API**:
   - Base URL: `http://localhost:19520`
   - All responses return JSON with `{ "ok": true/false, "status": { ... } }`.

4. **File Exchange (Hot-Folder)**:
   - Request: `Packages/Inbox/<filename>.json`
   - Response: `Packages/Outbox/<filename>.json.result.json`
