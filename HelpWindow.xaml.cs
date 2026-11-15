using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace TIAgent
{
    public partial class HelpWindow : Window
    {
        private string _language = "tr";

        public HelpWindow(string language = "tr")
        {
            InitializeComponent();
            _language = language ?? "tr";
            ApplyLanguage(_language);
        }

        public void ApplyLanguage(string language)
        {
            _language = language ?? "tr";
            bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);

            Title = isEn
                ? "TIAgent MCP Server — asriakdeniz@gmail.com — Help & Protocols"
                : "TIAgent MCP Server — asriakdeniz@gmail.com — Yardım & Protokoller";

            if (TxtHeaderSubtitle != null)
            {
                TxtHeaderSubtitle.Text = isEn
                    ? "Multi-Agent Protocols (MCP, OpenAPI/Swagger, CLI, Hot-Folder), Openness Architecture & Guide (V14 SP1 – V21)"
                    : "Çoklu Ajan Protokolleri (MCP, OpenAPI/Swagger, CLI, Hot-Folder), Openness Mimarisi ve Kılavuz (V14 SP1 – V21)";
            }

            if (TxtFooterInfo != null)
            {
                TxtFooterInfo.Text = isEn
                    ? "Developer: Asri Akdeniz · asriakdeniz@gmail.com · www.asriakdeniz.com"
                    : "Geliştirici: Asri Akdeniz · asriakdeniz@gmail.com · www.asriakdeniz.com";
            }

            if (BtnClose != null)
            {
                BtnClose.Content = isEn ? "Close" : "Kapat";
            }

            if (PnlHelpContentTr != null)
            {
                PnlHelpContentTr.Visibility = isEn ? Visibility.Collapsed : Visibility.Visible;
            }

            if (PnlHelpContentEn != null)
            {
                PnlHelpContentEn.Visibility = isEn ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                if (e.Uri != null)
                {
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                MessageBox.Show(
                    isEn ? $"Unable to open link: {ex.Message}" : $"Bağlantı açılamadı: {ex.Message}",
                    isEn ? "Information" : "Bilgi",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void BtnCopyRolePrompt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                string ruleText;

                if (isEn)
                {
                    ruleText =
@"# AI Project Rules (GEMINI.md)

## Role Distinction Rule (Bridge vs Developer)
Act according to the role specified in the user's prompt:

1. ""Köprüsün"" (Bridge) Mode:
- When the user starts with ""Köprüsün"", STRICTLY DO NOT modify or edit TIAgentMCPServer.csproj or TIAgent C# source code (*.cs, *.xaml, etc.).
- Use only the Bridge API (http://localhost:19520/api/package or Packages/Inbox) to send packages/commands to TIAgent.
- All user requests regarding the TIA Portal project (Ladder/SCL circuit generation, tag, DB, deletion) are sent to TIAgent via the bridge.

2. ""Programcısın"" (Developer) Mode:
- When the user starts with ""Programcısın"", you may make code edits, bug fixes, or architectural changes directly in TIAgent software itself (TIAgentMCPServer.csproj).
- Build the changes with `dotnet build` and restart the application.";
                }
                else
                {
                    ruleText =
@"# AI Proje Kuralları (GEMINI.md)

## Rol Ayrımı Kuralı (Köprü vs Programcı)
Kullanıcının talimatında belirttiği role göre hareket edilir:

1. ""Köprüsün"" Modu:
- Kullanıcı ""Köprüsün"" dediğinde TIAgentMCPServer.csproj veya TIAgent C# kaynak kodlarında (*.cs, *.xaml, vb.) KESİNLİKLE HİÇBİR DÜZENLEME YAPILMAZ.
- Yalnızca TIAgent'a paket/komut göndermek için Köprü API'si (http://localhost:19520/api/package veya Packages/Inbox) kullanılır.
- Kullanıcının TIA Portal projesine ilişkin tüm istekleri (Ladder/SCL devre üretimi, tag, DB, silme işlemleri) köprü üzerinden TIAgent'a iletilir.

2. ""Programcısın"" Modu:
- Kullanıcı ""Programcısın"" dediğinde TIAgent yazılımının kendisinde (TIAgentMCPServer.csproj) kod düzenlemesi, hata düzeltmesi veya mimari değişiklikler yapılabilir.
- Yapılan değişiklikler dotnet build ile derlenir ve uygulama yeniden başlatılır.";
                }

                Clipboard.SetText(ruleText);

                if (TxtCopyFeedback != null)
                {
                    TxtCopyFeedback.Text = "✓ AI 'Köprüsün / Programcısın' kural metni panoya kopyalandı!";
                    TxtCopyFeedback.Visibility = Visibility.Visible;
                }

                if (TxtCopyFeedbackEn != null)
                {
                    TxtCopyFeedbackEn.Text = "✓ AI 'Köprüsün / Programcısın' role rules copied to clipboard!";
                    TxtCopyFeedbackEn.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                MessageBox.Show(
                    isEn ? $"Could not copy to clipboard: {ex.Message}" : $"Panoya kopyalanamadı: {ex.Message}",
                    isEn ? "Error" : "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void BtnCopyAgentProtocols_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                string protocolsText;

                if (isEn)
                {
                    protocolsText =
@"# TIAgent MCP Server — Multi-Agent Integration Guide (asriakdeniz@gmail.com)

1. Model Context Protocol (MCP) — Cursor, VS Code / GitHub Copilot, Cline, Roo Code
Uses the tiagent_mcp.py stdio MCP server at project root.
Cursor configuration (.cursor/mcp.json):
{
  ""mcpServers"": {
    ""tiagent"": {
      ""command"": ""python"",
      ""args"": [""${workspaceFolder}/tiagent_mcp.py""]
    }
  }
}

2. OpenAPI 3.0 / Swagger — Hermes, LangChain, AutoGen, CrewAI
Live Schema URL: http://localhost:19520/swagger.json or http://localhost:19520/openapi.json
CORS and OPTIONS 204 preflight are enabled.

3. One-Line CLI — OpenCode, Aider, and Terminal Agents
Commands:
- tiagent status (or tiagent dbs / tiagent project)
- tiagent send <package.json>
- tiagent command ""<instruction>""
- tiagent delete -Blocks ""<names>"" (for all DBs: -Blocks ""*"")
- tiagent gsd ""<keyword>""

4. Hot-Folder (Inbox / Outbox) — Network-Restricted / Sandboxed Agents
- Request: Packages/Inbox/<package>.json
- Result: Packages/Outbox/<package>.json.result.json

5. Standard Rule Files:
- Cursor: .cursorrules
- GitHub Copilot: .github/copilot-instructions.md
- Claude Code / OpenCode: CLAUDE.md
- Hermes / Aider / LangChain: CONVENTIONS.md
- AI: GEMINI.md";
                }
                else
                {
                    protocolsText =
@"# TIAgent MCP Server — Çoklu Ajan Entegrasyon Kılavuzu (asriakdeniz@gmail.com)

1. Model Context Protocol (MCP) — Cursor, VS Code / GitHub Copilot, Cline, Roo Code
Proje kökündeki tiagent_mcp.py stdio MCP sunucusunu kullanır.
Cursor yapılandırması (.cursor/mcp.json):
{
  ""mcpServers"": {
    ""tiagent"": {
      ""command"": ""python"",
      ""args"": [""${workspaceFolder}/tiagent_mcp.py""]
    }
  }
}

2. OpenAPI 3.0 / Swagger — Hermes, LangChain, AutoGen, CrewAI
Canlı Şema URL: http://localhost:19520/swagger.json veya http://localhost:19520/openapi.json
CORS ve OPTIONS 204 preflight aktiftir.

3. Tek Satırlık CLI — OpenCode, Aider ve Terminal Ajanları
Komutlar:
- tiagent status (veya tiagent dbs / tiagent project)
- tiagent send <paket.json>
- tiagent command ""<talimat>""
- tiagent delete -Blocks ""<adlar>"" (tüm DB'ler için: -Blocks ""*"")
- tiagent gsd ""<anahtar>""

4. Hot-Folder (Inbox / Outbox) — Ağ Kısıtlamalı / Sandbox Ajanlar
- İstek: Packages/Inbox/<paket>.json
- Sonuç: Packages/Outbox/<paket>.json.result.json

5. Standart Kural Dosyaları:
- Cursor: .cursorrules
- GitHub Copilot: .github/copilot-instructions.md
- Claude Code / OpenCode: CLAUDE.md
- Hermes / Aider / LangChain: CONVENTIONS.md
- AI: GEMINI.md";
                }

                Clipboard.SetText(protocolsText);

                if (TxtAgentProtocolsFeedback != null)
                {
                    TxtAgentProtocolsFeedback.Text = "✓ Çoklu Ajan protokol ve kurulum metni panoya kopyalandı!";
                    TxtAgentProtocolsFeedback.Visibility = Visibility.Visible;
                }

                if (TxtAgentProtocolsFeedbackEn != null)
                {
                    TxtAgentProtocolsFeedbackEn.Text = "✓ Multi-Agent protocol and setup guide copied to clipboard!";
                    TxtAgentProtocolsFeedbackEn.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                bool isEn = string.Equals(_language, "en", StringComparison.OrdinalIgnoreCase);
                MessageBox.Show(
                    isEn ? $"Could not copy to clipboard: {ex.Message}" : $"Panoya kopyalanamadı: {ex.Message}",
                    isEn ? "Error" : "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

