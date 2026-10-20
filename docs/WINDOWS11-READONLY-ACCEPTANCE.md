# Windows 11: sichere MyTagSync-Endabnahme (noch nicht freigegeben)

**Zielplattform:** Windows 11 x64 auf dem tatsächlichen DJ-Laptop; rekordbox 6/7 mit XDJ-RX3, foobar2000 und foobar-Bridge. **Windows 10 ist nicht die Zielplattform dieses Benutzers.**

Diese Anleitung ist ein **Abnahmevertrag**, keine Freigabe zum produktiven Schreiben. Insbesondere führt die aktuelle öffentliche Build-Pipeline zwar vollständige Win-x64-Tests aus, GitHub `windows-latest` (Windows Server) und `windows-2022` (Windows Server 2022) sind **keine** echte Windows-11-Client-Qualifikation.

## Voraussetzungen für die einzige abschließende Laptop-Prüfung

1. Exakten `main`-Commit und dazu **FAST=SUCCESS**, **FULL=SUCCESS** einschließlich `qualify-portable-candidate` und `older-windows-api-smoke` dokumentieren. Ein späterer Commit erfordert eine neue SHA-gebundene Qualifikation.
2. **Rechte-/Verteilungsfreigabe** sowie einen sicheren, überprüfbaren Übergabeweg für *ein* Kandidatenpaket abschließen (siehe [Distribution gate](DISTRIBUTION-GATE.md)). `FULL`-Upload und `PROMOTE-RELEASE` bleiben bis dahin gesperrt. Ein nur im temporären GitHub-Runner gebautes ZIP steht nicht zum Download bereit.
3. Das tatsächlich erhaltene Kandidaten-ZIP anhand seines eigenen **inneren ZIP-SHA-256** und `SOURCE_SHA.txt`/`candidate-manifest.json` kontrollieren. Der äußere GitHub-Transport-ZIP-Hash ist ein **anderes** Objekt. Keinen Hash eines älteren Commits wiederverwenden.
4. Auf dem Windows-11-Gerät im nichtadministrativen Benutzerkontext starten, ohne in produktive Dateien zu kopieren oder Windows-Rechte zu umgehen. Die UI ist nicht als Administrator auszuführen.
5. rekordbox, rekordboxAgent und rbxport vor jeglichem möglichen Schreib-/Restore-Vorgang schließen. Die hier beschriebene Phase ist ausschließlich **read-only**; `Apply`, `Restore` und jede Eingabe einer Schreibbestätigung sind ausdrücklich **nicht** Bestandteil dieses Tests.

## Einmalige read-only Prüfung auf dem DJ-Laptop

| Bereich | Konkretes PASS-/FAIL-Kriterium |
| --- | --- |
| Betriebssystem und Start | Windows 11 x64 (Edition/Build lokal feststellen), portables Programm startet ohne Installation/Adminrechte, fenstergerechtes Verhalten bei DPI/Skalierung |
| UI-Persistenz | Größe, Position, Spaltenbreiten und Filter bleiben über einen Neustart erhalten; Icons, Tooltips und Tastaturbedienung sind nutzbar; erste Tabellenspalten bleiben beim horizontalen Scrollen zugänglich; Touchpad scrollt sinnvoll |
| foobar-Quelle | Der echte Bridge-Snapshot wird automatisch gefunden, `Inspect selected` zeigt nachvollziehbare Quelle/Generation; bestehende Musik/Tags/Cues bleiben unverändert |
| rekordbox-Ziel | `Find libraries` entdeckt die tatsächliche `master.db`; SQLCipher und DBVersion/DBID werden **nur lesend** validiert; ein Alias oder eine Kopie darf nicht fälschlich die reale Datenbank identifizieren |
| Pfadzuordnung | Quell-/Zielwurzeln (ggf. Laufwerks-/SUBST-/UNC-Aliase) werden nachvollziehbar vorgeschlagen; nur unabhängige native VolumeSerial/FileIndex-Identität gilt als physische Bestätigung; ein Dateiname allein genügt niemals |
| Umfang/Ausschlüsse | ContentIDs und Grund jedes Ausschlusses sichtbar; virtuelle CUE-Subsongs nie physische Schreibziele; Duplikate, Kopien und fehlende Dateien bleiben ausgeschlossen; Abweichungen in der echten Bibliothek melden, nicht automatisch korrigieren |
| Tag-Vorschau | `DATE`→Year, `GENRE`→Genre, Mehrfachwerte, optionale Regex-/Filterregeln und bestehende manuelle MyTags werden korrekt differenziert; Apply-Vorschlag ohne Berechtigung verändert nichts |
| No-write-Sicherheit | Suche, Discovery, Inspection, Validate, Path Scan, Preview, Abbruch und Fenster-Schließen dürfen weder `master.db` noch Audiodateien/physische Tags/embedded CUEs verändern. Write-Aktionen bleiben gesperrt, bis später *separat* freigegeben |
| XDJ-RX3 | Nach Beendigung der PC-Prüfung rekordbox regulär starten und Sammlung, My Tags, Cue-Daten und RX3-Browsing unverändert wiederfinden; keine RX3-Firmware-/Geräteänderungen |

Bei einem FAIL werden **nur lokale, anonymisierte Ergebnisse** weitergegeben (Schritt, erwarteter/tatsächlicher Zustand, Windows-Version, App-Source-SHA); **keine** privaten `master.db`, Musikdateien, ContentIDs, Tokens, Pfade oder vollständigen Debug-Dumps in GitHub-Issues veröffentlichen. Die vollständige Testreihe soll möglichst einmalig und gebündelt stattfinden, nicht in vielen Mini-Runden.

**Grenze dieser Abnahme:** Eine erfolgreiche read-only-Abnahme erteilt **keine** Genehmigung für `Apply` oder `Restore`. Für jede spätere reale Transaktion sind nachvollziehbare Vorschau, neu geprüfte Identität, separates Rolling Backup und eine eigene ausdrückliche Zustimmung notwendig.

## Bisheriger softwareseitiger Nachweis, nicht Client-Abnahme

- Produkt-`main`: `71939b9d91258f6bb40ef7509e7744a8c7c8d5e7`
- [FAST #39446071413](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39446071413) – SUCCESS, einschließlich nativer foobar-Bridge-Publisher→verschlüsselter SQLCipher-Preview-Test
- [FULL #39446568359](https://github.com/lxsdd/rekordbox-mytag-sync/actions/runs/39446568359) – SUCCESS, inklusive Win-x64 ZIP-Determinismus und ältere Windows-Server-API-Probe
- Innerer, nur temporär im Runner erzeugter ZIP-SHA-256: `bb82d68ff36300efee274017ac09af347c2e43843d53bc6199c6b43a860d2cf0`
- GitHub-Download-Artefakte dieses automatischen FULL-Laufs: **0**, wegen beabsichtigter Verteilungssperre. Kein Nutzer-Download oder Windows-11-Hosttest aus diesen Ergebnissen ableiten.

Diese konkreten Werte sind ein historischer Checkpoint; bei einer neueren Version müssen sämtliche SHA- und Runangaben aktualisiert werden.
