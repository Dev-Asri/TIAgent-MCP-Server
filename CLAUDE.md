# CLAUDE.md / OpenCode Guide for TIAgent

## Overview
This repository contains **TIAgent**, a local Openness bridge for Siemens TIA Portal (V14 SP1 to V21).
It enables AI agents to create PLC Tag tables, Global/Instance Data Blocks, write Ladder (SimaticML XML) and SCL blocks, configure PROFINET hardware drives (e.g. SINAMICS V90, G120), and compile projects with 0 errors.

## Primary Communication Interfaces

### 1. Model Context Protocol (MCP)
Run the built-in MCP server:
```bash
python tiagent_mcp.py
```
Available tools:
- `tiagent_status`: Inspect TIA Portal connection, DBs, blocks, and compile logs.
- `tiagent_list_dbs`: List all DataBlocks (DBs) in the active project.
- `tiagent_get_project`: Get complete project overview (DBs, blocks, tag tables, tags).
- `tiagent_get_hardware`: Inspect all hardware devices, PLC racks, expansion slots, and plugged modules.
- `tiagent_add_module`: Plug an I/O or communication module (e.g. '16DI 16DQ', 'SM 1223', 'DI 16/DQ 16 x24VDC ST') into the PLC rack.
- `tiagent_import_package`: Import full package (LAD/SCL blocks, DBs, tags, hardware devices/modules).
- `tiagent_send_command`: Send natural language commands to TIA Portal (e.g. 'PLC ye 16I 16Q modul ekle', 'DB_V90 sil').
- `tiagent_delete`: Delete blocks, tags, hardware (pass `BlocksToDelete=['*']` to delete all DBs).
- `tiagent_search_gsd`: Search GSDML catalog for drives and telegram modules.

### 2. HTTP REST Endpoints (Port 19520)
- `GET http://localhost:19520/api/status` (Connection, DB list, block/tag counts)
- `GET http://localhost:19520/api/dbs` (List DataBlocks)
- `GET http://localhost:19520/api/project` (Complete project tree)
- `GET http://localhost:19520/api/hardware` (Hardware devices and rack modules)
- `POST http://localhost:19520/api/package` (Package import)
- `POST http://localhost:19520/api/command` (Natural language prompt e.g. "PLC ye 16I 16Q modul ekle")
- `POST http://localhost:19520/api/delete` (Delete blocks/tags/DBs)
- `GET http://localhost:19520/swagger.json` (OpenAPI specification)

### 3. File Hot-Folder (`Packages/Inbox` and `Packages/Outbox`)
For sandboxed or offline LLMs:
- Place package file in `Packages/Inbox/<id>.json`
- TIAgent automatically executes it and writes response to `Packages/Outbox/<id>.json.result.json`

## CLI Wrapper
You can execute terminal commands using:
```powershell
.\tiagent.ps1 status
.\tiagent.ps1 send <package.json>
.\tiagent.ps1 compile
```
