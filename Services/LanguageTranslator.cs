using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TIAgent.Services
{
    public static class LanguageTranslator
    {
        public static string TranslateCategory(string category, string lang)
        {
            if (string.IsNullOrEmpty(category)) return category ?? "";
            bool isEn = string.Equals(lang, "en", StringComparison.OrdinalIgnoreCase);

            string lower = category.Trim().ToLowerInvariant();
            if (isEn)
            {
                switch (lower)
                {
                    case "mod": return "mode";
                    case "proje": return "project";
                    case "silme": return "delete";
                    case "paket": return "package";
                    case "onay": return "approval";
                    case "aktarım": return "transfer";
                    case "izin": return "permission";
                    case "donanım": return "hardware";
                    case "yardım": return "help";
                    default: return category;
                }
            }
            else
            {
                switch (lower)
                {
                    case "mode": return "mod";
                    case "project": return "proje";
                    case "delete": return "silme";
                    case "package": return "paket";
                    case "approval": return "onay";
                    case "transfer": return "aktarım";
                    case "permission": return "izin";
                    case "hardware": return "donanım";
                    case "help": return "yardım";
                    default: return category;
                }
            }
        }

        public static string TranslateLog(string text, string targetLang)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            bool isEn = string.Equals(targetLang, "en", StringComparison.OrdinalIgnoreCase);

            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = isEn ? TranslateLineToEnglish(lines[i]) : TranslateLineToTurkish(lines[i]);
            }
            return string.Join("\r\n", lines);
        }

        private static string TranslateLineToEnglish(string tr)
        {
            if (string.IsNullOrWhiteSpace(tr)) return tr ?? "";
            string s = tr.Trim();

            // Direct full phrases
            if (s.Contains("TIAgent başlatıldı (AI Köprüsü: http://localhost:19520 & Packages/Inbox)"))
                return "TIAgent started (AI Bridge: http://localhost:19520 & Packages/Inbox)";
            if (s.Contains("TIA Portal açık ancak içinde açık proje yok"))
                return "TIA Portal is open but has no open project";
            if (s.Contains("TIA Portal bağlantısı kesildi") || s.Contains("TIA Portal bağlantısı kapatıldı"))
                return "TIA Portal connection closed";
            if (s.Contains("Bağlantı kuruldu. Açık proje taranıyor..."))
                return "Connection established. Scanning for open projects...";
            if (s.Contains("AI Köprüsü kullanıcı tarafından AKTİF yapıldı"))
                return "AI Bridge ENABLED by user";
            if (s.Contains("AI Köprüsü kullanıcı tarafından PASİF yapıldı"))
                return "AI Bridge DISABLED by user";
            if (s.Contains("Çalışan TIA Portal süreci bulunamadı"))
                return "No running TIA Portal process found";
            if (s.Contains("Bağlantı bekleniyor... | Aktif Proje: - |"))
                return "Waiting for connection... | Active Project: - |";

            // Permissions tab details
            s = Regex.Replace(s, @"^Kullanıcı:\s*(.*)", "User: $1");
            s = Regex.Replace(s, @"^Siemens TIA Openness Grubu:\s*✓\s*Üye\s*\(Aktif\)", "Siemens TIA Openness Group: ✓ Member (Active)");
            s = Regex.Replace(s, @"^Siemens TIA Openness Grubu:\s*✗\s*Üye Değil\s*\(Eklemeniz Önerilir\)", "Siemens TIA Openness Group: ✗ Not a Member (Recommended to add)");
            s = Regex.Replace(s, @"^Registry Whitelist \((.*?)\):\s*✓\s*Kayıtlı\s*\(Onay Penceresi Çıkmaz\)", "Registry Whitelist ($1): ✓ Registered (No confirmation popup)");
            s = Regex.Replace(s, @"^Registry Whitelist \((.*?)\):\s*ℹ\s*Kayıtlı Değil\s*\(İlk bağlantıda TIA onay sorabilir\)", "Registry Whitelist ($1): ℹ Not Registered (TIA may prompt for approval on first connection)");

            // Mode
            s = Regex.Replace(s, @"İşlem modu:\s*Onay Bekleme\s*\(Sormadan doğrudan TIA Portal'a aktarır\)",
                "Operation mode: No Approval (Direct transfer to TIA Portal without confirmation)");
            s = Regex.Replace(s, @"İşlem modu:\s*Onay Bekle\s*\(Her işlem onay çubuğunda onayınızı bekler\)",
                "Operation mode: Wait for Approval (Each operation awaits confirmation in approval bar)");

            // Regex patterns
            s = Regex.Replace(s, @"^TIA durumu \((.*?)\)", "TIA status ($1)");
            s = Regex.Replace(s, @"^Açık (.*?) süreci bulunamadı", "No open $1 process found");
            s = Regex.Replace(s, @"^TIA'ya bağlan \(PID:\s*(\d+)\)", "Connecting to TIA (PID: $1)");
            s = Regex.Replace(s, @"^Seçilen sürece bağlanılıyor \(PID:\s*(\d+)\)\.\.\.", "Connecting to selected process (PID: $1)...");
            s = Regex.Replace(s, @"^PLC bilgisi \((.*?)\)", "PLC info ($1)");
            s = Regex.Replace(s, @"^Proje '(.*?)' açık ancak PLC istasyonu bulunamadı", "Project '$1' is open but PLC station not found");
            s = Regex.Replace(s, @"^TIA projesine bağlanıldı:\s*(.*)", "Connected to TIA project: $1");
            s = Regex.Replace(s, @"^TIA bağlantı hatası:\s*(.*)", "TIA connection error: $1");
            s = Regex.Replace(s, @"^Bağlantı hatası:\s*(.*)", "Connection error: $1");
            s = Regex.Replace(s, @"^Projeye bağlanırken hata:\s*(.*)", "Error connecting to project: $1");
            s = Regex.Replace(s, @"^Bağlantı kesme hatası:\s*(.*)", "Disconnection error: $1");
            s = Regex.Replace(s, @"^Bağlantı kapatılırken uyarı:\s*(.*)", "Warning while closing connection: $1");
            s = Regex.Replace(s, @"^Proje dosyası açılıyor:\s*(.*)", "Opening project file: $1");
            s = Regex.Replace(s, @"^Proje açma hatası:\s*(.*)", "Project opening error: $1");
            s = Regex.Replace(s, @"^Proje okuma uyarısı:\s*(.*)", "Project read warning: $1");
            s = Regex.Replace(s, @"^Proje başarıyla açıldı:\s*(.*)", "Project successfully opened: $1");
            s = Regex.Replace(s, @"^Sürüm listesi yüklenemedi:\s*(.*)", "Could not load version list: $1");
            s = Regex.Replace(s, @"^Yardım penceresi açılamadı:\s*(.*)", "Could not open help window: $1");

            s = Regex.Replace(s, @"^Proje okundu \((\d+)\s*blok,\s*(\d+)\s*DB,\s*(\d+)\s*tag\)",
                "Project read ($1 blocks, $2 DBs, $3 tags)");
            s = Regex.Replace(s, @"^Proje '(.*?)':\s*(\d+)\s*adet PLC istasyonu bulundu\.?",
                "Project '$1': $2 PLC station(s) found.");
            s = Regex.Replace(s, @"^SCL blok taraması tamamlandı:\s*(\d+)\s*adet FC / FB SCL fonksiyonu bulundu\.?",
                "SCL block scan completed: $1 FC / FB SCL function(s) found.");
            s = Regex.Replace(s, @"^'(.*?)' kodları okundu \((\d+)\s*network/ifade\)\.?",
                "Code read for '$1' ($2 networks/statements).");

            // Deletion & Commands
            s = Regex.Replace(s, @"^Silme işlemi onay çubuğuna alındı:\s*""(.*?)""\s*\(Onay bekleniyor\)",
                "Delete operation queued for approval: \"$1\" (Pending confirmation)");
            s = Regex.Replace(s, @"^Silme komutu alındı \((.*?)\):\s*""(.*?)""",
                "Delete command received ($1): \"$2\"");
            s = Regex.Replace(s, @"^Silme hatası:\s*(.*)", "Deletion error: $1");
            s = Regex.Replace(s, @"^Özel paket çözümlenemedi:\s*""(.*?)""", "Custom package could not be resolved: \"$1\"");
            s = Regex.Replace(s, @"^Paket üretilemedi:\s*(.*)", "Package generation failed: $1");

            // Ready packages
            s = Regex.Replace(s, @"^(.*?) hazır – (\d+)\s*blok,\s*(\d+)\s*DB,\s*(\d+)\s*I/O tag\s*(.*?)\s*\((\d+)\s*bileşen\)",
                "$1 ready – $2 blocks, $3 DBs, $4 I/O tags $5 ($6 components)");
            s = Regex.Replace(s, @"^(.*?) hazır – (\d+)\s*blok,\s*(\d+)\s*DB,\s*(\d+)\s*I/O tag\s*\((\d+)\s*bileşen\)",
                "$1 ready – $2 blocks, $3 DBs, $4 I/O tags ($5 components)");

            s = Regex.Replace(s, @"^Paket onaylandı:\s*(.*?)\s*→\s*(.*?)\s*>\s*Program blocks",
                "Package approved: $1 → $2 > Program blocks");
            s = Regex.Replace(s, @"^(.*?)\s*aktarım hatası:\s*(.*)", "$1 transfer error: $2");
            s = Regex.Replace(s, @"^İşlem reddedildi:\s*(.*?)\s*\((.*?)\)", "Operation rejected: $1 ($2)");

            // Hardware & Agents
            s = Regex.Replace(s, @"^Donanım listesi (.*?)'ye gönderildi", "Hardware list sent to $1");
            s = Regex.Replace(s, @"^Yüklü GSDML listesi gönderildi", "Installed GSDML list sent");
            s = Regex.Replace(s, @"^(.*?)\s*özel paketi alındı:\s*(.*)", "$1 custom package received: $2");
            s = Regex.Replace(s, @"^(.*?)\s*komutu alındı:\s*""(.*?)""", "$1 command received: \"$2\"");

            // Openness Service lines
            s = Regex.Replace(s, @"^API Kütüphanesi yüklendi:\s*(.*)", "API Library loaded: $1");
            s = Regex.Replace(s, @"^Çalışan süreçler kontrol edilirken bilgi:\s*(.*)", "Info while checking running processes: $1");
            s = Regex.Replace(s, @"^TIA Portal \(PID:\s*(\d+)\) sürecine bağlanılıyor\.\.\.",
                "Connecting to TIA Portal (PID: $1) process...");
            s = Regex.Replace(s, @"^'(.*?)' projesi zaten açık \(PID:\s*(\d+)\)\. Doğrudan bağlanılıyor\.\.\.",
                "Project '$1' is already open (PID: $2). Connecting directly...");
            s = Regex.Replace(s, @"^Boşta çalışan TIA Portal \(PID:\s*(\d+)\) bulundu\. Proje bu oturumda açılıyor\.\.\.",
                "Idle TIA Portal found (PID: $1). Opening project in this session...");
            s = Regex.Replace(s, @"^TIA Portal başlatılıyor \((.*?)\) ve kapalı proje açılıyor:\s*(.*?)\.\.\.",
                "Starting TIA Portal ($1) and opening offline project: $2...");

            s = Regex.Replace(s, @"^'(.*?)' veri bloğu derleniyor\s*(.*)", "Compiling data block '$1' $2");
            s = Regex.Replace(s, @"^'(.*?)' derlenirken uyarı:\s*(.*)", "Warning while compiling '$1': $2");
            s = Regex.Replace(s, @"^'(.*?)' Global DB oluşturuldu ve (\d+)\s*tag tanımlandı\.?",
                "Global DB '$1' created and $2 tags defined.");
            s = Regex.Replace(s, @"^'(.*?)' veri bloğu güncellendi \((\d+)\s*yeni tag eklendi\)\.?",
                "Data block '$1' updated ($2 new tags added).");
            s = Regex.Replace(s, @"^'(.*?)' veri bloğunda tüm taglar zaten tanımlı\.?",
                "All tags are already defined in data block '$1'.");
            s = Regex.Replace(s, @"^Mevcut '(.*?)' XML üzerinden güncellenirken uyarı:\s*(.*?)\.? Alternatif yöntem deneniyor\.\.\.",
                "Warning updating '$1' via XML: $2. Trying alternative method...");
            s = Regex.Replace(s, @"^Şablon DB üzerinden '(.*?)' oluşturulamadı \((.*?)\), doğrudan XML/kaynak yöntemi deneniyor\.\.\.",
                "Could not create '$1' via template DB ($2), trying direct XML/source method...");
            s = Regex.Replace(s, @"^'(.*?)' Global DB doğrudan oluşturuldu ve (\d+)\s*tag tanımlandı\.?",
                "Global DB '$1' created directly and $2 tags defined.");
            s = Regex.Replace(s, @"^Doğrudan XML ile '(.*?)' oluşturulamadı \((.*?)\), ExternalSource yöntemi deneniyor\.\.\.",
                "Could not create '$1' via direct XML ($2), trying ExternalSource method...");
            s = Regex.Replace(s, @"^'(.*?)' Global DB ExternalSource üzerinden oluşturuldu \((\d+)\s*tag tanımlandı\)\.?",
                "Global DB '$1' created via ExternalSource ($2 tags defined).");
            s = Regex.Replace(s, @"^\[DEBUG\] Hatalı SimaticML XML dosyası kaydedildi:\s*(.*)",
                "[DEBUG] Faulty SimaticML XML file saved: $1");
            s = Regex.Replace(s, @"^LAD bloğu projeye başarıyla eklendi / içe aktarıldı\.?",
                "LAD block successfully added / imported into project.");
            s = Regex.Replace(s, @"^'(.*?)' bloğu derleniyor\.\.\.", "Compiling block '$1'...");
            s = Regex.Replace(s, @"^İçe aktarılan blok derlenirken uyarı:\s*(.*)", "Warning while compiling imported block: $1");
            s = Regex.Replace(s, @"^\[Derleme\]\s*(.*)", "[Compile] $1");
            s = Regex.Replace(s, @"^\[Derleme Detayı\]\s*(.*)", "[Compile Detail] $1");
            s = Regex.Replace(s, @"^\[Derleme Uyarısı\]\s*(.*)", "[Compile Warning] $1");
            s = Regex.Replace(s, @"^Test için örnek SCL fonksiyon bloğu \('(.*?)'\) oluşturuluyor\.\.\.",
                "Creating sample SCL function block ('$1') for testing...");
            s = Regex.Replace(s, @"^Örnek SCL bloğu \('(.*?)'\) başarıyla PLC'ye aktarıldı\.?",
                "Sample SCL block ('$1') successfully transferred to PLC.");
            s = Regex.Replace(s, @"^Dosya okunuyor:\s*'(.*?)'\.\.\.", "Reading file: '$1'...");
            s = Regex.Replace(s, @"^SimaticML XML dosyası PLC bloklarına aktarılıyor:\s*'(.*?)'\.\.\.",
                "Transferring SimaticML XML file to PLC blocks: '$1'...");
            s = Regex.Replace(s, @"^SimaticML XML bloğu başarıyla projeye aktarıldı:\s*'(.*?)'",
                "SimaticML XML block successfully imported into project: '$1'");
            s = Regex.Replace(s, @"^SCL kaynak kodunda '(.*?)' bloğu tespit edildi\.?",
                "Block '$1' detected in SCL source code.");
            s = Regex.Replace(s, @"^Dosyada FUNCTION zarfı bulunamadı, '(.*?)' adıyla otomatik SCL fonksiyon zarfına alındı\.?",
                "FUNCTION envelope not found in file; automatically wrapped in SCL function envelope as '$1'.");
            s = Regex.Replace(s, @"^SCL kaynağından PLC blokları derlenip üretiliyor \('(.*?)'\)\.\.\.",
                "Compiling and generating PLC blocks from SCL source ('$1')...");
            s = Regex.Replace(s, @"^'(.*?)' SCL bloğu başarıyla üretildi ve PLC projesine eklendi\.?",
                "SCL block '$1' successfully generated and added to PLC project.");
            s = Regex.Replace(s, @"^Program blocks altında '(.*?)' klasörü oluşturuldu\.?",
                "Folder '$1' created under Program blocks.");
            s = Regex.Replace(s, @"^PLC Tag Tablosu oluşturuldu:\s*'(.*?)'", "PLC Tag Table created: '$1'");
            s = Regex.Replace(s, @"^Tag '(.*?)' oluşturulurken uyarı:\s*(.*)", "Warning while creating tag '$1': $2");
            s = Regex.Replace(s, @"^'(.*?)' tablosuna (\d+)\s*yeni PLC I/O Tag eklendi\.?",
                "$2 new PLC I/O Tags added to table '$1'.");
            s = Regex.Replace(s, @"^'(.*?)' Instance DB \('(.*?)' için\) oluşturuldu\.?",
                "Instance DB '$1' created (for '$2').");
            s = Regex.Replace(s, @"^'(.*?)' Instance DB ExternalSource ile oluşturuldu\.?",
                "Instance DB '$1' created via ExternalSource.");
            s = Regex.Replace(s, @"^'(.*?)' Instance DB oluşturulurken uyarı:\s*(.*)",
                "Warning while creating Instance DB '$1': $2");
            s = Regex.Replace(s, @"^Main \[OB1\] içerisine '(.*?)' blok çağrısı \(LAD Network\) eklendi\.?",
                "Block call '$1' (LAD Network) added into Main [OB1].");
            s = Regex.Replace(s, @"^Main \[OB1\] güncellenirken uyarı:\s*(.*)",
                "Warning while updating Main [OB1]: $1");
            s = Regex.Replace(s, @"^Main \[OB1\] oluşturuldu ve '(.*?)' çağrısı eklendi\.?",
                "Main [OB1] created and call '$1' added.");
            s = Regex.Replace(s, @"^Main \[OB1\] oluşturulurken uyarı:\s*(.*)",
                "Warning while creating Main [OB1]: $1");
            s = Regex.Replace(s, @"^Yedekleme tamamlandı \((\d+)\s*mevcut blok yedeklendi\):\s*(.*)",
                "Backup completed ($1 existing block(s) backed up): $2");
            s = Regex.Replace(s, @"^Yedekleme uyarısı:\s*(.*)", "Backup warning: $1");
            s = Regex.Replace(s, @"^Eksik (\d+)\s*adet PLC Tag otomatik tanımlandı \((.*?)\):\s*(.*)",
                "$1 missing PLC Tag(s) automatically defined ($2): $3");
            s = Regex.Replace(s, @"^Referans verilen taglar taranırken uyarı:\s*(.*)",
                "Warning while scanning referenced tags: $1");
            s = Regex.Replace(s, @"^\[Uyarı\] Mevcut Main \[OB1\] tutarsız olduğu için dışa aktarılamadı \((.*?)\)\. Yeni blok olarak içe aktarılacak\.?",
                "[Warning] Existing Main [OB1] could not be exported due to inconsistency ($1). Will import as new block.");
            s = Regex.Replace(s, @"^Main \[OB1\] network birleştirme uyarısı:\s*(.*?)\.? Doğrudan yeni blok olarak içe aktarılıyor\.\.\.",
                "Main [OB1] network merge warning: $1. Importing directly as new block...");
            s = Regex.Replace(s, @"^Tag tablosu '(.*?)' silinirken uyarı:\s*(.*)",
                "Warning while deleting tag table '$1': $2");

            // Common prefixes / suffixes
            s = Regex.Replace(s, @"\bblok\b", "block");
            s = Regex.Replace(s, @"\bbileşen\b", "component");
            s = Regex.Replace(s, @"\[HATA\]", "[ERROR]");
            s = Regex.Replace(s, @"\[UYARI\]", "[WARNING]");
            s = Regex.Replace(s, @"\[BİLGİ\]", "[INFO]");

            return s;
        }

        private static string TranslateLineToTurkish(string en)
        {
            if (string.IsNullOrWhiteSpace(en)) return en ?? "";
            string s = en.Trim();

            if (s.Contains("TIAgent started (AI Bridge: http://localhost:19520 & Packages/Inbox)"))
                return "TIAgent başlatıldı (AI Köprüsü: http://localhost:19520 & Packages/Inbox)";
            if (s.Contains("TIA Portal is open but has no open project"))
                return "TIA Portal açık ancak içinde açık proje yok";
            if (s.Contains("TIA Portal connection closed"))
                return "TIA Portal bağlantısı kapatıldı";
            if (s.Contains("Connection established. Scanning for open projects..."))
                return "Bağlantı kuruldu. Açık proje taranıyor...";
            if (s.Contains("AI Bridge ENABLED by user"))
                return "AI Köprüsü kullanıcı tarafından AKTİF yapıldı";
            if (s.Contains("AI Bridge DISABLED by user"))
                return "AI Köprüsü kullanıcı tarafından PASİF yapıldı";
            if (s.Contains("No running TIA Portal process found"))
                return "Çalışan TIA Portal süreci bulunamadı";
            if (s.Contains("Waiting for connection... | Active Project: - |"))
                return "Bağlantı bekleniyor... | Aktif Proje: - |";

            s = Regex.Replace(s, @"^User:\s*(.*)", "Kullanıcı: $1");
            s = Regex.Replace(s, @"^Siemens TIA Openness Group:\s*✓\s*Member\s*\(Active\)", "Siemens TIA Openness Grubu: ✓ Üye (Aktif)");
            s = Regex.Replace(s, @"^Registry Whitelist \((.*?)\):\s*✓\s*Registered\s*\(No confirmation popup\)", "Registry Whitelist ($1): ✓ Kayıtlı (Onay Penceresi Çıkmaz)");
            s = Regex.Replace(s, @"^Registry Whitelist \((.*?)\):\s*ℹ\s*Not Registered\s*\(TIA may prompt for approval on first connection\)", "Registry Whitelist ($1): ℹ Kayıtlı Değil (İlk bağlantıda TIA onay sorabilir)");

            s = Regex.Replace(s, @"Operation mode:\s*No Approval\s*\(Direct transfer to TIA Portal without confirmation\)",
                "İşlem modu: Onay Bekleme (Sormadan doğrudan TIA Portal'a aktarır)");
            s = Regex.Replace(s, @"Operation mode:\s*Wait for Approval\s*\(Each operation awaits confirmation in approval bar\)",
                "İşlem modu: Onay Bekle (Her işlem onay çubuğunda onayınızı bekler)");

            s = Regex.Replace(s, @"^TIA status \((.*?)\)", "TIA durumu ($1)");
            s = Regex.Replace(s, @"^No open (.*?) process found", "Açık $1 süreci bulunamadı");
            s = Regex.Replace(s, @"^Connecting to TIA \(PID:\s*(\d+)\)", "TIA'ya bağlan (PID: $1)");
            s = Regex.Replace(s, @"^Connected to TIA project:\s*(.*)", "TIA projesine bağlanıldı: $1");
            s = Regex.Replace(s, @"^PLC info \((.*?)\)", "PLC bilgisi ($1)");
            s = Regex.Replace(s, @"^Project read \((\d+)\s*blocks,\s*(\d+)\s*DBs,\s*(\d+)\s*tags\)",
                "Proje okundu ($1 blok, $2 DB, $3 tag)");
            s = Regex.Replace(s, @"^Opening project file:\s*(.*)", "Proje dosyası açılıyor: $1");
            s = Regex.Replace(s, @"^Project successfully opened:\s*(.*)", "Proje başarıyla açıldı: $1");

            s = Regex.Replace(s, @"\[ERROR\]", "[HATA]");
            s = Regex.Replace(s, @"\[WARNING\]", "[UYARI]");
            s = Regex.Replace(s, @"\[INFO\]", "[BİLGİ]");

            return s;
        }
    }
}
