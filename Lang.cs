// All texts of the program in Turkish and English.

using System;
using System.Collections.Generic;
using System.IO;

static class Lang
{
    static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "language.txt");
    public static string Current = "tr";

    static readonly Dictionary<string, (string tr, string en)> Texts = new Dictionary<string, (string, string)>
    {
        ["title"]          = ("osu! Pattern Ekleyici", "osu! Pattern Inserter"),
        ["saveGroup"]      = ("Editörde seçili notaları bu kategoriye kaydet", "Save the objects selected in the editor to this category"),
        ["category"]       = ("Kategori  (yazdıkça süzülür; yeni kategori için yeni bir ad yaz)", "Category  (filters as you type; type a new name to create one)"),
        ["all"]            = ("Tümü", "All"),
        ["noCategory"]     = ("Kategorisiz", "No category"),
        ["name"]           = ("İsim", "Name"),
        ["saveBtn"]        = ("Seçileni kaydet", "Save selected"),
        ["search"]         = ("Ara...", "Search..."),
        ["listLabel"]      = ("Patternler (editördeki zamana eklemek için çift tıkla):", "Patterns (double-click to insert at the editor time):"),
        ["insert"]         = ("Ekle", "Insert"),
        ["undo"]           = ("Son eklemeyi geri al", "Undo last insert"),
        ["delete"]         = ("Sil", "Delete"),
        ["top"]            = ("Hep üstte", "Always on top"),
        ["ready"]          = ("Hazır.", "Ready."),
        ["mapNone"]        = ("osu! editörü açık değil", "osu! editor is not open"),
        ["mapOpen"]        = ("Açık harita: {0}", "Open map: {0}"),
        ["needName"]       = ("Önce pattern için bir isim yaz.", "Type a name for the pattern first."),
        ["needSelect"]     = ("Önce listeden bir pattern seç.", "Select a pattern in the list first."),
        ["overwriteQ"]     = ("\"{0}\" adında bir pattern zaten var. Üzerine yazılsın mı?", "A pattern named \"{0}\" already exists. Overwrite it?"),
        ["overwriteT"]     = ("Üzerine yazılsın mı?", "Overwrite?"),
        ["deleteQ"]        = ("\"{0}\" silinsin mi?", "Delete the pattern \"{0}\"?"),
        ["deleteT"]        = ("Silinsin mi?", "Delete?"),
        ["deleted"]        = ("\"{0}\" silindi.", "Deleted \"{0}\"."),
        ["skipped"]        = ("({0} dosya atlandı: pattern değil ya da zaten kütüphanede.)", "({0} file(s) skipped: not a pattern or already in the library.)"),
        ["importNone"]     = ("Eklenecek pattern bulunamadı. Sadece içinde nota olan .osu dosyaları ya da pattern paketleri (.zip) eklenebilir.", "No pattern to import. Only .osu files with hit objects can be added."),
        ["exportTitle"]    = ("Dışa aktar", "Export"),
        ["exportFolder"]   = ("{0} pattern dosyasının kaydedileceği klasörü seç", "Choose a folder for the {0} pattern files"),
        ["exported"]       = ("{0} pattern dışa aktarıldı: {1}", "Exported {0} pattern(s): {1}"),
        ["imported"]       = ("{0} pattern dosyası eklendi.", "Imported {0} pattern file(s)."),

        ["patternMissing"] = ("Pattern dosyası bulunamadı: {0}", "Pattern file not found: {0}"),
        ["patternEmpty"]   = ("Pattern dosyasında hiç nota yok.", "The pattern file has no hit objects."),
        ["replaced"]       = ("({0} eski nota yerine geçti.)", "({0} old objects replaced.)"),
        ["renamed"]        = ("\"{0}\" olarak kaydedildi.", "Saved as \"{0}\"."),
        ["nameTaken"]      = ("Bu kategoride \"{0}\" adında bir pattern zaten var.", "There is already a pattern named \"{0}\" in this category."),
        ["inserted"]       = ("{0} nota {1} zamanına eklendi.", "Inserted {0} objects at {1}."),
        ["noSelection"]    = ("Hiçbir şey seçili değil. Önce editörde pattern'in notalarını seç.", "Nothing is selected. Select the pattern's objects in the editor first."),
        ["selectionLost"]  = ("Seçili notalar harita dosyasında bulunamadı.", "Could not find the selected objects in the beatmap file."),
        ["saved"]          = ("{0} nota \"{1}\" olarak kaydedildi.", "Saved {0} objects as \"{1}\"."),
        ["nothingToUndo"]  = ("Geri alınacak bir ekleme yok.", "There is nothing to undo."),
        ["backupMissing"]  = ("Yedek dosyası bulunamadı: {0}", "Backup file not found: {0}"),
        ["adapted"]        = ("(Ritme uyarlandı: {0} → {1} BPM.)", "(Rhythm adapted: {0} → {1} BPM.)"),
        ["adaptedSame"]    = ("(Ritim korundu.)", "(Rhythm kept.)"),
        ["undoneN"]        = ("\"{0}\" geri alındı ({1} nota kaldırıldı, sonradan yaptığın değişiklikler duruyor).", "Undid \"{0}\" ({1} objects removed; your later changes are kept)."),
        ["undoMore"]       = ("Daha {0} ekleme geri alınabilir.", "{0} more insert(s) can be undone."),
        ["undone"]         = ("Son ekleme geri alındı.", "Last insert undone."),
        ["tabSave"]        = ("Kaydet", "Save"),
        ["tabInsert"]      = ("Ekle", "Insert"),
        ["searchPh"]       = ("Pattern ara...", "Search patterns..."),
        ["insertHint"]     = ("Enter: ekle · ↑↓: seç · Ctrl+Tab: sekme değiştir · Esc: kapat", "Enter: insert · ↑↓: choose · Ctrl+Tab: switch tab · Esc: close"),
        ["noPatterns"]     = ("Henüz pattern yok. Önce notaları seçip Kaydet sekmesinden kaydet.", "No patterns yet. Select objects and save them in the Save tab first."),
        ["notesShort"]     = ("nota", "obj."),
        ["hkTitle"]        = ("Pattern kaydet", "Save pattern"),
        ["hkCategory"]     = ("Kategori (boş bırakabilirsin)", "Category (can be empty)"),
        ["hkName"]         = ("Pattern adı", "Pattern name"),
        ["ok"]             = ("Tamam", "OK"),
        ["cancel"]         = ("İptal", "Cancel"),
        ["hotkeyFail"]     = ("Kısayol tuşu başlatılamadı. Programı kapatıp tekrar aç.", "The hotkey could not be started. Close and reopen the program."),
        ["hotkeySet"]      = ("Kısayol tuşu {0} olarak ayarlandı. Editörde {0}: notalar seçiliyse kaydetme, seçili değilse ekleme penceresi açılır.", "Hotkey set to {0}. In the editor, {0} opens saving if objects are selected, otherwise inserting."),
        ["osuNotRunning"]  = ("osu! açık değil.", "osu! is not running."),
        ["editorNotOpen"]  = ("osu! editörü açık değil. Haritanı editörde açıp tekrar dene.", "The osu! editor is not open. Open your beatmap in the editor and try again."),
        ["readFail"]       = ("osu! editörü okunamadı. Bir saniye sonra tekrar dene.\n({0})", "Could not read the osu! editor. Try again in a second.\n({0})"),
        ["mapMissing"]     = ("Harita dosyası bulunamadı: {0}", "Beatmap file not found: {0}"),
    };

    public static string T(string key, params object[] args)
    {
        if (!Texts.TryGetValue(key, out var t)) return key;
        string s = Current == "en" ? t.en : t.tr;
        return args.Length > 0 ? string.Format(s, args) : s;
    }

    public static void Load()
    {
        try { if (File.Exists(SettingsFile)) Current = File.ReadAllText(SettingsFile).Trim() == "en" ? "en" : "tr"; }
        catch { }
    }

    public static void Set(string lang)
    {
        Current = lang;
        try { File.WriteAllText(SettingsFile, lang); } catch { }
    }
}
