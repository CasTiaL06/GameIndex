using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace GameIndex.Services;

/// <summary>
/// Lightweight runtime localization (English text is the key). Code strings go through <see cref="T"/>/<see cref="F"/>;
/// static XAML text is translated by <see cref="Apply"/>, which walks the UI and remembers the English original so switching back works.
/// </summary>
public static class Loc
{
    public static string Language { get; private set; } = "en";
    public static bool IsTurkish => Language == "tr";

    private static readonly ConditionalWeakTable<object, Dictionary<string, string>> Originals = new();
    private static readonly Dictionary<string, string> Reverse = new();

    static Loc() { foreach (var (k, v) in Tr) Reverse[v] = k; }

    /// <summary>code: "system" | "en" | "tr"</summary>
    public static void SetLanguage(string code)
    {
        if (code == "system") code = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
        Language = code == "tr" ? "tr" : "en";
        var culture = CultureInfo.GetCultureInfo(Language == "tr" ? "tr-TR" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
    }

    public static string T(string en) => IsTurkish && Tr.TryGetValue(en, out var v) ? v : en;
    public static string F(string en, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(en), args);

    public static string Duration(long seconds)
    {
        if (!IsTurkish) return GameIndex.Core.Format.Duration(seconds);
        if (seconds <= 0) return "0 dakika";
        long totalMin = seconds / 60, h = totalMin / 60, m = totalMin % 60;
        if (h == 0) return m == 0 ? "1 dakikadan az" : $"{m} dakika";
        return m == 0 ? $"{h} saat" : $"{h} saat {m} dakika";
    }

    // ---------- UI tree translation ----------
    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);   // each element exactly once (logical + visual walks overlap)
        try { Visit(root, 0, seen); } catch { /* translation must never break the UI */ }
    }

    public static void FixText(MenuFlyoutItem item) => Fix(item, "Text", item.Text, t => item.Text = t);

    private static void Fix(object owner, string prop, string? current, Action<string> set)
    {
        if (string.IsNullOrEmpty(current)) return;
        var map = Originals.GetOrCreateValue(owner);
        string? orig = null;
        if (map.TryGetValue(prop, out var known) && (current == known || current == T(known))) orig = known;
        if (orig is null)
        {
            if (Tr.ContainsKey(current)) orig = current;
            else if (Reverse.TryGetValue(current, out var en)) orig = en;
        }
        if (orig is null) return;
        map[prop] = orig;
        var target = T(orig);
        if (target != current) set(target);
    }

    private static void Visit(DependencyObject d, int depth, HashSet<DependencyObject> seen)
    {
        if (depth > 80 || !seen.Add(d)) return;
        switch (d)
        {
            case TextBlock tb:
                Fix(tb, "Text", tb.Text, t => tb.Text = t);
                foreach (var inl in tb.Inlines) if (inl is Run r) Fix(r, "Text", r.Text, t => r.Text = t);
                break;
            case TextBox x: HeaderFix(x, x.Header, h => x.Header = h); Fix(x, "Ph", x.PlaceholderText, t => x.PlaceholderText = t); break;
            case PasswordBox x: HeaderFix(x, x.Header, h => x.Header = h); break;
            case NumberBox x: HeaderFix(x, x.Header, h => x.Header = h); break;
            case Slider x: HeaderFix(x, x.Header, h => x.Header = h); break;
            case ToggleSwitch x: HeaderFix(x, x.Header, h => x.Header = h); break;
            case ComboBox x:
                HeaderFix(x, x.Header, h => x.Header = h);
                foreach (var it in x.Items) if (it is ComboBoxItem ci && ci.Content is string cs) Fix(ci, "Content", cs, t => ci.Content = t);
                break;
            case Pivot p:
                foreach (var it in p.Items) if (it is PivotItem pi) { if (pi.Header is string hs) Fix(pi, "Header", hs, t => pi.Header = t); if (pi.Content is DependencyObject pc) Visit(pc, depth + 1, seen); }
                break;
            case SelectorBar sb:
                foreach (var it in sb.Items) Fix(it, "Text", it.Text, t => it.Text = t);
                break;
            case ContentDialog cd:
                Fix(cd, "Primary", cd.PrimaryButtonText, t => cd.PrimaryButtonText = t);
                Fix(cd, "Close", cd.CloseButtonText, t => cd.CloseButtonText = t);
                break;
            case ContentControl cc when cc.Content is string s:
                Fix(cc, "Content", s, t => cc.Content = t);
                break;
        }

        // logical children (works even when a control's template has not been applied yet, e.g. collapsed panels)
        switch (d)
        {
            case Panel pn: foreach (var c in pn.Children) Visit(c, depth + 1, seen); break;
            case Border b when b.Child is not null: Visit(b.Child, depth + 1, seen); break;
            case ContentControl cc when cc.Content is DependencyObject co: Visit(co, depth + 1, seen); break;
            case ContentDialog: break;
        }
        // realized visual children (list items, template parts)
        var n = VisualTreeHelper.GetChildrenCount(d);
        for (var i = 0; i < n; i++) Visit(VisualTreeHelper.GetChild(d, i), depth + 1, seen);
    }

    private static void HeaderFix(object owner, object? header, Action<string> set)
    {
        if (header is string h) Fix(owner, "Header", h, set);
    }

    // ---------- Turkish dictionary (English text -> Turkish) ----------
    private static readonly Dictionary<string, string> Tr = new()
    {
        // navigation / toolbar
        ["Home"] = "Ana Sayfa", ["All Games"] = "Tüm Oyunlar", ["Installed Games"] = "Kurulu Oyunlar", ["Favorites"] = "Favoriler",
        ["Recently Played"] = "Son Oynananlar", ["Most Played"] = "En Çok Oynananlar", ["Recently Added"] = "Son Eklenenler",
        ["PLATFORMS"] = "PLATFORMLAR", ["CATEGORIES"] = "KATEGORİLER", ["Library"] = "Kütüphane",
        ["Add Game"] = "Oyun Ekle", ["Scan for games"] = "Oyunları tara", ["Settings"] = "Ayarlar", ["Search games"] = "Oyun ara",
        ["Recently played"] = "Son oynananlar", ["Alphabetical"] = "Alfabetik", ["Most played"] = "En çok oynanan", ["Least played"] = "En az oynanan",
        ["Recently added"] = "Son eklenenler", ["Largest games"] = "En büyük oyunlar", ["Achievement completion"] = "Başarım tamamlanma",
        ["Carousel"] = "Karusel", ["Grid"] = "Izgara", ["List"] = "Liste",
        ["Quick switch (Ctrl+K)"] = "Hızlı geçiş (Ctrl+K)", ["Quick switch"] = "Hızlı geçiş", ["Type to find a game…"] = "Oyun bulmak için yazın…",
        ["Enter: select • Ctrl+Enter: play • Esc: close"] = "Enter: seç • Ctrl+Enter: oyna • Esc: kapat",
        // info panel
        ["PLAY"] = "OYNA", ["Game Details"] = "Oyun Detayları", ["Achievements"] = "Başarımlar", ["Installed DLCs"] = "Kurulu DLC'ler",
        ["Open Installation Folder"] = "Kurulum Klasörünü Aç", ["Toggle Favorite"] = "Favori Değiştir", ["Refresh Information"] = "Bilgileri Yenile",
        ["Edit Game"] = "Oyunu Düzenle", ["Remove from Library"] = "Kütüphaneden Kaldır",
        ["TOTAL PLAYTIME"] = "TOPLAM OYNAMA SÜRESİ", ["GAME SIZE"] = "OYUN BOYUTU", ["INSTALLED DLC"] = "KURULU DLC", ["ACHIEVEMENTS"] = "BAŞARIMLAR", ["LAST PLAYED"] = "SON OYNANMA",
        ["Add a game"] = "Oyun ekle", ["Scan now"] = "Şimdi tara",
        // details
        ["Close"] = "Kapat", ["Overview"] = "Genel Bakış", ["Sessions"] = "Oturumlar", ["LOCATION"] = "KONUM", ["Installation directory"] = "Kurulum klasörü",
        ["Executable"] = "Çalıştırılabilir dosya", ["Launch method"] = "Başlatma yöntemi",
        ["Platform artwork & logos: GameIndex shows the platform name and the game title as text. Official platform logos and game logo images are not bundled."] =
            "Platform görselleri ve logoları: GameIndex platform adını ve oyun adını yazı olarak gösterir. Resmi platform logoları ve oyun logoları pakete dahil değildir.",
        // statuses and values
        ["RUNNING"] = "ÇALIŞIYOR", ["INSTALLED"] = "KURULU", ["NOT INSTALLED"] = "KURULU DEĞİL", ["Unfavorite"] = "Favoriden çıkar", ["Favorite"] = "Favori",
        ["Not played yet"] = "Henüz oynanmadı", ["Never"] = "Hiç", ["Calculating…"] = "Hesaplanıyor…", ["Not installed"] = "Kurulu değil",
        ["Not available"] = "Mevcut değil", ["NOT AVAILABLE"] = "MEVCUT DEĞİL", ["Checking…"] = "Kontrol ediliyor…", ["Installed DLC only"] = "Yalnızca kurulu DLC'ler",
        ["Could not be read"] = "Okunamadı", ["Manual game"] = "Manuel oyun", ["Installed size on disk"] = "Diskte kapladığı alan",
        ["Installed size • updated {0}"] = "Kurulu boyut • güncelleme: {0}",
        ["Tracked by GameIndex"] = "GameIndex tarafından takip ediliyor", ["Tracked by GameIndex (no sessions yet)"] = "GameIndex tarafından takip ediliyor (henüz oturum yok)",
        ["Imported from {0}: {1} • Tracked by GameIndex since: {2}"] = "{0} üzerinden alındı: {1} • GameIndex ile sonradan takip edilen: {2}",
        ["Not exposed by {0}"] = "{0} bu bilgiyi sunmuyor", ["Imported from {0}"] = "{0} üzerinden alındı", ["No sessions recorded"] = "Kayıtlı oturum yok",
        ["{0} / {1} UNLOCKED"] = "{0} / {1} AÇILDI", ["{0}% COMPLETED"] = "%{0} TAMAMLANDI",
        ["Add a Steam Web API key in Settings"] = "Ayarlar'dan bir Steam Web API anahtarı ekleyin",
        ["No data (private profile or no achievements)"] = "Veri yok (gizli profil veya başarım yok)",
        ["Detected"] = "Algılandı", ["Not detected"] = "Algılanmadı", ["Error"] = "Hata", ["Not scanned yet"] = "Henüz taranmadı",
        ["Unlocked"] = "Açıldı", ["Locked"] = "Kilitli", ["Installed"] = "Kurulu", ["Owned, not installed"] = "Sahip, kurulu değil", ["In progress"] = "Devam ediyor",
        ["Recovered after unexpected shutdown"] = "Beklenmeyen kapanmadan sonra kurtarıldı",
        ["Owned-but-not-installed DLC is not exposed by this platform; only installed DLC is listed."] = "Sahip olunup kurulmamış DLC'leri bu platform sunmaz; yalnızca kurulu DLC'ler listelenir.",
        // status messages
        ["No games yet. GameIndex is scanning your installed platforms. You can also add a game manually or drop an EXE here."] =
            "Henüz oyun yok. GameIndex kurulu platformlarınızı tarıyor. Manuel oyun ekleyebilir veya bir EXE dosyasını buraya sürükleyebilirsiniz.",
        ["No games match this view."] = "Bu görünümde eşleşen oyun yok.",
        ["Removed {0} from the library."] = "{0} kütüphaneden kaldırıldı.", [" It will return on the next scan while it is installed."] = " Kuruluysa bir sonraki taramada geri gelir.",
        ["Refreshing {0}…"] = "{0} yenileniyor…", ["{0} updated."] = "{0} güncellendi.", ["Scanning for games…"] = "Oyunlar taranıyor…",
        ["Scan finished. {0} game(s) in library."] = "Tarama bitti. Kütüphanede {0} oyun var.",
        // dialogs
        ["Add game"] = "Oyun ekle", ["Edit game"] = "Oyunu düzenle", ["Save"] = "Kaydet", ["Cancel"] = "İptal", ["Remove"] = "Kaldır", ["Remove {0}?"] = "{0} kaldırılsın mı?",
        ["The game is removed from GameIndex (its files are not touched). Playtime history for it is deleted."] = "Oyun GameIndex'ten kaldırılır (dosyalarına dokunulmaz). Oyun süresi geçmişi silinir.",
        ["The game is removed from GameIndex (its files are not touched). It will come back on the next scan while it is still installed."] = "Oyun GameIndex'ten kaldırılır (dosyalarına dokunulmaz). Hâlâ kuruluysa bir sonraki taramada geri gelir.",
        ["Game name"] = "Oyun adı", ["Executable path"] = "Çalıştırılabilir dosya yolu", ["Launch arguments (optional)"] = "Başlatma parametreleri (isteğe bağlı)",
        ["Working directory (optional)"] = "Çalışma klasörü (isteğe bağlı)", ["Platform name (optional, e.g. itch.io)"] = "Platform adı (isteğe bağlı, örn. itch.io)",
        ["Category (optional, type to create a new one)"] = "Kategori (isteğe bağlı, yeni oluşturmak için yazın)",
        ["Custom cover image (optional)"] = "Özel kapak görseli (isteğe bağlı)", ["Custom background image (optional)"] = "Özel arka plan görseli (isteğe bağlı)",
        ["Track playtime for any process in the game's folder (for launcher EXEs that start the real game)"] = "Oyun klasöründeki herhangi bir işlem için süre takibi yap (asıl oyunu başlatan başlatıcı EXE'ler için)",
        ["Browse…"] = "Gözat…",
        ["Closing the window keeps GameIndex running in the background (tray + desktop icon). Exit completely from the tray menu or Task Manager."] = "Pencereyi kapatmak GameIndex'i arka planda çalışır bırakır (tepsi + masaüstü simgesi). Tamamen çıkmak için tepsi menüsünü veya Görev Yöneticisi'ni kullanın.",
        ["Desktop icon visibility"] = "Masaüstü simgesi görünürlüğü",
        ["Desktop only (hidden behind other apps, e.g. while watching a stream)"] = "Yalnızca masaüstünde (diğer uygulamaların arkasında kalır, örn. yayın izlerken)",
        ["Always on screen (stays on top of other apps)"] = "Her zaman ekranda (diğer uygulamaların üstünde kalır)",
        ["Desktop icon size"] = "Masaüstü simgesi boyutu", ["Desktop icon opacity (%)"] = "Masaüstü simgesi saydamlığı (%)", ["Desktop icon style"] = "Masaüstü simgesi stili",
        ["Startup sound"] = "Açılış sesi", ["Off"] = "Kapalı",
        ["Window corner roundness (0 = square)"] = "Pencere köşe yuvarlaklığı (0 = köşeli)",
        ["Up to about 9 uses Windows' smooth rounding; larger values clip the window with a rounded shape (the edge is slightly less smooth)."] = "9'a kadar Windows'un pürüzsüz yuvarlaması kullanılır; daha büyük değerlerde pencere yuvarlak bir şekle kırpılır (kenar biraz daha az pürüzsüz olur).",
        ["Artwork scaling"] = "Görsel ölçekleme", ["Fit: show the whole image, keep its aspect ratio"] = "Sığdır: görselin tamamını, en boy oranını koruyarak göster",
        ["Fill: crop the image to fill the space"] = "Doldur: görseli alanı dolduracak şekilde kırp", ["Estimated from the game's DLC folders"] = "Oyunun DLC klasörlerinden tahmin edildi", ["Collapse menu"] = "Menüyü daralt", ["Expand menu"] = "Menüyü genişlet",
        ["The floating desktop icon could not be created (see Logs)."] = "Yüzen masaüstü simgesi oluşturulamadı (Logs klasörüne bakın).",
        ["Hide the desktop icon while GameIndex is open"] = "GameIndex açıkken masaüstü simgesini gizle", ["Hide desktop icon"] = "Masaüstü simgesini gizle", ["Show desktop icon"] = "Masaüstü simgesini göster", ["Exit GameIndex"] = "GameIndex'ten çık",
        ["Hotkey {0} is already used by another program."] = "{0} kısayolu başka bir program tarafından kullanılıyor.",
        ["DESKTOP & SHORTCUTS"] = "MASAÜSTÜ VE KISAYOLLAR",
        ["Global hotkey: show / hide GameIndex from anywhere"] = "Genel kısayol: GameIndex'i her yerden göster / gizle", ["Hotkey"] = "Kısayol",
        ["Floating desktop icon (drag it anywhere, click to open GameIndex)"] = "Yüzen masaüstü simgesi (istediğiniz yere sürükleyin, GameIndex'i açmak için tıklayın)",
        ["Reset icon position"] = "Simge konumunu sıfırla", ["Sounds (startup, open and close)"] = "Sesler (açılış, açma ve kapama)", ["Play startup sound"] = "Açılış sesini çal", ["Recent games"] = "Son oynanan oyunlar", ["Favorite games"] = "Favori oyunlar", ["Open GameIndex"] = "GameIndex'i aç",
        ["Taskbar jump list: show recent and favorite games when right-clicking the taskbar icon"] = "Görev çubuğu atlama listesi: görev çubuğu simgesine sağ tıklayınca son oynananları ve favorileri göster", ["That executable does not exist."] = "Bu çalıştırılabilir dosya mevcut değil.",
        ["This game launches through its platform (login, DRM, cloud saves). Title, category and artwork edits apply; the executable is only used for games without a platform launch method."] =
            "Bu oyun platformu üzerinden başlatılır (giriş, DRM, bulut kayıtları). Başlık, kategori ve görsel düzenlemeleri geçerlidir; EXE yalnızca platform başlatma yöntemi olmayan oyunlarda kullanılır.",
        // settings
        ["LANGUAGE"] = "DİL", ["Language"] = "Dil", ["English"] = "English", ["Follow Windows"] = "Windows'u izle",
        ["APPEARANCE"] = "GÖRÜNÜM", ["Theme"] = "Tema", ["Dark"] = "Koyu", ["Light"] = "Açık",
        ["Accent color (hex)"] = "Vurgu rengi (hex)", ["Pick color…"] = "Renk seç…",
        ["Enable transparency effects (turn off for solid, fully opaque windows)"] = "Şeffaflık efektlerini etkinleştir (katı, tamamen opak pencereler için kapatın)",
        ["Settings / Details window opacity"] = "Ayarlar / Detay penceresi opaklığı",
        ["Panel transparency (higher = more opaque)"] = "Panel şeffaflığı (yüksek = daha opak)", ["Window material (system blur)"] = "Pencere malzemesi (sistem bulanıklığı)",
        ["Mica"] = "Mica", ["Mica Alt"] = "Mica Alt", ["Acrylic (real blur-behind)"] = "Acrylic (gerçek arka plan bulanıklığı)", ["None (solid)"] = "Yok (düz renk)",
        ["Background artwork dimming"] = "Arka plan görseli karartma", ["Custom fallback background image"] = "Özel yedek arka plan görseli",
        ["Shown when no game artwork exists"] = "Oyun görseli yoksa gösterilir", ["Animations"] = "Animasyonlar", ["Animation speed (%)"] = "Animasyon hızı (%)",
        ["Compact sidebar (icons only until expanded)"] = "Kompakt kenar çubuğu (genişletilene kadar yalnızca simgeler)",
        ["Note: Windows exposes no adjustable blur radius for Mica/Acrylic, so blur strength is chosen via Window material plus the transparency slider."] =
            "Not: Windows, Mica/Acrylic için ayarlanabilir bulanıklık yarıçapı sunmaz; bulanıklık gücü pencere malzemesi ve şeffaflık kaydırıcısıyla belirlenir.",
        ["APPLICATION"] = "UYGULAMA", ["Start with Windows (current user, no admin rights)"] = "Windows ile başlat (geçerli kullanıcı, yönetici hakkı gerekmez)",
        ["Minimize to system tray (closing keeps playtime tracking running)"] = "Sistem tepsisine küçült (kapatınca süre takibi çalışmaya devam eder)",
        ["Automatic game detection"] = "Otomatik oyun algılama", ["Scan interval (minutes)"] = "Tarama aralığı (dakika)", ["Default library view"] = "Varsayılan kütüphane görünümü",
        ["Automatic metadata refresh (DLC, achievements, platform playtime)"] = "Otomatik bilgi yenileme (DLC, başarımlar, platform oynama süresi)",
        ["SCAN DIRECTORIES (standalone games)"] = "TARAMA KLASÖRLERİ (bağımsız oyunlar)",
        ["GameIndex never scans whole drives. Add folders that contain one sub folder per game, e.g. D:\\Games."] = "GameIndex tüm diskleri taramaz. Her oyun için bir alt klasör içeren klasörler ekleyin, örn. D:\\Games.",
        ["Add folder…"] = "Klasör ekle…", ["STEAM (achievements & playtime)"] = "STEAM (başarımlar ve oynama süresi)",
        ["Create a key at steamcommunity.com/dev/apikey. Your Steam profile's Game details must be Public. The key is stored encrypted with Windows DPAPI (tied to this Windows user) and is never written to the repository or logs. Passwords are never requested or stored."] =
            "steamcommunity.com/dev/apikey adresinden bir anahtar oluşturun. Steam profilinizdeki Oyun ayrıntıları Herkese Açık olmalıdır. Anahtar Windows DPAPI ile şifrelenerek saklanır (bu Windows kullanıcısına bağlıdır), depoya veya günlüklere yazılmaz. Parola istenmez ve saklanmaz.",
        ["SteamID64 (optional, auto-detected from the local Steam install)"] = "SteamID64 (isteğe bağlı, yerel Steam kurulumundan otomatik algılanır)",
        ["Steam Web API key"] = "Steam Web API anahtarı", ["Save key"] = "Anahtarı kaydet", ["Remove key"] = "Anahtarı sil",
        ["No API key saved"] = "Kayıtlı API anahtarı yok", ["API key saved (encrypted with Windows DPAPI)"] = "API anahtarı kaydedildi (Windows DPAPI ile şifreli)",
        ["PLATFORM INTEGRATIONS"] = "PLATFORM ENTEGRASYONLARI", ["Enabled"] = "Etkin", ["Disabled"] = "Devre dışı",
        ["DATA"] = "VERİ", ["Backup database"] = "Veritabanını yedekle", ["Restore database…"] = "Veritabanını geri yükle…", ["Export library…"] = "Kütüphaneyi dışa aktar…",
        ["Import library…"] = "Kütüphaneyi içe aktar…", ["Clear cache"] = "Önbelleği temizle", ["Backup location (empty = Data\\Backups)"] = "Yedek konumu (boş = Data\\Backups)",
        ["PORTABLE MODE"] = "TAŞINABİLİR MOD", ["Open data folder"] = "Veri klasörünü aç", ["Data directory: "] = "Veri klasörü: ", ["Database: "] = "Veritabanı: ", ["Artwork: "] = "Görseller: ",
        ["Portable mode: all data is stored next to GameIndex.exe."] = "Taşınabilir mod: tüm veriler GameIndex.exe'nin yanında saklanır.",
        ["Steam API key saved."] = "Steam API anahtarı kaydedildi.", ["Steam API key removed."] = "Steam API anahtarı silindi."
    };
}
