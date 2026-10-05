osu! Pattern Gallery  (osu! stable)

=== TÜRKÇE ===
KURULUM
- Hazır paket (PatternGallery-win64.zip): zip'i bir klasöre çıkar, PatternGallery.exe'yi aç. Başka bir şey kurmana gerek yok.
- Kaynak koddan: .NET 10 SDK kur, Build.bat'a çift tıkla, App\PatternGallery.exe'yi aç.
  (Release.bat, başkalarıyla paylaşmak için hazır paketi oluşturur.)

KULLANIM
- Kaydet: editörde objeleri seç, programda isim yaz, "Seçileni kaydet". Kategori seçiliyse oraya kaydedilir.
- Ekle: editörde istediğin ana git, pattern kartına çift tıkla.
- Kısayol (varsayılan F11, editördeyken): objeler seçiliyse kaydetme, seçili değilse arama/ekleme penceresi açılır.
  Program küçültülmüş dururken de çalışır (tam ekran için ideal).
- Ritme uyarla: pattern haritanın BPM'ine ve slider hızına göre uyarlanır (1/4 stream 1/4 kalır).
- Çakışanları sil: pattern'in kapladığı aralıktaki eski objeler silinir.
- Geri al: sadece eklenen pattern'i kaldırır, sonradan yaptığın değişikliklere dokunmaz. Son 30 ekleme geri alınabilir.
- Düzenle (✎): isim veya kategori değiştir. Sil (✕).
- Dışa aktar: Ctrl+tık ile birden fazla seç (Ctrl+A: hepsi), her pattern ayrı .osu dosyası olarak kaydedilir.
- İçe aktar: .osu dosyalarını, klasörleri ya da .zip paketlerini pencereye sürükle-bırak.
- Her eklemeden önce haritanın yedeği App\Backups klasörüne alınır (son 50 yedek tutulur).

OSU! SOHBET BOTU
- Programda "Bot" → "Botu başlat". Çıkan 6 haneli kodu osu!'da exporage'a özel mesajla yaz: !link 123456
- Sonra editör açıkken osu! sohbetinden komut yaz (program açık kalmalı):
  !jumps 1  ·  !p kare  ·  !list  ·  !list jumps  ·  !undo  ·  !lang tr  ·  !help

=== ENGLISH ===
SETUP
- Ready package (PatternGallery-win64.zip): extract it, open PatternGallery.exe. Nothing else to install.
- From source: install the .NET 10 SDK, double-click Build.bat, open App\PatternGallery.exe.
  (Release.bat makes the ready package for sharing.)

USAGE
- Save: select objects in the editor, type a name, "Save selected". Goes into the chosen category.
- Insert: go to a time in the editor, double-click a pattern card.
- Hotkey (default F11, in the editor): opens saving if objects are selected, otherwise search & insert.
  Works while the program is minimized (great for fullscreen).
- Adapt rhythm: the pattern is fitted to the map's BPM and slider velocity (a 1/4 stream stays 1/4).
- Replace overlapping: old objects in the pattern's time range are removed.
- Undo: removes only the inserted pattern, your later changes are kept. The last 30 inserts can be undone.
- Edit (✎): rename or move to another category. Delete (✕).
- Export: Ctrl+click to select several (Ctrl+A: all); each pattern is saved as its own .osu file.
- Import: drag & drop .osu files, folders or .zip packs onto the window.
- A backup of the map is made before every insert (the newest 50 are kept).

OSU! CHAT BOT
- In the program: "Bot" → "Start bot". Send the 6 digit code to exporage in osu! chat: !link 123456
- Then, with the editor open, type commands in osu! chat (keep the program running):
  !jumps 1  ·  !p square  ·  !list  ·  !list jumps  ·  !undo  ·  !lang en  ·  !help

Only works with osu! stable (not lazer). Needs Microsoft Edge WebView2 (part of Windows 10/11).
Uses EditorReader.dll from Mapping Tools (OliBomby/Mapping_Tools, MIT license).
