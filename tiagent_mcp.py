#!/usr/bin/env python3
"""
TIAgent Model Context Protocol (MCP) Server
Provides native tool calling for Cursor, GitHub Copilot (VS Code), Cline, Roo Code, Claude Code, and Windsurf.
Communicates with TIAgent on http://localhost:19520.
Uses only Python standard library (no pip packages required).
"""

import sys
import json
import urllib.request
import urllib.error
import urllib.parse

TIAGENT_BASE_URL = "http://localhost:19520"

TOOLS = [
    {
        "name": "tiagent_status",
        "description": "Get current Siemens TIA Portal status: connected project name, PLC CPU name, list of existing DataBlocks (DBs), block and tag counts, import mode, and recent execution steps.",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "tiagent_list_dbs",
        "description": "Get the list of all DataBlocks (DBs) currently existing in the active Siemens TIA Portal project. Call this to inspect DB names before modifying or deleting them.",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "tiagent_get_project",
        "description": "Get complete project overview from Siemens TIA Portal: list of all DataBlocks (DBs), program blocks (OB, FB, FC), PLC tag tables, and PLC tags.",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "tiagent_import_package",
        "description": "Import a complete PLC package into TIA Portal. Supports Ladder (LAD SimaticML XML) or SCL blocks, Global/Instance DBs, PLC tag tables, and PROFINET/GSDML hardware devices.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "PackageName": { "type": "string", "description": "Short identifier for the package" },
                "Title": { "type": "string", "description": "Human-readable package title" },
                "Description": { "type": "string", "description": "Description of package contents" },
                "Language": { "type": "string", "enum": ["LAD", "SCL"], "description": "Primary programming language" },
                "TagTableName": { "type": "string", "description": "Target PLC tag table name (e.g. 'TIAgent_IO_Taglari')" },
                "PlcIpAddress": { "type": "string", "description": "PLC PROFINET IP address (e.g. '192.168.0.1')" },
                "Tags": {
                    "type": "array",
                    "description": "List of PLC tags to create/update",
                    "items": {
                        "type": "object",
                        "properties": {
                            "Name": { "type": "string" },
                            "DataType": { "type": "string" },
                            "LogicalAddress": { "type": "string" },
                            "Comment": { "type": "string" }
                        },
                        "required": ["Name", "DataType", "LogicalAddress"]
                    }
                },
                "DataBlocks": {
                    "type": "array",
                    "description": "List of Global or Instance DBs",
                    "items": {
                        "type": "object",
                        "properties": {
                            "Name": { "type": "string" },
                            "DbType": { "type": "string", "enum": ["GlobalDB", "InstanceDB"] },
                            "Comment": { "type": "string" },
                            "Members": {
                                "type": "array",
                                "items": {
                                    "type": "object",
                                    "properties": {
                                        "Name": { "type": "string" },
                                        "DataType": { "type": "string" },
                                        "StartValue": { "type": "string" },
                                        "Comment": { "type": "string" }
                                    },
                                    "required": ["Name", "DataType"]
                                }
                            }
                        },
                        "required": ["Name", "Members"]
                    }
                },
                "Blocks": {
                    "type": "array",
                    "description": "List of OB, FB, or FC program blocks",
                    "items": {
                        "type": "object",
                        "properties": {
                            "Name": { "type": "string" },
                            "BlockType": { "type": "string", "enum": ["OB", "FB", "FC"] },
                            "BlockNumber": { "type": "integer" },
                            "Language": { "type": "string", "enum": ["LAD", "SCL"] },
                            "Code": { "type": "string" },
                            "SimaticMlXml": { "type": "string" },
                            "Comment": { "type": "string" }
                        },
                        "required": ["Name", "BlockType", "Language"]
                    }
                },
                "PlcModules": {
                    "type": "array",
                    "description": "List of rack I/O and communication expansion modules (SM, SB, CM) to plug into the PLC rack (e.g. '16DI 16DQ', 'SM 1223', 'DI 16/DQ 16 x24VDC ST')",
                    "items": {
                        "type": "object",
                        "properties": {
                            "Name": { "type": "string" },
                            "TypeIdentifier": { "type": "string", "description": "Order number or keyword like '16DI 16DQ', 'SM 1223', '6ES7 223-1BL32-0XB0'" },
                            "Position": { "type": "integer", "description": "Rack slot number (e.g. 2, 3). If omitted, automatically picks first free slot." },
                            "InputStartAddress": { "type": "integer" },
                            "OutputStartAddress": { "type": "integer" }
                        },
                        "required": ["Name"]
                    }
                },
                "HardwareDevices": {
                    "type": "array",
                    "description": "List of PROFINET / GSDML drives and remote I/O devices to add to TIA Portal",
                    "items": {
                        "type": "object",
                        "properties": {
                            "Name": { "type": "string" },
                            "GsdKeyword": { "type": "string" },
                            "IpAddress": { "type": "string" },
                            "SubnetMask": { "type": "string" },
                            "ConnectToPlcIoSystem": { "type": "boolean" },
                            "ReplaceIfExists": { "type": "boolean" },
                            "Modules": { "type": "array", "items": { "type": "object" } }
                        },
                        "required": ["Name"]
                    }
                },
                "AutoApprove": {
                    "type": "boolean",
                    "description": "Set true to automatically apply and compile package without prompting user on UI",
                    "default": True
                }
            },
            "required": ["PackageName"]
        }
    },
    {
        "name": "tiagent_send_command",
        "description": "Send a natural language instruction or prompt to TIA Portal (e.g. 'DB_V90 veri blogunu sil', 'motor devresi ekle').",
        "inputSchema": {
            "type": "object",
            "properties": {
                "Prompt": { "type": "string", "description": "Instruction in Turkish or English" },
                "AutoApprove": { "type": "boolean", "default": True }
            },
            "required": ["Prompt"]
        }
    },
    {
        "name": "tiagent_delete",
        "description": "Delete specific DBs, blocks, tag tables, or hardware devices from the TIA Portal project. Use BlocksToDelete=['*'] to delete all DBs, or specify exact DB names (obtained from tiagent_list_dbs).",
        "inputSchema": {
            "type": "object",
            "properties": {
                "BlocksToDelete": { "type": "array", "items": { "type": "string" }, "description": "Names of OB, FB, FC, or DB blocks to delete. Pass ['*'] to delete all DBs." },
                "TagTablesToDelete": { "type": "array", "items": { "type": "string" }, "description": "Names of tag tables to delete. Pass ['*'] to delete all tag tables." },
                "DevicesToDelete": { "type": "array", "items": { "type": "string" }, "description": "Names of hardware devices to delete" },
                "ClearMainNetworks": { "type": "boolean", "description": "If true, clears all networks inside Main [OB1]" }
            }
        }
    },
    {
        "name": "tiagent_search_gsd",
        "description": "Search installed GSDML files in TIA Portal for drives, inverters, and telegram modules (e.g. 'v90', 'g120', 'telegram 3').",
        "inputSchema": {
            "type": "object",
            "properties": {
                "query": { "type": "string", "description": "Device keyword or search term" }
            },
            "required": ["query"]
        }
    },
    {
        "name": "tiagent_get_hardware",
        "description": "Inspect all hardware devices, PLC racks, expansion slots, and plugged modules in the active TIA Portal project.",
        "inputSchema": {
            "type": "object",
            "properties": {}
        }
    },
    {
        "name": "tiagent_add_module",
        "description": "Add/plug an I/O or communication expansion module (e.g. '16DI 16DQ', 'SM 1223', 'DI 16/DQ 16 x24VDC ST', '6ES7 223-1BL32-0XB0') into the PLC rack.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "Name": { "type": "string", "description": "Module name or identifier (e.g. 'SM1223_DI16_DQ16')" },
                "TypeIdentifier": { "type": "string", "description": "Module type, generic description, or Siemens order number (e.g. '16DI 16DQ', 'DI 16/DQ 16 x24VDC ST', 'SM 1223', '6ES7 223-1BL32-0XB0')" },
                "Position": { "type": "integer", "description": "Rack slot number (e.g. 2, 3, etc.). Omit or set 0 to automatically pick first available slot." },
                "AutoApprove": { "type": "boolean", "default": True }
            },
            "required": ["Name"]
        }
    }
]

def http_get(endpoint):
    url = f"{TIAGENT_BASE_URL}{endpoint}"
    req = urllib.request.Request(url, headers={"User-Agent": "TIAgent-MCP/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data)
    except urllib.error.URLError as e:
        return {"ok": False, "error": f"Failed to connect to TIAgent at {url}: {str(e)}"}

def http_post(endpoint, payload):
    url = f"{TIAGENT_BASE_URL}{endpoint}"
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json", "User-Agent": "TIAgent-MCP/1.0"})
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data)
    except urllib.error.URLError as e:
        return {"ok": False, "error": f"Failed to post to TIAgent at {url}: {str(e)}"}

def handle_tool_call(name, args):
    if name == "tiagent_status":
        return http_get("/api/status")
    elif name == "tiagent_list_dbs":
        return http_get("/api/dbs")
    elif name == "tiagent_get_project":
        return http_get("/api/project")
    elif name == "tiagent_import_package":
        pkg_payload = {
            "Action": "package",
            "AutoApprove": args.get("AutoApprove", True),
            "CustomPackage": args
        }
        return http_post("/api/package", pkg_payload)
    elif name == "tiagent_send_command":
        cmd_payload = {
            "Action": "generate",
            "Prompt": args.get("Prompt", ""),
            "AutoApprove": args.get("AutoApprove", True)
        }
        return http_post("/api/command", cmd_payload)
    elif name == "tiagent_delete":
        del_payload = {
            "Action": "delete",
            "BlocksToDelete": args.get("BlocksToDelete", []),
            "TagTablesToDelete": args.get("TagTablesToDelete", []),
            "DevicesToDelete": args.get("DevicesToDelete", []),
            "ClearMainNetworks": args.get("ClearMainNetworks", False)
        }
        return http_post("/api/delete", del_payload)
    elif name == "tiagent_search_gsd":
        q = urllib.parse.quote(args.get("query", ""))
        return http_get(f"/api/gsd?q={q}")
    elif name == "tiagent_get_hardware":
        return http_get("/api/hardware")
    elif name == "tiagent_add_module":
        mod_name = args.get("Name", "SM_Module")
        type_id = args.get("TypeIdentifier") or args.get("Type") or mod_name
        pos = args.get("Position", 0)
        pkg_payload = {
            "Action": "package",
            "AutoApprove": args.get("AutoApprove", True),
            "CustomPackage": {
                "PackageName": f"AddModule_{mod_name}",
                "PlcModules": [
                    {
                        "Name": mod_name,
                        "TypeIdentifier": type_id,
                        "Position": pos
                    }
                ]
            }
        }
        return http_post("/api/package", pkg_payload)
    else:
        return {"error": f"Unknown tool: {name}"}

def main():
    while True:
        line = sys.stdin.readline()
        if not line:
            break
        line = line.strip()
        if not line:
            continue

        try:
            req = json.loads(line)
        except json.JSONDecodeError:
            continue

        req_id = req.get("id")
        method = req.get("method")
        params = req.get("params", {})

        if method == "initialize":
            res = {
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {
                        "tools": {}
                    },
                    "serverInfo": {
                        "name": "tiagent-mcp",
                        "version": "1.2.0"
                    }
                }
            }
            sys.stdout.write(json.dumps(res) + "\n")
            sys.stdout.flush()

        elif method == "notifications/initialized":
            pass

        elif method == "tools/list":
            res = {
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "tools": TOOLS
                }
            }
            sys.stdout.write(json.dumps(res) + "\n")
            sys.stdout.flush()

        elif method == "tools/call":
            tool_name = params.get("name")
            tool_args = params.get("arguments", {})
            output = handle_tool_call(tool_name, tool_args)
            res = {
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {
                    "content": [
                        {
                            "type": "text",
                            "text": json.dumps(output, ensure_ascii=False, indent=2)
                        }
                    ],
                    "isError": not output.get("ok", True) if isinstance(output, dict) else False
                }
            }
            sys.stdout.write(json.dumps(res) + "\n")
            sys.stdout.flush()

        elif method == "ping":
            res = {"jsonrpc": "2.0", "id": req_id, "result": {}}
            sys.stdout.write(json.dumps(res) + "\n")
            sys.stdout.flush()

if __name__ == "__main__":
    main()
