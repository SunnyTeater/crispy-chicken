# Jellyfin Codec Converter

Plugin für Jellyfin 12, das die Videospur von Filmen und Folgen mit Jellyfins eigenem ffmpeg in einen
anderen Codec umwandelt (H.265/HEVC, AV1 oder H.264).

## Was es tut

- **Auswahl:** nach aktuellem Codec filtern, dann Filme, ganze Serien, einzelne Staffeln oder einzelne Folgen ankreuzen.
- **Ziel und Verlust:** Zielcodec, Encoder (CPU oder NVENC / Quick Sync / VAAPI) und Verlust-Stufe bzw. Qualitätswert (CRF) wählen.
- **Schätzung:** zeigt vor dem Start grob, wie groß die Auswahl danach etwa ist.
- **Warteschlange:** immer ein Job gleichzeitig, und nur solange weniger Streams aktiv sind als die Stream-Grenze
  (Standard 2, also höchstens ein laufender Stream). Wird die Grenze erreicht, hält der laufende Job an und
  macht später an derselben Stelle weiter.
- **Original:** bleibt an seinem Platz, bis die neue Datei fertig und geprüft ist (Codec, Laufzeit, Anzahl der
  Ton- und Untertitelspuren). Danach wandert es in den Sicherungsordner und wird dort nach 14 Tagen gelöscht (einstellbar).
- **Namen:** Ordner und Dateiname bleiben gleich, das Jellyfin-Namensschema bleibt also erhalten. Die Dateiendung
  ändert sich nur, wenn der alte Container den Zielcodec nicht aufnehmen kann (z. B. `.avi` → `.mkv`).
- **Unverändert übernommen:** alle Tonspuren, Untertitel, Kapitel und Metadaten.

## Installieren

**Über den Jellyfin-Katalog:** Dashboard → Plugins → Repositories verwalten, neues Repository mit dieser URL anlegen:

    https://raw.githubusercontent.com/SunnyTeater/crispy-chicken/main/manifest.json

Danach „Codec Converter“ im Katalog installieren und Jellyfin neu starten.

**Von Hand:** `codec-converter_1.0.0.0.zip` aus den Releases laden, in den Plugin-Ordner entpacken und Jellyfin neu starten:

    sudo mkdir -p /var/lib/jellyfin/plugins/CodecConverter_1.0.0.0
    sudo unzip codec-converter_1.0.0.0.zip -d /var/lib/jellyfin/plugins/CodecConverter_1.0.0.0
    sudo chown -R jellyfin: /var/lib/jellyfin/plugins/CodecConverter_1.0.0.0
    sudo systemctl restart jellyfin

Danach: Dashboard → Codec Converter. Auf dem Server wird nichts zusätzlich installiert, das Plugin benutzt
das ffmpeg, das Jellyfin selbst verwendet.

## Voraussetzungen

- **Jellyfin 12.0 oder neuer.** Getestet mit 12.0 und 12.4 auf Ubuntu 24.04 (x86-64); Docker, Windows, macOS und ARM sind ungetestet.
- **Jellyfins ffmpeg** mit dem gewünschten Encoder (`libx265`, `libsvtav1`, `libx264`). Das Paket `jellyfin-ffmpeg` enthält alle drei.
- **Schreibrechte des Jellyfin-Dienstes in den Medienordnern.** Er legt dort die neue Datei an und verschiebt das Original. Prüfen und vergeben:

      sudo -u jellyfin find /pfad/zur/Bibliothek -type d ! -writable
      sudo setfacl -R -m u:jellyfin:rwX /pfad/zur/Bibliothek
      sudo setfacl -R -d -m u:jellyfin:rwX /pfad/zur/Bibliothek

- **Freier Speicher:** im Medienordner etwa die Größe der neuen Datei; liegt der Sicherungsordner auf einem anderen
  Datenträger, dort die Größe des Originals plus 2 GB Reserve. Das Plugin prüft beides vor jedem Job.
- Der **Sicherungsordner** liegt standardmäßig im Jellyfin-Datenordner (`…/data/codec-converter/backup`), also meist
  auf der Systemplatte. Für große Bibliotheken besser einen Ordner auf der Medienplatte einstellen – aber außerhalb
  der Bibliotheken, sonst nimmt Jellyfin die Originale als Duplikate auf.
- **Hardware-Encoder** brauchen zusätzlich GPU, Treiber und Gerätezugriff für den Benutzer `jellyfin` (ungetestet).

## Gut zu wissen

- Nach einem fertigen Job dauert es etwa eine Minute, bis Jellyfin die neue Datei eingelesen hat. Solange ist sie
  in der Auswahl gesperrt.
- Die Größenschätzung ist grob. Sie rechnet aus Auflösung, Bildrate, Laufzeit, Quellbitrate und Qualitätswert und
  kann je nach Bildinhalt (Filmkorn, viel Bewegung) deutlich danebenliegen.
- Hardware-Encoder werden angeboten, sobald Jellyfins ffmpeg sie enthält – das heißt nicht, dass die passende
  GPU vorhanden ist. Ohne GPU schlägt der Job fehl, das Original bleibt unangetastet.
- Dolby-Vision-Dateien werden nicht umgewandelt (die Dolby-Vision-Daten gingen verloren). HDR10 bleibt mit
  dem CPU-Encoder x265 erhalten.
- Eingebettete Cover-Bilder in MP4-Dateien werden nicht übernommen.
- Wird genau die umgewandelte Datei gerade abgespielt, wartet der Austausch, bis die Wiedergabe endet.
- Meldet ffmpeg 60 Minuten lang keinen Fortschritt, wird der Job abgebrochen; das Original bleibt erhalten.
- Unter Windows kann ein laufender Job nicht angehalten werden; er läuft zu Ende, neue Jobs warten.
- Ein Neustart oder Absturz des Servers ist unkritisch: unterbrochene Jobs beginnen danach von vorn, halbfertige
  Dateien werden entfernt.

## Bauen

    ./build.sh

Braucht das .NET 10 SDK, aber nur auf dem Rechner, der baut.
