# Jellyfin Codec Converter

Plugin für Jellyfin 12, das die Videospur von Filmen und Folgen mit Jellyfins eigenem ffmpeg in einen
anderen Codec umwandelt (H.265/HEVC, AV1 oder H.264).

## Was es tut

- **Auswahl:** nach aktuellem Codec filtern, dann Filme, ganze Serien, einzelne Staffeln oder einzelne Folgen ankreuzen.
- **Ziel und Verlust:** Zielcodec, Encoder (CPU oder NVENC / Quick Sync / VAAPI) und Verlust-Stufe bzw. Qualitätswert (CRF) wählen.
- **Kompression der Quelle:** zeigt Videobitrate und, soweit in der Datei vermerkt, den CRF-Wert, mit dem sie erzeugt wurde.
- **Schätzung:** zeigt vor dem Start grob, wie groß die Auswahl danach etwa ist und wie lange die Umwandlung rechnet.
- **Restzeit:** Beim laufenden Job aus den Fortschrittsdaten von ffmpeg (umgewandelte Laufzeit und dafür gebrauchte
  Rechenzeit), bei wartenden Jobs hochgerechnet aus früheren Jobs mit demselben Encoder. Pausen wegen aktiver Streams
  zählen nicht mit.
- **Live-Protokoll:** zeigt auf der Plugin-Seite den ffmpeg-Aufruf, alle paar Sekunden den Fortschritt, Pausen,
  Prüfung und Freigabe.
- **Warteschlange:** immer ein Job gleichzeitig, und nur solange weniger Streams aktiv sind als die Stream-Grenze
  (Standard 2, also höchstens ein laufender Stream). Wird die Grenze erreicht, hält der laufende Job an und
  macht später an derselben Stelle weiter.
- **Ablage:** Die neue Datei wird neben das Original gelegt und trägt den neuen Codec im Namen. Steht der alte
  Codec im Dateinamen, wird er ersetzt (`Film (2020) - 1080p x264.mkv` → `Film (2020) - 1080p x265.mkv`), sonst
  wird er als Versionsname angehängt (`Film (2020).mkv` → `Film (2020) - HEVC.mkv`). In Jellyfin erscheinen beide
  als auswählbare Versionen.
- **Freigabe:** Erst mit „Freigeben“ wird das Original gelöscht, mit „Verwerfen“ die neue Datei. Ohne Entscheidung
  wird nach 14 Tagen automatisch freigegeben (einstellbar, 0 = nie).
- **Beschädigte Quellen:** Liefert die Videospur kein lesbares Bild, scheitert der Job mit einer Klartext-Meldung.
  Meldet ffmpeg nur stellenweise Lesefehler, wird trotzdem umgewandelt, aber mit Warnhinweis und ohne automatische Freigabe.
- **Geprüft:** Vor dem Ablegen und noch einmal vor dem Löschen des Originals wird die neue Datei geprüft (Codec,
  Laufzeit, Anzahl der Ton- und Untertitelspuren).
- **Unverändert übernommen:** alle Tonspuren, Untertitel, Kapitel und Metadaten. Externe Untertiteldateien werden
  für die neue Datei mitkopiert, der Gesehen-Status wird bei der Freigabe übertragen.

## Installieren

**Über den Jellyfin-Katalog:** Dashboard → Plugins → Repositories verwalten, neues Repository mit dieser URL anlegen:

    https://raw.githubusercontent.com/SunnyTeater/crispy-chicken/main/manifest.json

Danach „Codec Converter“ im Katalog installieren und Jellyfin neu starten.

**Von Hand:** `codec-converter_1.2.4.0.zip` aus den Releases laden, in den Plugin-Ordner entpacken und Jellyfin neu starten:

    sudo mkdir -p /var/lib/jellyfin/plugins/CodecConverter_1.2.4.0
    sudo unzip codec-converter_1.2.4.0.zip -d /var/lib/jellyfin/plugins/CodecConverter_1.2.4.0
    sudo chown -R jellyfin: /var/lib/jellyfin/plugins/CodecConverter_1.2.4.0
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

- **Freier Speicher im Medienordner:** Original und neue Datei liegen bis zur Freigabe nebeneinander. Das Plugin
  prüft vor jedem Job, ob die geschätzte neue Datei Platz hat.
- **Hardware-Encoder** brauchen zusätzlich GPU, Treiber und Gerätezugriff für den Benutzer `jellyfin` (ungetestet).

## Gut zu wissen

- Nach einem fertigen Job dauert es etwa eine Minute, bis Jellyfin die neue Datei als Version anzeigt.
- Bei Filmen erkennt Jellyfin Versionen nur, wenn der Dateiname mit dem Ordnernamen beginnt und der Zusatz mit
  ` - `, Punkt, Unterstrich oder Klammern abgetrennt ist. Sonst (z. B. `Film.2003.1080p.x264-GRP.mkv`) erscheinen
  Original und neue Datei bis zur Freigabe als zwei getrennte Filme. Bei Folgen klappt es mit jeder Schreibweise.
- Welche Version Jellyfin zuerst abspielt, bestimmt Jellyfin (alphabetisch bzw. nach Auflösung). Zum Prüfen die
  neue Version im Player auswählen.
- Wird die Datei gerade abgespielt, lassen sich Freigeben und Verwerfen nicht ausführen.
- Originale, die Version 1.0 in den Sicherungsordner verschoben hat, werden dort weiterhin nach Fristablauf gelöscht.
- Die Größenschätzung rechnet mit dem CRF-Wert der Quelle, wenn er in der Datei steht (nur x264/x265 schreiben ihn hinein),
  sonst mit Bitrate, Auflösung und Bildrate. Gemessen an 90 Umwandlungen kurzer Testclips lag sie im Mittel um Faktor 1,3
  (mit CRF-Wert) bzw. 1,6 (nur Bitrate) daneben. Ab drei eigenen Umwandlungen je Encoder gleicht sie sich mit deren Ergebnissen ab.
  Die neue Datei kann größer werden als das Original; die Schätzung zeigt das an.
- Mit NVENC laufen Dekodieren und Kodieren auf der Grafikkarte. Kann die Karte eine Quelle nicht dekodieren,
  folgt automatisch ein zweiter Versuch, bei dem die CPU dekodiert.
- Hardware-Encoder werden angeboten, sobald Jellyfins ffmpeg sie enthält – das heißt nicht, dass die passende
  GPU vorhanden ist. Ohne GPU schlägt der Job fehl, das Original bleibt unangetastet.
- Dolby-Vision-Dateien werden nicht umgewandelt (die Dolby-Vision-Daten gingen verloren). HDR10 bleibt mit
  dem CPU-Encoder x265 erhalten.
- Eingebettete Cover-Bilder in MP4-Dateien werden nicht übernommen.
- Meldet ffmpeg 60 Minuten lang keinen Fortschritt, wird der Job abgebrochen; das Original bleibt erhalten.
- Unter Windows kann ein laufender Job nicht angehalten werden; er läuft zu Ende, neue Jobs warten.
- Ein Neustart oder Absturz des Servers ist unkritisch: unterbrochene Jobs beginnen danach von vorn, halbfertige
  Dateien werden entfernt, wartende Freigaben bleiben erhalten.

## Bauen

    ./build.sh

Braucht das .NET 10 SDK, aber nur auf dem Rechner, der baut.
